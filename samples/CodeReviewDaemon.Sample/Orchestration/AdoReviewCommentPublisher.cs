using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        ILogger<AdoReviewCommentPublisher> logger
    )
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Provider => "ado";

    public async Task<PostedComment?> FindPostedCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ValidateFor(Provider);

        if (target.Kind == ReviewCommentKind.Reply)
        {
            return await FindPostedReplyAsync(target, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }

        using var request = await BuildRequestAsync(
            HttpMethod.Get,
            ThreadsUrl(target),
            SandboxOperation.ReadProviderMetadata,
            cancellationToken
        );
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
                    var threadId =
                        ProviderIdOf(thread) ?? throw new JsonException("ADO thread response has no numeric id.");
                    var commentId = ProviderIdOf(comment);
                    return new PostedComment(threadId, CanonicalCommentId(threadId, commentId));
                }
            }
        }

        return null;
    }

    public async Task<PostedComment> PostReviewCommentAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ValidateFor(Provider);

        if (target.Kind == ReviewCommentKind.Reply)
        {
            return await PostReplyAsync(target, idempotencyKey, body, cancellationToken).ConfigureAwait(false);
        }

        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            ThreadsUrl(target),
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        var content = new JsonObject
        {
            ["comments"] = new JsonArray(
                new JsonObject { ["content"] = IdempotencyMarker.Embed(body, idempotencyKey), ["commentType"] = 1 }
            ),
            ["status"] = 1,
        };
        if (target.Kind == ReviewCommentKind.Inline)
        {
            var sidePrefix = target.Side == ReviewCommentSide.Left ? "left" : "right";
            content["threadContext"] = new JsonObject
            {
                ["filePath"] = "/" + target.Path,
                [$"{sidePrefix}FileStart"] = Position(target.Line!.Value),
                [$"{sidePrefix}FileEnd"] = Position(target.Line.Value),
            };
        }
        request.Content = JsonContent.Create(content);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var id = document.RootElement.GetProperty("id").GetRawText();
        var commentId = FirstCommentIdOf(document.RootElement);
        _logger.LogInformation("Posted ADO review thread {ThreadId} on PR {PrId}.", id, target.PrId);
        return new PostedComment(id, CanonicalCommentId(id, commentId));
    }

    private static string ThreadsUrl(ReviewCommentTarget target) =>
        $"{ThreadsBaseUrl(target)}?api-version={ApiVersion}";

    private static string ThreadsBaseUrl(ReviewCommentTarget target) =>
        $"{BaseUrl}/{Segment(target.Repo.OrgOrOwner)}/{Segment(target.Repo.Project ?? string.Empty)}"
        + $"/_apis/git/repositories/{Segment(target.Repo.RepoName)}/pullRequests/{target.PrId}/threads";

    private static string ThreadCommentsUrl(ReviewCommentTarget target) =>
        $"{ThreadsBaseUrl(target)}/{target.ProviderThreadId}/comments?api-version={ApiVersion}";

    public async Task<IReadOnlyList<ExistingReviewComment>> ListExistingReviewCommentsAsync(
        ReviewCommentTarget target,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);

        using var request = await BuildRequestAsync(
            HttpMethod.Get,
            ThreadsUrl(target),
            SandboxOperation.ReadProviderMetadata,
            cancellationToken
        );
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Headers.Contains("x-ms-continuationtoken"))
        {
            throw new InvalidDataException(
                "Azure DevOps comment listing requires pagination and cannot be frozen completely."
            );
        }

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
                if (!IsActionableComment(comment))
                {
                    continue; // ADO system activity (votes/merges/reviewer updates) + deleted comments — not discussion.
                }

                var content =
                    comment.TryGetProperty("content", out var c) && c.ValueKind is JsonValueKind.String
                        ? c.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                results.Add(
                    new ExistingReviewComment(
                        path,
                        line,
                        Trim(content),
                        AuthorOf(comment),
                        isActive,
                        PublishedAtOf(comment),
                        threadId,
                        CanonicalCommentId(threadId, ProviderIdOf(comment)),
                        VersionOf(comment)
                    )
                );
            }
        }

        return results;
    }

    /// <summary>False for ADO non-discussion entries: system activity (merge attempts, reviewer updates, votes all
    /// carry <c>commentType: "system"</c>) and deleted comments. Those are non-blank but not review discussion, so
    /// excluding them stops them consuming the bounded existing-comment budget and displacing real findings.</summary>
    private static bool IsActionableComment(JsonElement comment)
    {
        if (comment.TryGetProperty("isDeleted", out var del) && del.ValueKind is JsonValueKind.True)
        {
            return false;
        }

        return !(
            comment.TryGetProperty("commentType", out var ct)
            && ct.ValueKind is JsonValueKind.String
            && string.Equals(ct.GetString(), "system", StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>Reads the comment's <c>publishedDate</c> (ISO-8601) — used to order past vs. new comments.</summary>
    private static DateTimeOffset? PublishedAtOf(JsonElement comment) =>
        comment.TryGetProperty("publishedDate", out var p)
        && p.ValueKind is JsonValueKind.String
        && DateTimeOffset.TryParse(
            p.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var dt
        )
            ? dt
            : null;

    private static string? VersionOf(JsonElement comment) =>
        StringOf(comment, "lastContentUpdatedDate") ?? StringOf(comment, "publishedDate");

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
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

        var path =
            ctx.TryGetProperty("filePath", out var fp) && fp.ValueKind is JsonValueKind.String ? fp.GetString() : null;
        string? line = null;
        if (
            ctx.TryGetProperty("rightFileStart", out var rs)
            && rs.ValueKind is JsonValueKind.Object
            && rs.TryGetProperty("line", out var ln)
            && ln.ValueKind is JsonValueKind.Number
        )
        {
            line = ln.GetInt32().ToString(CultureInfo.InvariantCulture);
        }

        return (path, line);
    }

    private static string? AuthorOf(JsonElement comment) =>
        comment.TryGetProperty("author", out var a)
        && a.ValueKind is JsonValueKind.Object
        && a.TryGetProperty("displayName", out var dn)
        && dn.ValueKind is JsonValueKind.String
            ? dn.GetString()
            : null;

    private async Task<PostedComment?> FindPostedReplyAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        CancellationToken cancellationToken
    )
    {
        using var request = await BuildRequestAsync(
            HttpMethod.Get,
            ThreadCommentsUrl(target),
            SandboxOperation.ReadProviderMetadata,
            cancellationToken
        );
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        foreach (var comment in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var content = comment.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (!IdempotencyMarker.Matches(content, idempotencyKey))
            {
                continue;
            }

            var commentId = ProviderIdOf(comment) ?? throw new JsonException("ADO comment response has no numeric id.");
            return new PostedComment(commentId, $"{target.ProviderThreadId}/{commentId}");
        }

        return null;
    }

    private async Task<PostedComment> PostReplyAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        string body,
        CancellationToken cancellationToken
    )
    {
        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            ThreadCommentsUrl(target),
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        request.Content = JsonContent.Create(
            new
            {
                content = IdempotencyMarker.Embed(body, idempotencyKey),
                parentCommentId = long.Parse(target.ReplyToProviderCommentId!, CultureInfo.InvariantCulture),
                commentType = 1,
            }
        );

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var commentId = document.RootElement.GetProperty("id").GetRawText();
        _logger.LogInformation(
            "Posted ADO review reply {CommentId} on PR {PrId} thread {ThreadId}.",
            commentId,
            target.PrId,
            target.ProviderThreadId
        );
        return new PostedComment(commentId, $"{target.ProviderThreadId}/{commentId}");
    }

    private static JsonObject Position(int line) => new() { ["line"] = line, ["offset"] = 1 };

    private static string? ProviderIdOf(JsonElement element) =>
        element.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number ? id.GetRawText() : null;

    private static string? FirstCommentIdOf(JsonElement thread) =>
        thread.TryGetProperty("comments", out var comments)
        && comments.ValueKind is JsonValueKind.Array
        && comments.GetArrayLength() > 0
            ? ProviderIdOf(comments[0])
            : null;

    private static string? CanonicalCommentId(string? threadId, string? commentId) =>
        threadId is not null && commentId is not null ? $"{threadId}/{commentId}" : null;

    /// <summary>
    /// Per-comment cap, shared with the GitHub publisher via <see cref="ExistingCommentBody"/> so the two
    /// cannot drift into showing the reviewer different amounts of the same conversation (#225).
    /// </summary>
    private static string Trim(string content) => ExistingCommentBody.Summarize(content);

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        SandboxOperation operation,
        CancellationToken cancellationToken
    )
    {
        var request = new HttpRequestMessage(method, url).WithOperation(operation);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
}
