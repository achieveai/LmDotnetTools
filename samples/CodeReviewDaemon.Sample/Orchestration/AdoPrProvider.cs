using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Real <see cref="IPrProvider"/> over the Azure DevOps REST API. Lists active PRs for a repo:
/// <c>GET /{org}/{project}/_apis/git/repositories/{repo}/pullrequests?searchCriteria.status=active&amp;api-version=7.1</c>.
/// ADO authenticates REST calls with HTTP Basic auth carrying the token in the password field (the
/// username is ignored), so the bearer minted by the shared <see cref="IOAuthTokenProvider"/> is sent
/// base64-encoded as <c>:{token}</c>. The opaque cursor (§12) records the highest active
/// <c>pullRequestId</c>; ADO's continuation/skip model never leaks across the seam. The daemon is
/// GitHub-only by default — this provider is registered only when <c>EnableAdoProvider</c> is set.
/// </summary>
internal sealed class AdoPrProvider : IPrProvider
{
    private const string BaseUrl = "https://dev.azure.com";
    private const string ApiVersion = "7.1";

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<AdoPrProvider> _logger;

    /// <summary>Caches a commit's last-push date by its immutable commit id, so a PR whose head does not change
    /// is not re-fetched on every poll (the recency filter's per-old-PR commit call was otherwise a serial
    /// request waterfall repeated each poll). Only SUCCESSFUL resolutions are cached — a failed/absent date is
    /// never cached, so a transient hiccup is retried rather than stuck. Bounded by FIFO eviction (see
    /// <see cref="_cacheOrder"/>): at capacity, admitting a new id evicts only the OLDEST entry, so the hot set
    /// of recently-resolved dates survives — a wholesale clear would drop every hot entry and turn the next poll
    /// back into a full serial re-fetch waterfall under commit churn. Commit ids are immutable, so an id is
    /// admitted at most once (a cache hit short-circuits before the fetch).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastPushDateByCommit =
        new(StringComparer.Ordinal);
    /// <summary>Insertion order of <see cref="_lastPushDateByCommit"/> admissions, enabling O(1) FIFO eviction of
    /// the oldest entry at capacity (a ConcurrentDictionary has no ordering of its own). Enqueued 1:1 with a
    /// successful <c>TryAdd</c>, so its length tracks the admitted set.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _cacheOrder = new();
    private const int MaxCachedCommitDates = 4096;

    public AdoPrProvider(HttpClient httpClient, IOAuthTokenProvider tokenProvider, ILogger<AdoPrProvider> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Provider => "ado";

    /// <summary>Bounded pages per poll (PR #121 M5) so one poll can't spin unboundedly on a huge repo.</summary>
    private const int MaxPages = 10;

    public async Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = request.Repo.OrgOrOwner;
        var project = request.Repo.Project;
        var repo = request.Repo.RepoName;
        var baseUrl =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/pullrequests"
            + $"?searchCriteria.status=active&api-version={ApiVersion}";

        var pullRequests = new List<PullRequestDescriptor>();
        var highWaterMark = 0L;

        // Follow ADO's continuation-token pagination (M5): each response may carry an
        // x-ms-continuationtoken header, which is passed back as &continuationToken= on the next page.
        // Bounded by MaxPages per poll.
        string? continuationToken = null;
        var pages = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages++;

            var url = continuationToken is null
                ? baseUrl
                : $"{baseUrl}&continuationToken={Uri.EscapeDataString(continuationToken)}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url)
                .WithOperation(SandboxOperation.ReadProviderMetadata);
            var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            httpRequest.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                foreach (var pr in document.RootElement.GetProperty("value").EnumerateArray())
                {
                    var prId = pr.GetProperty("pullRequestId").GetInt64();
                    var headSha = CommitId(pr, "lastMergeSourceCommit");
                    var createdAt = ParseTimestamp(pr, "creationDate");

                    // ADO's PR list has no last-activity field (and its server-side time filter supports only
                    // created/closed), so "updated since" can't come from the list. For a PR created BEFORE the
                    // recency window, fetch the source branch tip commit's date (its last push) so an
                    // old-but-recently-pushed PR is still reviewed. Bounded: recent-created PRs skip this call
                    // entirely. Keep-on-uncertainty: an unfetchable date resolves to the cutoff so an active PR
                    // is never silently dropped.
                    DateTimeOffset? updatedAt = null;
                    if (request.RecencyCutoff is { } cutoff
                        && createdAt is { } created
                        && created < cutoff
                        && !string.IsNullOrEmpty(headSha))
                    {
                        updatedAt =
                            await TryGetLastPushDateAsync(org, project, repo, headSha, cancellationToken)
                                .ConfigureAwait(false) ?? cutoff;
                    }

                    pullRequests.Add(new PullRequestDescriptor
                    {
                        PrId = prId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        HeadSha = headSha,
                        BaseSha = CommitId(pr, "lastMergeTargetCommit"),
                        // ADO's PR list exposes no last-activity timestamp, so a new source commit (the head
                        // SHA) is the re-review trigger; same-head comment threads do not re-trigger here.
                        TriggerWatermark = headSha,
                        LifecycleState = MapLifecycle(pr.GetProperty("status").GetString()),
                        // Recency signals: creationDate (opened) always; UpdatedAt = last push, resolved above
                        // only for PRs created before the window (bounded extra call).
                        CreatedAt = createdAt,
                        UpdatedAt = updatedAt,
                    });

                    if (prId > highWaterMark)
                    {
                        highWaterMark = prId;
                    }
                }
            }

