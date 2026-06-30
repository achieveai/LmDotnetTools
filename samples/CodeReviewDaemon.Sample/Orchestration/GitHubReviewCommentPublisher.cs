using System.Net.Http.Headers;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Real <see cref="IReviewCommentPublisher"/> over the GitHub issue-comments API (a PR is an issue for
/// commenting): <c>POST /repos/{owner}/{repo}/issues/{pr}/comments</c> to post and
/// <c>GET …/comments?per_page=100</c> to scan. Every posted body carries the hidden
/// <see cref="IdempotencyMarker"/>, so <see cref="FindPostedCommentAsync"/> can recognize a comment a
/// crashed prior attempt already posted and avoid a duplicate (the §11 exactly-once backstop). Posting
/// is gated upstream by <c>EnableCommentPosting</c>; this type only performs the call when asked.
/// </summary>
internal sealed class GitHubReviewCommentPublisher : IReviewCommentPublisher
{
    private const string BaseUrl = "https://api.github.com";
    private const string UserAgent = "LmDotnetTools-CodeReviewDaemon";

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<GitHubReviewCommentPublisher> _logger;

    public GitHubReviewCommentPublisher(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<GitHubReviewCommentPublisher> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Provider => "github";

    public async Task<PostedComment?> FindPostedCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var url = $"{CommentsUrl(target)}?per_page=100";
        using var request = await BuildRequestAsync(
            HttpMethod.Get, url, SandboxOperation.ReadProviderMetadata, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var comment in document.RootElement.EnumerateArray())
        {
            var body = comment.TryGetProperty("body", out var b) ? b.GetString() : null;
            if (IdempotencyMarker.Matches(body, idempotencyKey))
            {
                return new PostedComment(comment.GetProperty("id").GetRawText());
            }
        }

        return null;
    }

    public async Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var request = await BuildRequestAsync(
            HttpMethod.Post, CommentsUrl(target), SandboxOperation.PostReviewComment, cancellationToken);
        request.Content = JsonContent.Create(new { body = IdempotencyMarker.Embed(body, idempotencyKey) });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var id = document.RootElement.GetProperty("id").GetRawText();
        _logger.LogInformation("Posted GitHub review comment {CommentId} on PR {PrId}.", id, target.PrId);
        return new PostedComment(id);
    }

    private static string CommentsUrl(ReviewCommentTarget target) =>
        $"{BaseUrl}/repos/{target.Repo.OrgOrOwner}/{target.Repo.RepoName}/issues/{target.PrId}/comments";

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method, string url, SandboxOperation operation, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url).WithOperation(operation);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }
}
