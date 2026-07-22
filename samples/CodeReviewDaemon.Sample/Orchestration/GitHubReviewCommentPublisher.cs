using System.Globalization;
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

    /// <summary>Cap on inline-comment pages fetched when listing existing findings (100 per page).</summary>
    private const int MaxListPages = 5;

    /// <summary>Per-comment body cap when listing existing findings — enough to recognize a duplicate.</summary>
    private const int MaxBodyChars = 280;

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

    public async Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        var results = new List<ExistingReviewComment>();
        var pullsBase = $"{BaseUrl}/repos/{target.Repo.OrgOrOwner}/{target.Repo.RepoName}/pulls/{target.PrId}";

        // Inline review comments — the actual per-line findings. Paginated (100/page), bounded by MaxListPages.
        for (var page = 1; page <= MaxListPages; page++)
        {
            var count = 0;
            await foreach (var comment in EnumerateAsync($"{pullsBase}/comments?per_page=100&page={page}", cancellationToken))
            {
                count++;
                var body = GetString(comment, "body");
                if (string.IsNullOrWhiteSpace(body))
                {
                    continue;
                }

                results.Add(new ExistingReviewComment(GetString(comment, "path"), LineOf(comment), Trim(body), AuthorOf(comment)));
            }

            if (count < 100)
            {
                break; // last page reached
            }
        }

        // Review-level summaries (one page is plenty — the top-level "Reviewed PR X…" bodies).
        await foreach (var review in EnumerateAsync($"{pullsBase}/reviews?per_page=100", cancellationToken))
        {
            var body = GetString(review, "body");
            if (!string.IsNullOrWhiteSpace(body))
            {
                results.Add(new ExistingReviewComment(null, null, Trim(body), AuthorOf(review)));
            }
        }

        return results;
    }

    private async IAsyncEnumerable<JsonElement> EnumerateAsync(
        string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = await BuildRequestAsync(
            HttpMethod.Get, url, SandboxOperation.ReadProviderMetadata, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            yield return element;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() : null;

    private static string? LineOf(JsonElement comment) =>
        comment.TryGetProperty("line", out var l) && l.ValueKind is JsonValueKind.Number
            ? l.GetInt32().ToString(CultureInfo.InvariantCulture)
            : comment.TryGetProperty("original_line", out var ol) && ol.ValueKind is JsonValueKind.Number
                ? ol.GetInt32().ToString(CultureInfo.InvariantCulture)
                : null;

    private static string? AuthorOf(JsonElement element) =>
        element.TryGetProperty("user", out var u) && u.ValueKind is JsonValueKind.Object ? GetString(u, "login") : null;

    private static string Trim(string body)
    {
        var oneLine = body.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaxBodyChars ? oneLine : oneLine[..MaxBodyChars] + "…";
    }

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
