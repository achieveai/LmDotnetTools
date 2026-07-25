using System.Globalization;
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

    /// <summary>Per-comment content cap when listing existing findings — enough to recognize a duplicate.</summary>
    private const int MaxBodyChars = 280;

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

    public async Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        using var request = await BuildRequestAsync(
            HttpMethod.Get, ThreadsUrl(target), SandboxOperation.ReadProviderMetadata, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var results = new List<ExistingReviewComment>();
        foreach (var thread in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var (path, line) = ThreadLocation(thread);
            var isActive = ThreadIsActive(thread);
            var threadId = thread.TryGetProperty("id", out var tid) ? tid.GetRawText() : null;
            if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (var comment in comments.EnumerateArray())
            {
                var content = comment.TryGetProperty("content", out var c) && c.ValueKind is JsonValueKind.String
                    ? c.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                results.Add(new ExistingReviewComment(
                    path, line, Trim(content), AuthorOf(comment), isActive, PublishedAtOf(comment), threadId));
            }
        }

        return results;
    }

    /// <summary>Reads the comment's <c>publishedDate</c> (ISO-8601) — used to order past vs. new comments.</summary>
    private static DateTimeOffset? PublishedAtOf(JsonElement comment) =>
        comment.TryGetProperty("publishedDate", out var p) && p.ValueKind is JsonValueKind.String
            && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;

    /// <summary>
    /// True when the thread is still OPEN — status <c>active</c> or <c>pending</c> (or absent/unknown, which
    /// we treat as active so a possibly-open finding is never re-posted). <c>fixed</c>/<c>closed</c>/
    /// <c>wontFix</c>/<c>byDesign</c> mean the thread was acted on, so a recurring issue there MAY be re-raised.
    /// ADO returns <c>status</c> as a string; older payloads use the numeric enum (1=active, 6=pending).
    /// </summary>
    private static bool ThreadIsActive(JsonElement thread)
    {
        if (!thread.TryGetProperty("status", out var s))
        {
            return true;
        }

        return s.ValueKind switch
        {
            JsonValueKind.String => s.GetString() is "active" or "pending" or "unknown" or null,
            JsonValueKind.Number => s.GetInt32() is 1 or 6,
            _ => true,
        };
    }

    private static (string? Path, string? Line) ThreadLocation(JsonElement thread)
    {
        if (!thread.TryGetProperty("threadContext", out var ctx) || ctx.ValueKind is not JsonValueKind.Object)
        {
            return (null, null);
        }

        var path = ctx.TryGetProperty("filePath", out var fp) && fp.ValueKind is JsonValueKind.String ? fp.GetString() : null;
        string? line = null;
        if (ctx.TryGetProperty("rightFileStart", out var rs) && rs.ValueKind is JsonValueKind.Object
            && rs.TryGetProperty("line", out var ln) && ln.ValueKind is JsonValueKind.Number)
        {
            line = ln.GetInt32().ToString(CultureInfo.InvariantCulture);
        }

        return (path, line);
    }

    private static string? AuthorOf(JsonElement comment) =>
        comment.TryGetProperty("author", out var a) && a.ValueKind is JsonValueKind.Object
            && a.TryGetProperty("displayName", out var dn) && dn.ValueKind is JsonValueKind.String
            ? dn.GetString()
            : null;

    private static string Trim(string content)
    {
        var oneLine = content.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaxBodyChars ? oneLine : oneLine[..MaxBodyChars] + "…";
    }

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
