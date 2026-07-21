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
                    var sourceRefName = pr.TryGetProperty("sourceRefName", out var srn)
                        && srn.ValueKind is JsonValueKind.String
                            ? srn.GetString()
                            : null;

                    // ADO's PR list has no last-activity field (and its server-side time filter supports only
                    // created/closed), so "updated since" can't come from the list. For a PR created BEFORE the
                    // recency window, fetch the most recent push to its source branch (its true last-push time) so
                    // an old-but-recently-pushed PR is still reviewed. Bounded: recent-created PRs skip this call
                    // entirely.
                    //
                    // Recency signal (consumed only by PrPollingService.ApplyRecencyFilter as `UpdatedAt ?? CreatedAt`):
                    //  - recent-created PR  -> UpdatedAt null, CreatedAt = creationDate (recent) -> kept.
                    //  - old PR, push date known   -> UpdatedAt = last push -> kept/dropped on real recency.
                    //  - old PR, push date UNKNOWN  -> leave the signal indeterminate (BOTH null) so the filter's
                    //    "can't date it => keep" path applies. We do NOT fabricate a boundary timestamp (an earlier
                    //    `?? cutoff` did, which conflates "unknown" with "active exactly at the cutoff"), and we do
                    //    NOT fall back to the stale opened-date (which would wrongly DROP a possibly-active PR).
                    DateTimeOffset? updatedAt = null;
                    DateTimeOffset? recencyCreatedAt = createdAt;
                    if (request.RecencyCutoff is { } cutoff
                        && createdAt is { } created
                        && created < cutoff
                        && !string.IsNullOrEmpty(sourceRefName))
                    {
                        var lastPush = await TryGetLastPushDateAsync(org, project, repo, sourceRefName, cancellationToken)
                            .ConfigureAwait(false);
                        if (lastPush is { } push)
                        {
                            updatedAt = push;
                        }
                        else
                        {
                            // Uncertain: null the recency signal so the filter keeps this PR rather than dropping
                            // it on the old opened-date. CreatedAt has no other consumer than the recency filter.
                            recencyCreatedAt = null;
                        }
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
                        // Recency signals (consumed only by ApplyRecencyFilter): CreatedAt = opened date, but
                        // nulled for an old PR whose last push couldn't be dated (see above) so the filter keeps
                        // it; UpdatedAt = last push, resolved above only for PRs created before the window.
                        CreatedAt = recencyCreatedAt,
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
    /// Fetches the most recent push to the PR's source branch (<c>GET .../pushes?searchCriteria.refName=...</c>)
    /// and returns its <c>date</c> — the PR's true last-push time, ADO's stand-in for a "last updated" field.
    /// Uses the ref-update/push time rather than the head commit's <c>committer</c>/<c>author</c> date, which are
    /// authored/rebased timestamps, not push times (an old commit recently pushed or force-pushed would otherwise
    /// be mis-dated and wrongly dropped). Used by the recency filter for PRs created before the window. Returns
    /// null (caller keeps the PR, recency indeterminate) on any non-success, missing field, or transient failure
    /// — a recency heuristic must never drop an active PR because a metadata read hiccuped.
    /// </summary>
    private async Task<DateTimeOffset?> TryGetLastPushDateAsync(
        string org,
        string? project,
        string repo,
        string sourceRefName,
        CancellationToken cancellationToken)
    {
        try
        {
            var url =
                $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/pushes"
                + $"?searchCriteria.refName={Uri.EscapeDataString(sourceRefName)}"
                + $"&$top=1&api-version={ApiVersion}";

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
                    "ADO pushes fetch for ref {Ref} returned {Status}; keeping the PR (recency indeterminate).",
                    sourceRefName,
                    (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("value", out var pushes)
                || pushes.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            // ADO returns pushes newest-first; take the max `date` over the (top-1) page defensively, so the
            // result is the latest push time regardless of page ordering.
            DateTimeOffset? latest = null;
            foreach (var push in pushes.EnumerateArray())
            {
                var date = ParseTimestamp(push, "date");
                if (date is { } d && (latest is null || d > latest))
                {
                    latest = d;
                }
            }

            return latest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ADO pushes fetch for ref {Ref} failed; keeping the PR.", sourceRefName);
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
