using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
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
    private readonly int _maxPages;
    private readonly int _pageSize;

    public GitHubPrProvider(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<GitHubPrProvider> logger,
        int maxPagesPerPoll = MaxPages,
        int maxPrsPerPage = GitHubMaxPageSize
    )
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;

        // 100 is GitHub's hard ceiling for per_page; asking for more is not an error, it is silently
        // ignored, which would make a configured 500 read as if it had been honoured.
        _maxPages = Math.Max(1, maxPagesPerPoll);
        _pageSize = Math.Clamp(maxPrsPerPage, 1, GitHubMaxPageSize);
    }

    public string Provider => "github";

    /// <summary>Bounded pages per poll (PR #121 M5) so one poll can't spin unboundedly on a huge repo.
    /// Superseded by the constructor's <c>maxPagesPerPoll</c>, which carries the operator's configured
    /// <c>MaxPagesPerPoll</c>; this remains only as that parameter's default.</summary>
    private const int MaxPages = 10;

    /// <summary>GitHub's documented maximum for <c>per_page</c>. Both the default and the ceiling.</summary>
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
        // page URL comes verbatim from the previous response's Link header. Bounded by MaxPages per poll.
        var url = $"{BaseUrl}/repos/{owner}/{repo}/pulls?state=open&sort=updated&direction=desc&per_page={_pageSize}";
        var pages = 0;
        while (url is not null && pages < _maxPages)
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
                            DraftState = MapDraftState(pr, "draft"),
                            // Recency-filter signals: GitHub exposes both, so the filter uses updated_at (true
                            // last activity) with created_at as the fallback.
                            CreatedAt = ParseTimestamp(pr, "created_at"),
                            UpdatedAt = ParseTimestamp(pr, "updated_at"),
                            // Who OPENED the PR — addresses the per-developer feedback record. Left null when
                            // the payload omits it (deleted account, or a shape we didn't expect) so the daemon
                            // skips the record rather than addressing it to a placeholder.
                            Author = LoginOf(pr, "user"),
                            // What the PR SAYS it does. Without these the reviewer sees the diff but not the
                            // claim it is supposed to satisfy, and "does this change do what it says?" — the
                            // first question of any review — becomes unanswerable.
                            Title = StringOf(pr, "title"),
                            Description = StringOf(pr, "body"),
                            TargetBranch =
                                pr.TryGetProperty("base", out var baseRef) && baseRef.ValueKind is JsonValueKind.Object
                                    ? StringOf(baseRef, "ref")
                                    : null,
                            // Confidentiality trust signal. Both stay null when the payload cannot establish them;
                            // PrPollingService applies the fail-closed default, so an unexpected shape degrades to
                            // exactly today's behaviour rather than opening the gate.
                            IsForkPr = IsFork(pr),
                            IsTargetRepoPublic = BoolOf(RepoOf(pr, "base"), "private") is { } isPrivate
                                ? !isPrivate
                                : null,
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
                _pageSize,
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
    public async Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
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
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new PrStatus(MapPrLifecycle(document.RootElement), MapDraftState(document.RootElement, "draft"));
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

    private static PrDraftState MapDraftState(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
                ? PrDraftState.Draft
                : PrDraftState.Ready
            : PrDraftState.Unknown;

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

    /// <summary>A direct string property, or null when absent, non-string, or blank. GitHub sends
    /// <c>body: null</c> for a PR opened with no description, so "present but empty" and "absent" collapse
    /// to the same null — which is what the review brief renders as "(none)".</summary>
    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A direct boolean property, or null when the object itself is absent, the property is absent,
    /// or it is not a boolean. Null is meaningful for the trust signal — it is "GitHub did not say", which is
    /// not the same as "no". The <c>ValueKind</c> guard is load-bearing: <c>TryGetProperty</c> THROWS on a
    /// non-object element, so a missing nested object must not reach it.</summary>
    private static bool? BoolOf(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>The <c>repo</c> object nested under a PR's <c>head</c>/<c>base</c>, or null when either level
    /// is missing. GitHub nulls <c>head.repo</c> when the fork it lived in has since been deleted.</summary>
    private static JsonElement? RepoOf(JsonElement pr, string side) =>
        pr.TryGetProperty(side, out var s)
        && s.ValueKind is JsonValueKind.Object
        && s.TryGetProperty("repo", out var repo)
        && repo.ValueKind is JsonValueKind.Object
            ? repo
            : null;

    /// <summary>
    /// Whether the PR's head lives in a DIFFERENT repo than its base — the actual definition of a fork PR, and
    /// the one the confidentiality gate needs. Null when either side's repo is missing or unnamed.
    /// <para>
    /// Deliberately not <c>head.repo.fork</c>: that flag says the head repo is itself a fork of something,
    /// which is true of every PR opened <i>within</i> a fork — including same-repo ones that are perfectly
    /// same-trust — so reading it would deny co-location to runs that deserve it.
    /// </para>
    /// </summary>
    private static bool? IsFork(JsonElement pr)
    {
        if (RepoOf(pr, "head") is not { } head || RepoOf(pr, "base") is not { } @base)
        {
            return null;
        }

        var headName = StringOf(head, "full_name");
        var baseName = StringOf(@base, "full_name");
        return headName is null || baseName is null
            ? null
            : !string.Equals(headName, baseName, StringComparison.OrdinalIgnoreCase);
    }
}
