using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Real <see cref="IPrProvider"/> over the GitHub REST API. The daemon watches PRs by polling, so this
/// only needs to list the open PRs for a repo: <c>GET /repos/{owner}/{repo}/pulls?state=open</c>. Each
/// request carries a bearer token minted by the shared <see cref="IOAuthTokenProvider"/> (single bot
/// identity per provider, refreshed unattended — plan §5), the <c>User-Agent</c> GitHub mandates, and
/// the <c>vnd.github+json</c> accept header. The opaque cursor (§12) records the newest
/// <c>updated_at</c> seen as a high-water mark; GitHub's own pagination model never leaks across the seam.
/// </summary>
internal sealed class GitHubPrProvider : IPrProvider
{
    private const string BaseUrl = "https://api.github.com";
    private const string UserAgent = "LmDotnetTools-CodeReviewDaemon";

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<GitHubPrProvider> _logger;

    public GitHubPrProvider(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<GitHubPrProvider> logger,
        int maxPagesPerPoll = CodeReviewDaemonOptions.DefaultMaxPagesPerPoll,
        int maxPrsPerPage = CodeReviewDaemonOptions.DefaultMaxPrsPerPage
    )
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;

        // Normalized rather than validated: a nonsensical bound must not stop the daemon polling, and it
        // must not silently become "no pages" (a repo that looks permanently empty) or "no limit". Both
        // fall back to the documented default — see CodeReviewDaemonOptions.MaxPagesPerPoll.
        MaxPagesPerPoll = maxPagesPerPoll > 0 ? maxPagesPerPoll : CodeReviewDaemonOptions.DefaultMaxPagesPerPoll;
        // 100 is GitHub's hard ceiling for per_page; asking for more is not an error, it is silently
        // ignored, which would make a configured 500 read as if it had been honoured.
        PageSize = Math.Min(
            maxPrsPerPage > 0 ? maxPrsPerPage : CodeReviewDaemonOptions.DefaultMaxPrsPerPage,
            GitHubMaxPageSize
        );
    }

    public string Provider => "github";

    /// <summary>
    /// Bounded pages per poll (PR #121 M5) so one poll can't spin unboundedly on a huge repo. This is the
    /// operator's <see cref="CodeReviewDaemonOptions.MaxPagesPerPoll"/>, carried in by <c>Program.cs</c> at
    /// registration and normalized in the constructor. It is a property rather than a field so the effective
    /// bound the loop below reads is the same value a wiring test can observe on a provider resolved out of
    /// the real host — the knob had no reader at all before issue #537, and a reader nothing can see is how
    /// that recurs.
    /// </summary>
    internal int MaxPagesPerPoll { get; }

    /// <summary>The effective <c>per_page</c>: the operator's
    /// <see cref="CodeReviewDaemonOptions.MaxPrsPerPage"/>, clamped to GitHub's ceiling.</summary>
    internal int PageSize { get; }

    /// <summary>GitHub's documented maximum for <c>per_page</c>.</summary>
    private const int GitHubMaxPageSize = 100;

    public async Task<PullRequestPage> ListOpenPullRequestsAsync(
        PrPollRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var owner = request.Repo.OrgOrOwner;
        var repo = request.Repo.RepoName;

        var pullRequests = new List<PullRequestDescriptor>();
        string? highWaterMark = null;

        // Follow GitHub's Link rel="next" pagination (M5): the first page fixes the query, each subsequent
        // page URL comes verbatim from the previous response's Link header. Bounded by MaxPagesPerPoll.
        var url =
            $"{BaseUrl}/repos/{owner}/{repo}/pulls"
            + $"?state=open&sort=updated&direction=desc&per_page={PageSize.ToString(CultureInfo.InvariantCulture)}";
        var pages = 0;
        while (url is not null && pages < MaxPagesPerPoll)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages++;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url).WithOperation(
                SandboxOperation.ReadProviderMetadata
            );
            var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            httpRequest.Headers.UserAgent.ParseAdd(UserAgent);
            httpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                foreach (var pr in document.RootElement.EnumerateArray())
                {
                    var updatedAt = pr.GetProperty("updated_at").GetString() ?? string.Empty;
                    pullRequests.Add(
                        new PullRequestDescriptor
                        {
                            PrId = pr.GetProperty("number").GetRawText(),
                            HeadSha = pr.GetProperty("head").GetProperty("sha").GetString() ?? string.Empty,
                            BaseSha = pr.GetProperty("base").GetProperty("sha").GetString() ?? string.Empty,
                            TriggerWatermark = updatedAt,
                            LifecycleState = MapLifecycle(pr),
                            // Recency-filter signals: GitHub exposes both, so the filter uses updated_at (true
                            // last activity) with created_at as the fallback.
                            CreatedAt = ParseTimestamp(pr, "created_at"),
                            UpdatedAt = ParseTimestamp(pr, "updated_at"),
                            // Who OPENED the PR — addresses the per-developer feedback record. Left null when
                            // the payload omits it (deleted account, or a shape we didn't expect) so the daemon
                            // skips the record rather than addressing it to a placeholder.
                            Author = LoginOf(pr, "user"),
                            // What the PR SAYS it does. Retrieval ranks on this as well as on the changed
                            // paths, because sibling PRs applying one pattern often share no path token at all
                            // and the pattern is named here.
                            Title = StringOf(pr, "title"),
                            Description = StringOf(pr, "body"),
                        }
                    );

                    if (string.CompareOrdinal(updatedAt, highWaterMark) > 0)
                    {
                        highWaterMark = updatedAt;
                    }
                }
            }

            url = NextPageUrl(response);
        }

        // Stopping with a rel="next" still on offer means the list is incomplete. Less damaging here than on
        // ADO — this query is sorted `updated desc`, so what gets dropped is the LEAST recently touched, which
        // is what the recency filter would discard anyway — but it is still a coverage limit, and an unspoken
        // limit is the one nobody raises.
        if (url is not null)
        {
            _logger.LogWarning(
                "GitHub poll of {Owner}/{Repo} stopped after {Pages} page(s) of {PageSize} with more results "
                    + "still available; {Count} PR(s) were enumerated and the rest were NOT seen this poll. "
                    + "Raise CodeReviewDaemon:MaxPagesPerPoll if this repeats.",
                owner,
                repo,
                pages,
                PageSize,
                pullRequests.Count
            );
        }

        _logger.LogDebug(
            "GitHub poll of {Owner}/{Repo} returned {Count} open PR(s) across {Pages} page(s).",
            owner,
            repo,
            pullRequests.Count,
            pages
        );

        return new PullRequestPage
        {
            PullRequests = pullRequests,
            NextCursor = new OpaqueCursor
            {
                Provider = Provider,
                Scope = request.Scope,
                CursorVersion = PrPollingService.CursorVersion,
                CursorPayload = JsonSerializer.Serialize(new { highWaterMark }),
                HighWaterMark = highWaterMark,
            },
        };
    }

    /// <summary>
    /// Classifies a single PR's lifecycle via <c>GET /repos/{owner}/{repo}/pulls/{number}</c> — Open,
    /// Merged, or Abandoned (closed without merging). Used by the PR-lifecycle sweep (a later task) to
    /// decide whether to merge or delete the PR's notes branch.
    /// </summary>
    public async Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        using var document = await GetPullRequestAsync(repo, prId, cancellationToken).ConfigureAwait(false);
        return MapPrLifecycle(document.RootElement);
    }

    /// <summary>
    /// Reads the PR's current <c>head.sha</c> from the same single-PR resource
    /// <see cref="GetPrStateAsync"/> uses, so the currency check costs one request and no extra concept.
    /// Returns <c>null</c> only when the payload genuinely carries no head SHA; a transport or auth failure
    /// throws, because "unreachable" must never be reported as "nothing contradicts the recorded head".
    /// </summary>
    public async Task<string?> GetCurrentHeadShaAsync(
        RepoIdentity repo,
        string prId,
        CancellationToken cancellationToken
    )
    {
        using var document = await GetPullRequestAsync(repo, prId, cancellationToken).ConfigureAwait(false);
        if (
            !document.RootElement.TryGetProperty("head", out var head)
            || !head.TryGetProperty("sha", out var sha)
            || sha.ValueKind is not JsonValueKind.String
        )
        {
            return null;
        }

        var value = sha.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/pulls/{number}</c> — the single-PR resource both per-PR reads parse.
    /// The caller owns the returned document.
    /// </summary>
    private async Task<JsonDocument> GetPullRequestAsync(
        RepoIdentity repo,
        string prId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(prId);

        var owner = repo.OrgOrOwner;
        var repoName = repo.RepoName;
        var url = $"{BaseUrl}/repos/{owner}/{repoName}/pulls/{prId}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url).WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        httpRequest.Headers.UserAgent.ParseAdd(UserAgent);
        httpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        _ = response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Maps a single-PR response to <see cref="PrLifecycle"/>: <c>state == "open"</c> is Open;
    /// <c>state == "closed"</c> with a non-null <c>merged_at</c> is Merged; <c>state == "closed"</c> with a
    /// null <c>merged_at</c> is Abandoned.
    /// </summary>
    private static PrLifecycle MapPrLifecycle(JsonElement pr)
    {
        var state = pr.GetProperty("state").GetString();
        if (string.Equals(state, "open", StringComparison.OrdinalIgnoreCase))
        {
            return PrLifecycle.Open;
        }

        var merged = pr.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind is not JsonValueKind.Null;
        return merged ? PrLifecycle.Merged : PrLifecycle.Abandoned;
    }

    /// <summary>
    /// Extracts the <c>rel="next"</c> URL from the response's <c>Link</c> header, or <c>null</c> when this
    /// is the last page. GitHub's format is <c>&lt;url&gt;; rel="next", &lt;url&gt;; rel="last"</c>.
    /// </summary>
    private static string? NextPageUrl(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var linkHeaders))
        {
            return null;
        }

        foreach (var segment in string.Join(',', linkHeaders).Split(','))
        {
            var parts = segment.Split(';');
            if (parts.Length < 2 || !parts.Any(p => p.Contains("rel=\"next\"", StringComparison.Ordinal)))
            {
                continue;
            }

            var urlPart = parts[0].Trim();
            if (urlPart.StartsWith('<') && urlPart.EndsWith('>'))
            {
                return urlPart[1..^1];
            }
        }

        return null;
    }

    private static PrLifecycleState MapLifecycle(JsonElement pr)
    {
        var merged = pr.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind is not JsonValueKind.Null;
        if (merged)
        {
            return PrLifecycleState.Merged;
        }

        var state = pr.GetProperty("state").GetString();
        return string.Equals(state, "open", StringComparison.OrdinalIgnoreCase)
            ? PrLifecycleState.Open
            : PrLifecycleState.Closed;
    }

    /// <summary>Parses an ISO-8601 timestamp property (e.g. <c>created_at</c>/<c>updated_at</c>) to a
    /// <see cref="DateTimeOffset"/>, or null when the property is absent or unparseable — a missing date
    /// leaves the PR unfiltered by the recency window rather than silently dropping it.</summary>
    private static DateTimeOffset? ParseTimestamp(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed
        )
            ? parsed
            : null;

    /// <summary>
    /// Reads a string property off a PR payload, or null when it is absent, non-string, or blank. Blank
    /// collapses to null on purpose: an empty <c>body</c> is what GitHub sends for a PR with no
    /// description, and "" and null must rank identically rather than one of them looking like prose.
    /// </summary>
    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads <c>&lt;property&gt;.login</c> from a PR payload, or null when the object or the login is
    /// absent, non-string, or blank. GitHub omits the user object for a deleted account, so every layer
    /// below must already tolerate a null author — this returns null rather than a placeholder string so
    /// that tolerance is exercised instead of bypassed.
    /// </summary>
    private static string? LoginOf(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var user)
        && user.ValueKind is JsonValueKind.Object
        && user.TryGetProperty("login", out var login)
        && login.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(login.GetString())
            ? login.GetString()
            : null;
}
