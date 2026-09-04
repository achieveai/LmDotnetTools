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
                    return ThreadReceipt(thread, comment);
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

        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            ThreadsUrl(target),
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        request.Content = JsonContent.Create(
            new
            {
                comments = new[] { new { content = IdempotencyMarker.Embed(body, idempotencyKey), commentType = 1 } },
                status = 1,
            }
        );

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var threadId = RequiredId(root, "id");
        var comment = root.GetProperty("comments")[0];
        _logger.LogInformation("Posted ADO review thread {ThreadId} on PR {PrId}.", threadId, target.PrId);
        return ThreadReceipt(root, comment);
    }

    public async Task<PostedComment> PostLocatedThreadAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        AdoThreadRequest thread,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(thread);
        ValidateSpan(thread.Span);
        ValidatePullRequestContext(thread.PullRequestContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(thread.Body);

        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            ThreadsUrl(target),
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        request.Content = JsonContent.Create(
            new
            {
                comments = new[]
                {
                    new
                    {
                        parentCommentId = 0,
                        content = IdempotencyMarker.Embed(thread.Body, idempotencyKey),
                        commentType = 1,
                    },
                },
                status = 1,
                threadContext = ThreadContextPayload(thread.Span),
                pullRequestThreadContext = thread.PullRequestContext,
            }
        );
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response, cancellationToken);
        var receipt = ThreadReceipt(document.RootElement, document.RootElement.GetProperty("comments")[0]);
        return receipt with
        {
            Span = receipt.Span ?? thread.Span,
            IterationContext = receipt.IterationContext ?? thread.PullRequestContext,
        };
    }

    public async Task<PostedComment> ReplyToThreadAsync(
        ReviewCommentTarget target,
        string idempotencyKey,
        AdoThreadReplyRequest reply,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reply);
        var threadId = RequireNumericId(reply.ThreadId, nameof(reply.ThreadId));
        var parentCommentId = RequireNumericId(reply.ParentCommentId, nameof(reply.ParentCommentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reply.Body);

        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            ThreadCommentsUrl(target, threadId),
            SandboxOperation.PostReviewComment,
            cancellationToken
        );
        request.Content = JsonContent.Create(
            new
            {
                parentCommentId = long.Parse(parentCommentId, CultureInfo.InvariantCulture),
                content = IdempotencyMarker.Embed(reply.Body, idempotencyKey),
                commentType = 1,
            }
        );
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ParseAsync(response, cancellationToken);
        var comment = document.RootElement;
        var commentId = RequiredId(comment, "id");
        return new PostedComment(
            ThreadCommentReceiptId(threadId, commentId),
            ThreadId: threadId,
            CommentId: commentId,
            ParentCommentId: OptionalId(comment, "parentCommentId") ?? parentCommentId,
            Permalink: LinkOf(comment),
            Status: reply.ThreadStatus,
            Span: reply.Span,
            IterationContext: reply.PullRequestContext
        );
    }

    private static PostedComment ThreadReceipt(JsonElement thread, JsonElement comment)
    {
        var threadId = RequiredId(thread, "id");
        var commentId = RequiredId(comment, "id");
        return new PostedComment(
            ThreadCommentReceiptId(threadId, commentId),
            ThreadId: threadId,
            CommentId: commentId,
            ParentCommentId: OptionalId(comment, "parentCommentId"),
            Permalink: LinkOf(comment),
            Status: StatusOf(thread),
            Span: SpanOf(thread),
            IterationContext: PullRequestContextOf(thread)
        );
    }

    private static IReadOnlyDictionary<string, object> ThreadContextPayload(ProviderCommentSpan span)
    {
        var result = new Dictionary<string, object> { ["filePath"] = span.Path };
        var prefix = span.Side == "RIGHT" ? "right" : "left";
        result[$"{prefix}FileStart"] = Position(span.StartLine ?? span.EndLine, span.StartOffset);
        result[$"{prefix}FileEnd"] = Position(span.EndLine, span.EndOffset);
        return result;
    }

    private static object Position(int line, int? offset) => new { line, offset = offset ?? 1 };

    private static void ValidateSpan(ProviderCommentSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentException.ThrowIfNullOrWhiteSpace(span.Path);
        if (span.Side is not ("LEFT" or "RIGHT"))
        {
            throw new ArgumentException("Comment side must be LEFT or RIGHT.", nameof(span));
        }

        if (
            span.EndLine <= 0
            || span.StartLine is <= 0
            || span.StartLine > span.EndLine
            || span.StartOffset is <= 0
            || span.EndOffset is <= 0
        )
        {
            throw new ArgumentException("Comment lines and offsets must form a positive ascending span.", nameof(span));
        }
    }

    private static void ValidatePullRequestContext(AdoPullRequestThreadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (
            context.ChangeTrackingId <= 0
            || context.IterationContext.FirstComparingIteration < 0
            || context.IterationContext.SecondComparingIteration <= 0
        )
        {
            throw new ArgumentException("ADO change and iteration context must be positive.", nameof(context));
        }
    }

    private static ProviderCommentSpan? SpanOf(JsonElement thread)
    {
        if (!thread.TryGetProperty("threadContext", out var context) || context.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var path = StringOf(context, "filePath");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var side =
            context.TryGetProperty("rightFileStart", out var right) && right.ValueKind == JsonValueKind.Object
                ? "RIGHT"
                : "LEFT";
        var prefix = side == "RIGHT" ? "right" : "left";
        var start = PositionOf(context, $"{prefix}FileStart");
        var end = PositionOf(context, $"{prefix}FileEnd");
        if (start.Line is null && end.Line is null)
        {
            return null;
        }

        return new ProviderCommentSpan(
            path,
            side,
            start.Line ?? end.Line,
            end.Line ?? start.Line!.Value,
            start.Offset,
            end.Offset
        );
    }

    private static (int? Line, int? Offset) PositionOf(JsonElement context, string name)
    {
        if (!context.TryGetProperty(name, out var position) || position.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (IntOf(position, "line"), IntOf(position, "offset"));
    }

    private static AdoPullRequestThreadContext? PullRequestContextOf(JsonElement thread)
    {
        if (
            !thread.TryGetProperty("pullRequestThreadContext", out var context)
            || context.ValueKind != JsonValueKind.Object
            || IntOf(context, "changeTrackingId") is not { } changeTrackingId
            || !context.TryGetProperty("iterationContext", out var iteration)
            || iteration.ValueKind != JsonValueKind.Object
            || IntOf(iteration, "firstComparingIteration") is not { } first
            || IntOf(iteration, "secondComparingIteration") is not { } second
        )
        {
            return null;
        }

        return new AdoPullRequestThreadContext(changeTrackingId, new AdoIterationContext(first, second));
    }

    private static int? IntOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? StatusOf(JsonElement thread) =>
        thread.TryGetProperty("status", out var status)
            ? status.ValueKind switch
            {
                JsonValueKind.String => status.GetString(),
                JsonValueKind.Number => status.GetRawText(),
                _ => null,
            }
            : null;

    private static string? LinkOf(JsonElement comment) =>
        comment.TryGetProperty("_links", out var links)
        && links.ValueKind == JsonValueKind.Object
        && links.TryGetProperty("self", out var self)
        && self.ValueKind == JsonValueKind.Object
            ? StringOf(self, "href")
            : null;

    private static string RequiredId(JsonElement element, string name) =>
        OptionalId(element, name) ?? throw new JsonException($"Provider response omitted '{name}'.");

    private static string? OptionalId(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString(),
                _ => null,
            }
            : null;

    private static string RequireNumericId(string value, string parameterName) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? value
            : throw new ArgumentException("Provider thread/comment IDs must be numeric.", parameterName);

    private static async Task<JsonDocument> ParseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string ThreadCommentReceiptId(string threadId, string commentId) =>
        $"thread:{threadId}:comment:{commentId}";

    private static string ThreadCommentsUrl(ReviewCommentTarget target, string threadId) =>
        $"{BaseUrl}/{target.Repo.OrgOrOwner}/{target.Repo.Project}/_apis/git/repositories/{target.Repo.RepoName}"
        + $"/pullRequests/{target.PrId}/threads/{threadId}/comments?api-version={ApiVersion}";

    private static string ThreadsUrl(ReviewCommentTarget target) =>
        $"{BaseUrl}/{target.Repo.OrgOrOwner}/{target.Repo.Project}/_apis/git/repositories/{target.Repo.RepoName}"
        + $"/pullRequests/{target.PrId}/threads?api-version={ApiVersion}";

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
                        threadId
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

    /// <summary>
    /// Per-comment cap, shared with the GitHub publisher via <see cref="ExistingCommentBody"/> so the two
    /// cannot drift into showing the reviewer different amounts of the same conversation (#225).
    /// </summary>
    private static string Trim(string content) => ExistingCommentBody.Summarize(content);

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