            continuationToken = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.FirstOrDefault()
                : null;
        }
        while (!string.IsNullOrEmpty(continuationToken) && pages < MaxPages);

        // Make the page cap OBSERVABLE rather than silently truncating: if we stopped at MaxPages with a
        // continuation token still pending, active PRs beyond the cap were NOT observed this poll. (A stateful
        // cross-poll resume is unsafe here — ADO continuation tokens are scoped to a query snapshot and go
        // stale as the active-PR set changes — so the correct fix is a bounded backlog reconciliation, tracked
        // separately; surfacing the drop is the minimum so it can never read as full coverage.)
        if (pages >= MaxPages && !string.IsNullOrEmpty(continuationToken))
        {
            _logger.LogWarning(
                "ADO poll of {Org}/{Project}/{Repo} hit the {MaxPages}-page cap with more active PRs pending; "
                    + "PRs beyond the cap were not observed this poll.",
                org, project, repo, MaxPages);
        }

        _logger.LogDebug(
            "ADO poll of {Org}/{Project}/{Repo} returned {Count} active PR(s) across {Pages} page(s).",
            org, project, repo, pullRequests.Count, pages);

        var hwm = highWaterMark.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new PullRequestPage
        {
            PullRequests = pullRequests,
            NextCursor = new OpaqueCursor
            {
                Provider = Provider,
                Scope = request.Scope,
                CursorVersion = PrPollingService.CursorVersion,
                CursorPayload = JsonSerializer.Serialize(new { highWaterMark = hwm }),
                HighWaterMark = hwm,
            },
        };
    }

    private static string CommitId(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var commit)
        && commit.ValueKind is JsonValueKind.Object
        && commit.TryGetProperty("commitId", out var id)
            ? id.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Parses an ISO-8601 timestamp property (e.g. ADO's <c>creationDate</c>) to a
    /// <see cref="DateTimeOffset"/>, or null when absent/unparseable — a missing date leaves the PR
    /// unfiltered by the recency window rather than silently dropping it.</summary>
    private static DateTimeOffset? ParseTimestamp(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Fetches the source branch tip commit (<c>GET .../commits/{commitId}</c>) and returns its
    /// <c>committer.date</c> (falling back to <c>author.date</c>) — the PR's last-push time, ADO's stand-in
    /// for a "last updated" field. Used by the recency filter for PRs created before the window. Returns
    /// null (caller keeps the PR) on any non-success, missing field, or transient failure — a recency
    /// heuristic must never drop an active PR because a metadata read hiccuped.
    /// </summary>
    private async Task<DateTimeOffset?> TryGetLastPushDateAsync(
        string org,
        string? project,
        string repo,
        string commitId,
        CancellationToken cancellationToken)
    {
        // A commit id is immutable, so its push date never changes — serve a prior successful resolution from
        // the cache instead of re-fetching it every poll (the recency waterfall this thread flagged).
        if (_lastPushDateByCommit.TryGetValue(commitId, out var cached))
        {
            return cached;
        }

        try
        {
            var url =
                $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/commits/{commitId}"
                + $"?api-version={ApiVersion}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url)
                .WithOperation(SandboxOperation.ReadProviderMetadata);
            var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            httpRequest.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "ADO commit {CommitId} recency fetch returned {Status}; keeping the PR.",
                    commitId,
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            // Prefer committer.date (when the commit was applied to the branch); fall back to author.date.
            foreach (var field in new[] { "committer", "author" })
            {
                if (root.TryGetProperty(field, out var gitUserDate)
                    && gitUserDate.ValueKind is JsonValueKind.Object)
                {
                    var date = ParseTimestamp(gitUserDate, "date");
                    if (date is not null)
                    {
                        // Cache only successful resolutions (a null/failure is retried next poll). Bound with
                        // FIFO eviction: admit the new id, then evict only the OLDEST entry while over the cap —
                        // clearing wholesale would drop the hot set and turn the next poll into a full serial
                        // re-fetch waterfall under commit churn. Commit ids are immutable and a cache hit
                        // short-circuits above, so TryAdd succeeds at most once per id (queue stays 1:1 with the
                        // admitted set).
                        if (_lastPushDateByCommit.TryAdd(commitId, date.Value))
                        {
                            _cacheOrder.Enqueue(commitId);
                            while (_cacheOrder.Count > MaxCachedCommitDates && _cacheOrder.TryDequeue(out var oldest))
                            {
                                _ = _lastPushDateByCommit.TryRemove(oldest, out _);
                            }
                        }

                        return date;
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ADO commit {CommitId} recency fetch failed; keeping the PR.", commitId);
            return null;
        }
    }

    /// <summary>
    /// Classifies a single PR's lifecycle via
    /// <c>GET /{org}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}</c> — mapping ADO's
    /// <c>status</c> field (<c>active</c>/<c>completed</c>/<c>abandoned</c>) to <see cref="PrLifecycle"/>.
    /// Used by the PR-lifecycle sweep (a later task) to decide whether to merge or delete the PR's notes
    /// branch. Mirrors <see cref="ListOpenPullRequestsAsync"/>'s basic-auth + accept-json request shape.
    /// </summary>
    public async Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(prId);

        var org = repo.OrgOrOwner;
        var project = repo.Project;
        var repoName = repo.RepoName;
        var url =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repoName}/pullrequests/{prId}"
            + $"?api-version={ApiVersion}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url)
            .WithOperation(SandboxOperation.ReadProviderMetadata);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return MapPrLifecycle(document.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// Maps ADO's single-PR <c>status</c> to <see cref="PrLifecycle"/>: <c>active</c> is Open,
    /// <c>completed</c> is Merged, <c>abandoned</c> is Abandoned. An unrecognized status is treated as Open
    /// so the sweep leaves the notes branch untouched rather than risk a wrong merge or delete.
    /// </summary>
    private static PrLifecycle MapPrLifecycle(string? status) => status switch
    {
        "active" => PrLifecycle.Open,
        "completed" => PrLifecycle.Merged,
        "abandoned" => PrLifecycle.Abandoned,
        _ => PrLifecycle.Open,
    };

    private static PrLifecycleState MapLifecycle(string? status) => status switch
    {
        "active" => PrLifecycleState.Open,
        "completed" => PrLifecycleState.Merged,
        "abandoned" => PrLifecycleState.Abandoned,
        _ => PrLifecycleState.Closed,
    };
}
