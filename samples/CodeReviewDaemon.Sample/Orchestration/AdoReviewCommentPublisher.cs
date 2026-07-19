using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Real <see cref="IReviewCommentPublisher"/> over the Azure DevOps pull-request <c>threads</c> API. ADO
/// has no flat issue-comment list like GitHub, so a review comment is posted as a single-comment thread:
/// <c>POST …/pullRequests/{pr}/threads</c> to post and <c>GET …/pullRequests/{pr}/threads</c> to scan.
/// Every posted comment carries the hidden <see cref="IdempotencyMarker"/>, so
/// <see cref="FindPostedCommentAsync"/> can recognize a thread a crashed prior attempt already posted and
/// avoid a duplicate (the §11 exactly-once backstop). ADO authenticates with HTTP Basic carrying the
/// token in the password field. Registered only when <c>EnableAdoProvider</c> is set.
/// </summary>
internal sealed class AdoReviewCommentPublisher : IReviewCommentPublisher
{
    private const string BaseUrl = "https://dev.azure.com";
    private const string ApiVersion = "7.1";

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<AdoReviewCommentPublisher> _logger;

    public AdoReviewCommentPublisher(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<AdoReviewCommentPublisher> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Provider => "ado";

    public async Task<PostedComment?> FindPostedCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var request = await BuildRequestAsync(
            HttpMethod.Get, ThreadsUrl(target), SandboxOperation.ReadProviderMetadata, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var thread in document.RootElement.GetProperty("value").EnumerateArray())
        {
            if (!thread.TryGetProperty("comments", out var comments))
            {
                continue;
            }

            foreach (var comment in comments.EnumerateArray())
            {
                var content = comment.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (IdempotencyMarker.Matches(content, idempotencyKey))
                {
                    return new PostedComment(thread.GetProperty("id").GetRawText());
                }
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
            HttpMethod.Post, ThreadsUrl(target), SandboxOperation.PostReviewComment, cancellationToken);
        request.Content = JsonContent.Create(
            new
            {
                comments = new[] { new { content = IdempotencyMarker.Embed(body, idempotencyKey), commentType = 1 } },
                status = 1,
            });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var id = document.RootElement.GetProperty("id").GetRawText();
        _logger.LogInformation("Posted ADO review thread {ThreadId} on PR {PrId}.", id, target.PrId);
        return new PostedComment(id);
    }

    private static string ThreadsUrl(ReviewCommentTarget target) =>
        $"{BaseUrl}/{target.Repo.OrgOrOwner}/{target.Repo.Project}/_apis/git/repositories/{target.Repo.RepoName}"
        + $"/pullRequests/{target.PrId}/threads?api-version={ApiVersion}";

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method, string url, SandboxOperation operation, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url).WithOperation(operation);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
}
