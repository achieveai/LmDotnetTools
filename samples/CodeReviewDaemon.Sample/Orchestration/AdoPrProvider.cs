using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Configuration;
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

    public AdoPrProvider(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<AdoPrProvider> logger,
        int maxPagesPerPoll = CodeReviewDaemonOptions.DefaultMaxPagesPerPoll,
        int maxPrsPerPage = CodeReviewDaemonOptions.DefaultMaxPrsPerPage)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;

        // Normalized rather than validated: a nonsensical bound must not stop the daemon polling, and it
        // must not silently become "no pages" (a repo that looks permanently empty) or "no limit". Both fall
        // back to the documented default — see CodeReviewDaemonOptions.MaxPagesPerPoll.
        MaxPagesPerPoll = maxPagesPerPoll > 0 ? maxPagesPerPoll : CodeReviewDaemonOptions.DefaultMaxPagesPerPoll;
        // ADO rejects a $top above 1000 outright.
        PageSize = Math.Min(
            maxPrsPerPage > 0 ? maxPrsPerPage : CodeReviewDaemonOptions.DefaultMaxPrsPerPage,
            AdoMaxPageSize);
    }

    public string Provider => "ado";

    /// <summary>
    /// Bounded pages per poll (PR #121 M5) so one poll can't spin unboundedly on a huge repo. This is the
    /// operator's <see cref="CodeReviewDaemonOptions.MaxPagesPerPoll"/>, carried in by <c>Program.cs</c> at
    /// registration and normalized in the constructor. It is a property rather than a field so the effective
    /// bound the loop below reads is the same value a wiring test can observe on a provider resolved out of
    /// the real host — the knob had no reader at all before issue #537, and a reader nothing can see is how
    /// that recurs.
    /// </summary>
    internal int MaxPagesPerPoll { get; }

    /// <summary>The effective <c>$top</c>: the operator's
    /// <see cref="CodeReviewDaemonOptions.MaxPrsPerPage"/>, clamped to ADO's ceiling.</summary>
    internal int PageSize { get; }

    /// <summary>Azure DevOps' documented maximum for <c>$top</c> on the PR-list endpoint.</summary>
    private const int AdoMaxPageSize = 1000;

    /// <summary>
    /// Whether the listing might not be finished. True when ADO handed back a continuation token, or when
    /// the last page came back exactly full — a full page is indistinguishable from a truncated one, so it
    /// has to be treated as "maybe more" until a short page proves otherwise.
    /// <para>
    /// Used for BOTH the loop condition and the truncation warning, deliberately: the previous version
    /// tested the token in the loop and the token in the warning, so the case that actually truncates —
    /// full page, no token — exited the loop quietly and then failed to warn about it.
    /// </para>
    /// </summary>
    private static bool MoreMayRemain(string? continuationToken, int lastPageCount, int pageSize) =>
        !string.IsNullOrEmpty(continuationToken) || lastPageCount >= pageSize;

    /// <summary>Max concurrent ADO <c>/pushes</c> recency lookups per poll — bounds the fan-out so a page full
    /// of old PRs doesn't serialize into minutes of round trips or trip ADO throttling.</summary>
    private const int MaxRecencyLookupConcurrency = 6;

    public async Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = request.Repo.OrgOrOwner;
        var project = request.Repo.Project;
        var repo = request.Repo.RepoName;
        // org/project/repo are interpolated into a URL that becomes a System.Uri when SendAsync runs it. Uri
        // escapes a space in a segment, but NOT the delimiters '/' '?' '#' inside one — a '/' would open a new
        // path segment, a '?' start the query, a '#' the fragment, each silently addressing a different resource.
        // What makes that safe is that Azure DevOps FORBIDS all three in an org/project/repo name (issue #492
        // item 3). That reason is deliberately about the names themselves rather than about any validation
        // this daemon runs, because it has to hold on every route into this file — and the routes do not
        // share a guard. PrPollTargetBuilder.ValidateEnabledRepos does reject these characters at config load
        // (issue #491), but it only covers identities built from EnabledRepos: GetPullRequestAsync below
        // (:427) is reached from GetPrStateAsync with a RepoIdentity read back out of ReviewStore, which is
        // never re-validated. So the validator is a second, earlier guard on the poll route, NOT the reason
        // this interpolation is safe.
        // A future provider with laxer naming rules could not inherit the safety claim: it would need the
        // segments encoded up front (as GitRemoteUrl.RepoPathFor does for the git/allow-list side), because
        // nothing on the store-fed route would stop them.
        // $top is EXPLICIT. Omitting it does not mean "no limit" — ADO applies its own default of 101, which
        // is indistinguishable in the response from a repo that simply has 101 open PRs. Measured on a repo
        // with 711 active pull requests, of which one poll could see 101 (issue #537 / M7).
        var baseUrl =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/pullrequests"
            + $"?searchCriteria.status=active&$top={PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&api-version={ApiVersion}";

        var pullRequests = new List<PullRequestDescriptor>();
        var highWaterMark = 0L;

        // Paging is $skip-based, with continuation-token support kept for the case ADO does send one.
        //
        // The token alone does NOT work here, and that was measured rather than reasoned: with $top=200 a
        // poll of a repo holding 552 active PRs returned exactly 200 across ONE page and no continuation
        // header. Exactly-the-page-size is a cap signature, not a count — the same tell that hid the original
        // 101. Because the loop's exit condition was "no token", stopping early was also SILENT: the
        // truncation warning could not fire on the very case that truncates.
        //
        // So the end of the list is inferred the only way this endpoint allows: a page that comes back
        // SHORTER than $top is the last one. A full final page costs one extra empty request, which is the
        // price of proving termination instead of assuming it. Bounded by MaxPagesPerPoll.
        string? continuationToken = null;
        var pages = 0;
        var skip = 0;
        var lastPageCount = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages++;

            var url = continuationToken is not null
                ? $"{baseUrl}&continuationToken={Uri.EscapeDataString(continuationToken)}"
                : skip > 0
                    ? $"{baseUrl}&$skip={skip.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                    : baseUrl;

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

                // Phase 1: extract each PR's raw metadata. A JsonElement can't outlive its document, and the
                // recency resolution below is async I/O, so the fields must be materialized first.
                var rawPrs = new List<RawAdoPr>();
                foreach (var pr in document.RootElement.GetProperty("value").EnumerateArray())
                {
                    rawPrs.Add(new RawAdoPr(
                        pr.GetProperty("pullRequestId").GetInt64(),
                        CommitId(pr, "lastMergeSourceCommit"),
                        CommitId(pr, "lastMergeTargetCommit"),
                        ParseTimestamp(pr, "creationDate"),
                        pr.TryGetProperty("sourceRefName", out var srn) && srn.ValueKind is JsonValueKind.String
                            ? srn.GetString()
                            : null,
                        pr.GetProperty("status").GetString(),
                        // Who OPENED the PR. ADO's uniqueName is normally an email address; it is only
                        // carried as an opaque identity string here, and the consumer is responsible for
                        // reducing it to a safe, confined file name.
                        UniqueNameOf(pr, "createdBy"),
                        // What the PR SAYS it does — the prose half of the knowledge-retrieval key.
                        StringOf(pr, "title"),
                        StringOf(pr, "description")));
                }

                // Phase 2: resolve each PR's recency signal. ADO's PR list has no last-activity field, so a PR
                // created BEFORE the window needs one bounded `/pushes` lookup for its source branch's true
                // last-push time (so an old-but-recently-pushed PR is still reviewed). These lookups run with
                // bounded concurrency so a page full of old PRs doesn't serialize into minutes of round trips or
                // trip ADO throttling; recent PRs and PRs with no usable source ref make no call.
                var recency = await ResolveRecencySignalsAsync(
                    rawPrs, org, project, repo, request.RecencyCutoff, cancellationToken).ConfigureAwait(false);

                // Phase 3: build descriptors.
                for (var i = 0; i < rawPrs.Count; i++)
                {
                    var raw = rawPrs[i];
                    var (updatedAt, recencyCreatedAt) = recency[i];
                    pullRequests.Add(new PullRequestDescriptor
                    {
                        PrId = raw.PrId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        HeadSha = raw.HeadSha,
                        BaseSha = raw.BaseSha,
                        // ADO's PR list exposes no last-activity timestamp, so a new source commit (the head
                        // SHA) is the re-review trigger; same-head comment threads do not re-trigger here.
                        TriggerWatermark = raw.HeadSha,
                        LifecycleState = MapLifecycle(raw.Status),
                        // Recency signals (consumed only by ApplyRecencyFilter): CreatedAt = opened date, but
                        // nulled for an old PR whose last push couldn't be dated (see ResolveRecencySignalsAsync)
                        // so the filter keeps it; UpdatedAt = last push, resolved only for PRs before the window.
                        CreatedAt = recencyCreatedAt,
                        UpdatedAt = updatedAt,
                        Author = raw.Author,
                        Title = raw.Title,
                        Description = raw.Description,
                    });

                    if (raw.PrId > highWaterMark)
                    {
                        highWaterMark = raw.PrId;
                    }
                }

                lastPageCount = rawPrs.Count;
                skip += lastPageCount;
            }

            continuationToken = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.FirstOrDefault()
                : null;
        }
        while (MoreMayRemain(continuationToken, lastPageCount, PageSize) && pages < MaxPagesPerPoll);

        // A poll that stops while more may remain has NOT seen the repo's open PRs, and every downstream
        // filter — recency above all — can only ever filter what this list contained. Said out loud at
        // Warning because the failure it describes is invisible by construction: a truncated page and a
        // complete one are the same shape, which is how a 101-PR cap survived unnoticed on a repo with 711.
        if (MoreMayRemain(continuationToken, lastPageCount, PageSize))
        {
            _logger.LogWarning(
                "ADO poll of {Org}/{Project}/{Repo} stopped after {Pages} page(s) of {PageSize} with more "
                    + "results still available; {Count} PR(s) were enumerated and the rest were NOT seen this "
                    + "poll. Raise CodeReviewDaemon:MaxPagesPerPoll or MaxPrsPerPage if this repeats.",
                org, project, repo, pages, PageSize, pullRequests.Count);
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
    /// Reads <c>&lt;property&gt;.uniqueName</c> from an ADO PR payload, or null when it is not a usable
    /// string. Unlike a GitHub login this value is typically an email address and is NOT constrained to
    /// filename-safe characters, so it is returned verbatim and left for the consumer to slug and confine.
    /// <para>
    /// There is deliberately <b>no <c>displayName</c> fallback</b>. The one consumer of this value keys a
    /// per-developer record file off it, and <c>displayName</c> is not an identity — ADO lets two people
    /// carry the same one, so two developers would share a record no slugging scheme could tell apart.
    /// Returning null costs a record the daemon was never able to address correctly; falling back would
    /// have filed one person's mistakes under another's name in a public repository. A null author is an
    /// ordinary outcome on this path, not an error.
    /// </para>
    /// </summary>
    private static string? UniqueNameOf(JsonElement pr, string property)
    {
        if (!pr.TryGetProperty(property, out var identity) || identity.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return identity.TryGetProperty("uniqueName", out var value)
            && value.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    /// <summary>Raw per-PR metadata materialized from one ADO PR-list page, before the async recency
    /// resolution (a <see cref="JsonElement"/> can't outlive its document).</summary>
    private sealed record RawAdoPr(
        long PrId, string HeadSha, string BaseSha, DateTimeOffset? CreatedAt, string? SourceRefName, string? Status,
        string? Author, string? Title, string? Description);

    /// <summary>
    /// Reads a string property off an ADO PR payload, or null when it is absent, non-string, or blank.
    /// Blank collapses to null so a PR with an empty description ranks identically to one with none.
    /// </summary>
    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>
    /// Resolves each PR's recency signal (<c>UpdatedAt</c>, <c>CreatedAt</c>) for
    /// <c>PrPollingService.ApplyRecencyFilter</c>. A PR created before the window gets one bounded
    /// <c>/pushes</c> lookup for its source branch's real last-push time; recent PRs and PRs with no usable
    /// source ref make no call. Lookups run with bounded concurrency (<see cref="MaxRecencyLookupConcurrency"/>)
    /// so a page full of old PRs doesn't serialize into minutes of sequential round trips. Per PR:
    /// recent → <c>(null, createdAt)</c> (kept on the recent opened-date); old + push known →
    /// <c>(push, createdAt)</c>; old + push indeterminate (blank ref or failed lookup) → <c>(null, null)</c>
    /// so the filter's "can't-date-it ⇒ keep" path applies (never fabricate, never drop on the stale opened-date).
    /// </summary>
    private async Task<(DateTimeOffset? UpdatedAt, DateTimeOffset? RecencyCreatedAt)[]> ResolveRecencySignalsAsync(
        IReadOnlyList<RawAdoPr> prs,
        string org,
        string? project,
        string repo,
        DateTimeOffset? cutoff,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxRecencyLookupConcurrency);
        var tasks = prs.Select(pr => ResolveOneRecencyAsync(pr, gate, org, project, repo, cutoff, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Resolves one PR's recency signal (see <see cref="ResolveRecencySignalsAsync"/>), taking a slot on
    /// <paramref name="gate"/> only for the (old-PR) branch that makes the <c>/pushes</c> call.</summary>
    private async Task<(DateTimeOffset? UpdatedAt, DateTimeOffset? RecencyCreatedAt)> ResolveOneRecencyAsync(
        RawAdoPr pr,
        SemaphoreSlim gate,
        string org,
        string? project,
        string repo,
        DateTimeOffset? cutoff,
        CancellationToken cancellationToken)
    {
        // Recent PR (or recency off): no lookup; the recent opened-date is the keep signal.
        if (cutoff is not { } c || pr.CreatedAt is not { } created || created >= c)
        {
            return (null, pr.CreatedAt);
        }

        // Old PR with no usable source ref: recency indeterminate ⇒ keep (both null), like a failed lookup.
        if (string.IsNullOrEmpty(pr.SourceRefName))
        {
            return (null, null);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var push = await TryGetLastPushDateAsync(org, project, repo, pr.SourceRefName, cancellationToken)
                .ConfigureAwait(false);
            if (push is null)
            {
                return (null, null);
            }

            return (push, pr.CreatedAt);
        }
        finally
        {
            _ = gate.Release();
        }
    }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real caller cancellation (poll aborted) — propagate; nobody is waiting for this result.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else — including an HttpClient TIMEOUT, which surfaces as a
            // TaskCanceledException/OperationCanceledException even though the caller's token was NOT
            // cancelled — is a failed lookup: keep the PR (recency indeterminate) rather than letting one
            // timed-out /pushes call fault the whole poll via Task.WhenAll.
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
        using var document = await GetPullRequestAsync(repo, prId, cancellationToken).ConfigureAwait(false);
        return MapPrLifecycle(document.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// Reads the PR's current source-branch commit (<c>lastMergeSourceCommit.commitId</c> — the same field
    /// the poll records as <see cref="PullRequestDescriptor.HeadSha"/>) from the same single-PR resource
    /// <see cref="GetPrStateAsync"/> uses. Returns <c>null</c> only when the payload carries no such commit;
    /// a transport or auth failure throws, because "unreachable" must never be reported as "nothing
    /// contradicts the recorded head".
    /// <para>
    /// <b>Freshness.</b> Azure DevOps refreshes <c>lastMergeSourceCommit</c> when it re-evaluates the merge,
    /// not synchronously on push, so this field can lag a just-pushed head by the length of that evaluation.
    /// The lag can only produce a FALSE NEGATIVE — the guard sees the old commit, agrees with the equally old
    /// recorded head, and lets a review through that a moment later would have been refused. It cannot produce
    /// a false positive, so no review is abandoned over a stale field. Reading the SAME field the poll records
    /// at <see cref="ListOpenPullRequestsAsync"/> is deliberate: comparing two different notions of "head"
    /// would manufacture disagreements out of field semantics rather than out of the branch actually moving.
    /// </para>
    /// </summary>
    public async Task<string?> GetCurrentHeadShaAsync(
        RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        using var document = await GetPullRequestAsync(repo, prId, cancellationToken).ConfigureAwait(false);
        var head = CommitId(document.RootElement, "lastMergeSourceCommit");
        return string.IsNullOrWhiteSpace(head) ? null : head;
    }

    /// <summary>
    /// <c>GET .../pullrequests/{id}</c> — the single-PR resource both per-PR reads parse. The caller owns
    /// the returned document.
    /// </summary>
    private async Task<JsonDocument> GetPullRequestAsync(
        RepoIdentity repo, string prId, CancellationToken cancellationToken)
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
        _ = response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
