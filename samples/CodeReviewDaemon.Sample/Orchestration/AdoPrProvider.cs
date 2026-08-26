using System.Collections.Concurrent;
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
    private readonly int _maxPages;
    private readonly int _pageSize;

    /// <summary>
    /// Resolved project visibilities, keyed <c>{org}/{project}</c>. Keyed by PROJECT rather than by repo
    /// because visibility is a property of the project — two repos in one project share one answer, and
    /// asking twice would only ask the same question twice. The provider is a singleton, so this cache
    /// lives for the process: a project's visibility changes about as often as the project is renamed,
    /// while the daemon re-polls every repo every cycle, so caching is the difference between one lookup
    /// and one lookup per poll forever.
    /// <para>
    /// Only RECOGNIZED answers are cached. A 401 during a token refresh, a throttle, or an egress denial
    /// must not pin the gate closed for the life of the process — the next poll gets to ask again.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _projectVisibility = new(StringComparer.OrdinalIgnoreCase);

    public AdoPrProvider(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<AdoPrProvider> logger,
        int maxPagesPerPoll = MaxPages,
        int maxPrsPerPage = DefaultPageSize
    )
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;

        // Clamped rather than validated: a nonsensical page size should not stop the daemon polling, and
        // ADO rejects a $top above 1000 outright. The floor of 1 keeps a misconfigured 0 from turning every
        // poll into an empty result set, which would look exactly like "this repo has no open PRs".
        _maxPages = Math.Max(1, maxPagesPerPoll);
        _pageSize = Math.Clamp(maxPrsPerPage, 1, 1000);
    }

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

    public string Provider => "ado";

    /// <summary>Bounded pages per poll (PR #121 M5) so one poll can't spin unboundedly on a huge repo.
    /// Superseded by the constructor's <c>maxPagesPerPoll</c>, which carries the operator's configured
    /// <c>MaxPagesPerPoll</c>; this remains only as that parameter's default.</summary>
    private const int MaxPages = 10;

    /// <summary>
    /// Page size used when the operator configures none. Sending NO <c>$top</c> is what this replaces: ADO
    /// then applies its own documented default of 101, which silently capped every poll of a repo that had
    /// 707 active pull requests.
    /// </summary>
    private const int DefaultPageSize = 200;

    /// <summary>Max concurrent ADO <c>/pushes</c> recency lookups per poll — bounds the fan-out so a page full
    /// of old PRs doesn't serialize into minutes of round trips or trip ADO throttling.</summary>
    private const int MaxRecencyLookupConcurrency = 6;

    public async Task<PullRequestPage> ListOpenPullRequestsAsync(
        PrPollRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = request.Repo.OrgOrOwner;
        var project = request.Repo.Project;
        var repo = request.Repo.RepoName;
        // $top is EXPLICIT. Omitting it does not mean "no limit" — ADO applies its own default of 101, which
        // is indistinguishable in the response from a repo that simply has 101 open PRs. Measured on
        // Weve_DA/Nova: 707 active pull requests, of which one poll could see 101.
        var baseUrl =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/pullrequests"
            + $"?searchCriteria.status=active&$top={_pageSize}&api-version={ApiVersion}";

        var pullRequests = new List<PullRequestDescriptor>();
        var highWaterMark = 0L;

        // Paging is $skip-based, with continuation-token support kept for the case ADO ever sends one.
        //
        // The token alone does NOT work here, and that was measured rather than reasoned: with $top=200 a
        // poll of O365 Core/WeveNova returned exactly 200 PRs across ONE page and no continuation header,
        // while the repo has 552 active PRs. Exactly-the-page-size is a cap signature, not a count — the
        // same tell that hid the original 101. Because the loop's exit condition was "no token", stopping
        // early was also SILENT: the truncation warning could not fire on the very case that truncates.
        //
        // So the end of the list is inferred the only way this endpoint allows: a page that comes back
        // SHORTER than $top is the last one. A full final page costs one extra empty request, which is the
        // price of proving termination instead of assuming it.
        string? continuationToken = null;
        var pages = 0;
        var skip = 0;
        var lastPageCount = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages++;

            var url =
                continuationToken is not null ? $"{baseUrl}&continuationToken={Uri.EscapeDataString(continuationToken)}"
                : skip > 0 ? $"{baseUrl}&$skip={skip.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                : baseUrl;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url).WithOperation(
                SandboxOperation.ReadProviderMetadata
            );
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
                    rawPrs.Add(
                        new RawAdoPr(
                            pr.GetProperty("pullRequestId").GetInt64(),
                            CommitId(pr, "lastMergeSourceCommit"),
                            CommitId(pr, "lastMergeTargetCommit"),
                            ParseTimestamp(pr, "creationDate"),
                            pr.TryGetProperty("sourceRefName", out var srn) && srn.ValueKind is JsonValueKind.String
                                ? srn.GetString()
                                : null,
                            pr.GetProperty("status").GetString(),
                            MapDraftState(pr, "isDraft"),
                            // Who OPENED the PR. ADO's uniqueName is normally an email address; it is only
                            // carried as an opaque identity string here, and the consumer is responsible for
                            // reducing it to a safe, confined file name.
                            UniqueNameOf(pr, "createdBy"),
                            // What the PR SAYS it does. Without these the reviewer sees the diff but not the
                            // claim it is supposed to satisfy — on a revert whose files are all binaries, that
                            // leaves it nothing whatsoever to review against.
                            StringOf(pr, "title"),
                            StringOf(pr, "description"),
                            StringOf(pr, "targetRefName"),
                            // Confidentiality trust signal (design §6 Risk B). Both are read off the PR-list
                            // payload, and both stay null when the payload can't establish them, which
                            // PrPollingService collapses to the fail-closed value. Visibility additionally
                            // carries the EVIDENCE for why it could not be read, because the payload is
                            // documented not to carry it and the fallback below needs the failure attributable.
                            IsForkPr(pr),
                            IsTargetRepoPublic(pr, out var visibility),
                            visibility
                        )
                    );
                }

                // Phase 1b: resolve the target project's visibility where the payload could not.
                var projectVisibility = await ResolveMissingVisibilityAsync(
                        rawPrs,
                        org,
                        project,
                        repo,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                // Phase 2: resolve each PR's recency signal. ADO's PR list has no last-activity field, so a PR
                // created BEFORE the window needs one bounded `/pushes` lookup for its source branch's true
                // last-push time (so an old-but-recently-pushed PR is still reviewed). These lookups run with
                // bounded concurrency so a page full of old PRs doesn't serialize into minutes of round trips or
                // trip ADO throttling; recent PRs and PRs with no usable source ref make no call.
                var recency = await ResolveRecencySignalsAsync(
                        rawPrs,
                        org,
                        project,
                        repo,
                        request.RecencyCutoff,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                // Phase 3: build descriptors.
                for (var i = 0; i < rawPrs.Count; i++)
                {
                    var raw = rawPrs[i];
                    var (updatedAt, recencyCreatedAt) = recency[i];
                    pullRequests.Add(
                        new PullRequestDescriptor
                        {
                            PrId = raw.PrId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            HeadSha = raw.HeadSha,
                            BaseSha = raw.BaseSha,
                            // ADO's PR list exposes no last-activity timestamp, so a new source commit (the head
                            // SHA) is the re-review trigger; same-head comment threads do not re-trigger here.
                            TriggerWatermark = raw.HeadSha,
                            LifecycleState = MapLifecycle(raw.Status),
                            DraftState = raw.DraftState,
                            // Recency signals (consumed only by ApplyRecencyFilter): CreatedAt = opened date, but
                            // nulled for an old PR whose last push couldn't be dated (see ResolveRecencySignalsAsync)
                            // so the filter keeps it; UpdatedAt = last push, resolved only for PRs before the window.
                            CreatedAt = recencyCreatedAt,
                            UpdatedAt = updatedAt,
                            Author = raw.Author,
                            Title = raw.Title,
                            Description = raw.Description,
                            TargetBranch = ShortBranchName(raw.TargetRefName),
                            IsForkPr = raw.IsForkPr,
                            // The payload's answer wins where it has one (on-prem ADO Server, or a future API
                            // version that serializes it); otherwise the project API's. Still null when neither
                            // could answer — PrPollingService turns that into the fail-closed default.
                            IsTargetRepoPublic = raw.IsTargetRepoPublic ?? projectVisibility,
                        }
                    );

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
        } while (MoreMayRemain(continuationToken, lastPageCount, _pageSize) && pages < _maxPages);

        // A poll that stops while more may remain has NOT seen the repo's open PRs, and every downstream
        // filter — recency above all — can only ever filter what this list contained. Said out loud at
        // Warning because the failure it describes is invisible by construction: a truncated page and a
        // complete one are the same shape, which is how a 101-PR cap survived unnoticed on a repo with 707.
        if (MoreMayRemain(continuationToken, lastPageCount, _pageSize))
        {
            _logger.LogWarning(
                "ADO poll of {Org}/{Project}/{Repo} stopped after {Pages} page(s) of {PageSize} with more "
                    + "results still available; {Count} PR(s) were enumerated and the rest were NOT seen this "
                    + "poll. Raise CodeReviewDaemon:MaxPagesPerPoll or MaxPrsPerPage if this repeats.",
                org,
                project,
                repo,
                pages,
                _pageSize,
                pullRequests.Count
            );
        }

        _logger.LogDebug(
            "ADO poll of {Org}/{Project}/{Repo} returned {Count} active PR(s) across {Pages} page(s).",
            org,
            project,
            repo,
            pullRequests.Count,
            pages
        );

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
            out var parsed
        )
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

        return
            identity.TryGetProperty("uniqueName", out var value)
            && value.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    /// <summary>A direct string property, or null when absent, non-string, or blank. ADO omits
    /// <c>description</c> entirely for a PR opened without one, so absence and emptiness collapse to the
    /// same null — which the review brief renders as "(none)".</summary>
    private static string? StringOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>Strips ADO's <c>refs/heads/</c> prefix so a target branch reads the way a developer names
    /// it (<c>main</c>, not <c>refs/heads/main</c>). Any other ref shape — a tag, or a form we did not
    /// anticipate — is passed through unchanged rather than guessed at.</summary>
    private static string? ShortBranchName(string? refName) =>
        refName is { Length: > 0 } r && r.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? r["refs/heads/".Length..]
            : refName;

    /// <summary>
    /// Whether the PR was opened from a FORK of the target repo — one half of the confidentiality gate that
    /// decides whether a private sibling repo may be co-located beside this PR's diff.
    /// <para>
    /// Per the REST 7.1 <c>GitPullRequest</c> contract, <c>forkSource</c> is serialized ONLY for a PR whose
    /// source branch lives in a fork, so its absence is the "not a fork" signal. That inference is only sound
    /// when the payload is the shape we expect, so it is guarded on the <c>repository</c> object being present:
    /// a payload missing that too is a shape we do not recognize, and inferring "not a fork" from a shape we
    /// don't recognize is exactly how a gate opens by accident. That case returns null — "could not
    /// determine" — which <c>PrPollingService</c> collapses to the fail-closed value.
    /// </para>
    /// </summary>
    private static bool? IsForkPr(JsonElement pr) =>
        pr.TryGetProperty("repository", out var repository) && repository.ValueKind is JsonValueKind.Object
            ? pr.TryGetProperty("forkSource", out var forkSource) && forkSource.ValueKind is JsonValueKind.Object
            : null;

    /// <summary>
    /// Whether the project the PR's repo belongs to is publicly visible — the other half of the gate, read
    /// from <c>repository.project.visibility</c>, which the REST contract defines as <c>"private"</c> or
    /// <c>"public"</c>. Any other value, or a missing one, returns null rather than a guess: an
    /// unrecognized visibility must not read as "private".
    /// <para>
    /// ADO's PR-<b>list</b> serializes <c>project</c> as a shallow <c>TeamProjectReference</c> of
    /// <c>{id, name, state}</c> and omits <c>visibility</c> entirely — every sample response on the REST
    /// 7.1 "Get Pull Requests" page shows exactly those three keys — so on the cloud API this returns null
    /// for every PR and the caller resolves the answer from the project API instead. It is still read here
    /// first because Azure DevOps Server and future API versions may serialize it, and a payload that
    /// answers the question outright should not cost a round trip.
    /// </para>
    /// <para>
    /// <paramref name="evidence"/> records WHY it could not answer. The three causes need different
    /// remedies — a second call, a parser taught a new value, or a shape nobody has seen — and, undistinguished,
    /// all three surface as the identical fail-closed default with nothing to attribute a closed sibling
    /// gate to.
    /// </para>
    /// </summary>
    private static bool? IsTargetRepoPublic(JsonElement pr, out VisibilityEvidence evidence)
    {
        if (!pr.TryGetProperty("repository", out var repository) || repository.ValueKind is not JsonValueKind.Object)
        {
            evidence = new VisibilityEvidence(VisibilityGap.NoProject, "(no repository object)");
            return null;
        }

        if (!repository.TryGetProperty("project", out var project) || project.ValueKind is not JsonValueKind.Object)
        {
            evidence = new VisibilityEvidence(VisibilityGap.NoProject, PropertyNames(repository));
            return null;
        }

        if (StringOf(project, "visibility") is not { } visibility)
        {
            evidence = new VisibilityEvidence(VisibilityGap.VisibilityAbsent, PropertyNames(project));
            return null;
        }

        if (MapVisibility(visibility) is { } answer)
        {
            evidence = VisibilityEvidence.Answered;
            return answer;
        }

        evidence = new VisibilityEvidence(VisibilityGap.VisibilityUnrecognized, visibility);
        return null;
    }

    /// <summary>
    /// Maps an ADO <c>ProjectVisibility</c> to "is this project visible OUTSIDE the organization?" — the
    /// only question the confidentiality gate actually asks. Shared by both readers (the PR payload and the
    /// project API) so one enum cannot end up with two parsers that drift.
    /// <para>
    /// <c>ProjectVisibility</c> is not two-valued. Besides <c>private</c> and <c>public</c> it defines
    /// <c>organization</c>, plus the <c>systemprivate</c> and <c>unchanged</c> sentinels — and a parser that
    /// knew only the first two is what left the gate shut on a private corporate org for 143 runs.
    /// </para>
    /// <para>
    /// <c>organization</c> maps to NOT public. The gate exists to stop an untrusted PR getting sibling
    /// repositories co-located beside it, where a prompt-injected agent could read one and surface it in the
    /// review it posts. A project visible only to members of the organization is the same trust domain as
    /// the repository under review, so the boundary that matters is org-EXTERNAL, not repo-external.
    /// <c>public</c> means internet-visible and stays true.
    /// </para>
    /// <para>
    /// Anything else — including <c>unchanged</c> (ADO's "no value supplied" sentinel, which is what the
    /// PR-list projection actually sends) and <c>systemprivate</c> — returns null, which stays unknown and
    /// fails closed. A value nobody has ruled on must never be guessed into a trust decision.
    /// </para>
    /// </summary>
    private static bool? MapVisibility(string? visibility) =>
        visibility?.ToLowerInvariant() switch
        {
            "public" => true,
            "private" => false,
            "organization" => false,
            _ => null,
        };

    /// <summary>Why the PR-list payload could not establish the target project's visibility.</summary>
    private enum VisibilityGap
    {
        /// <summary>It did establish it; there is nothing to explain.</summary>
        None,

        /// <summary>No <c>repository.project</c> object to read a visibility from at all.</summary>
        NoProject,

        /// <summary>A <c>repository.project</c> object carrying no <c>visibility</c> property. This is the
        /// documented ADO cloud shape, not an anomaly.</summary>
        VisibilityAbsent,

        /// <summary>A <c>visibility</c> that is neither <c>"public"</c> nor <c>"private"</c>.</summary>
        VisibilityUnrecognized,
    }

    /// <summary>
    /// What the payload offered where the visibility should have been. <paramref name="Observed"/> holds
    /// property NAMES for the two "it wasn't there" cases and the unmapped value itself for the third —
    /// never a property value from the payload at large, which is full of PR titles, descriptions and
    /// author identities that have no business in a log.
    /// </summary>
    private sealed record VisibilityEvidence(VisibilityGap Gap, string Observed)
    {
        public static readonly VisibilityEvidence Answered = new(VisibilityGap.None, string.Empty);
    }

    /// <summary>The names (never the values) of an object's properties, for diagnostics.</summary>
    private static string PropertyNames(JsonElement element) =>
        string.Join(", ", element.EnumerateObject().Select(static p => p.Name));

    /// <summary>
    /// Resolves the visibility the PR-list payload could not, for the whole page at once: the question is
    /// "is the polled PROJECT public?", which is the same question for every PR on the page, so it is asked
    /// once (and then cached for the process). Returns null when nothing could answer, and logs one line
    /// per distinct cause so a closed cross-repo sibling gate is attributable rather than merely observed.
    /// <para>
    /// The project comes from the poll request, not the payload: <c>{org}/{project}/_apis/git/repositories/{repo}</c>
    /// is the route being polled, and ADO defines <c>repository</c> as the repo containing the PR's TARGET
    /// branch — the repo we asked about. The two can never disagree, and one of them is a shape we have
    /// just established we cannot read the answer from.
    /// </para>
    /// </summary>
    private async Task<bool?> ResolveMissingVisibilityAsync(
        IReadOnlyList<RawAdoPr> prs,
        string org,
        string? project,
        string repo,
        CancellationToken cancellationToken
    )
    {
        var unresolved = prs.Where(static p => p.IsTargetRepoPublic is null).ToList();
        if (unresolved.Count == 0)
        {
            return null;
        }

        var resolved = await TryGetProjectVisibilityAsync(org, project, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            return resolved;
        }

        // Nothing established it. Group by evidence so a page of 50 PRs sharing one cause yields one line.
        foreach (var group in unresolved.GroupBy(static p => p.Visibility))
        {
            _logger.LogWarning(
                "Could not establish the target project's visibility for {PrCount} PR(s) on "
                    + "{Org}/{Project}/{Repo}: {Cause}, and the org-scoped project API did not answer either. "
                    + "Observed where the visibility should have been: {Observed}. The confidentiality trust "
                    + "signal therefore stays unknown and collapses to its fail-closed default, so no "
                    + "cross-repo sibling is co-located for these runs.",
                group.Count(),
                org,
                project,
                repo,
                DescribeGap(group.Key.Gap),
                group.Key.Observed
            );
        }

        return null;
    }

    /// <summary>The cause phrase for a <see cref="VisibilityGap"/>, written so the reader can tell which
    /// remedy applies without opening the code.</summary>
    private static string DescribeGap(VisibilityGap gap) =>
        gap switch
        {
            VisibilityGap.NoProject => "the PR-list payload carried no repository.project object",
            VisibilityGap.VisibilityAbsent =>
                "the PR-list payload's repository.project carried no visibility property (the documented ADO cloud shape)",
            VisibilityGap.VisibilityUnrecognized =>
                "repository.project.visibility carried a value this parser does not map to public/private",
            _ => "the visibility was established (this line should not have been logged)",
        };

    /// <summary>
    /// Reads a project's visibility from ADO's org-scoped project API
    /// (<c>GET /{org}/_apis/projects/{project}</c>), whose <c>TeamProject</c> — unlike the shallow
    /// <c>TeamProjectReference</c> embedded in a PR — does serialize <c>visibility</c>.
    /// <para>
    /// Returns null on anything that isn't a recognized answer — no project in the request, non-success,
    /// missing field, unrecognized value, denial, or transient failure — which leaves the trust signal
    /// unknown and the gate fail-closed. Every failure is swallowed for the same reason
    /// <see cref="TryGetLastPushDateAsync"/> swallows its own: losing this signal costs a run its
    /// cross-repo siblings, whereas letting the exception escape would fault the whole poll and cost every
    /// PR on the page its review.
    /// </para>
    /// </summary>
    private async Task<bool?> TryGetProjectVisibilityAsync(
        string org,
        string? project,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrEmpty(project))
        {
            return null;
        }

        var key = $"{org}/{project}";
        if (_projectVisibility.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var url = $"{BaseUrl}/{org}/_apis/projects/{Uri.EscapeDataString(project)}" + $"?api-version={ApiVersion}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url).WithOperation(
                SandboxOperation.ReadProviderMetadata
            );
            var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            httpRequest.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "ADO project fetch for {Org}/{Project} returned {Status}; visibility stays unknown.",
                    org,
                    project,
                    (int)response.StatusCode
                );
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var raw =
                document.RootElement.ValueKind is JsonValueKind.Object
                    ? StringOf(document.RootElement, "visibility")
                    : null;

            if (MapVisibility(raw) is { } answer)
            {
                _projectVisibility[key] = answer;
                _logger.LogDebug(
                    "Resolved {Org}/{Project} visibility from the project API: {Visibility} (public={IsPublic}).",
                    org,
                    project,
                    raw,
                    answer
                );
                return answer;
            }

            // The call SUCCEEDED and still could not answer — the one give-up path that used to say nothing
            // at all, because neither the non-success nor the exception branch covers it. That silence is
            // what left run 143 able to report the PR-list sentinel but not why the fallback failed to
            // rescue it. The value is named because it IS the remedy: a parser cannot be taught a value
            // nobody wrote down. It is a visibility enum, not EUII.
            _logger.LogDebug(
                "ADO project API answered for {Org}/{Project} but carried no visibility this parser maps to "
                    + "a trust decision (got {Visibility}); visibility stays unknown and the confidentiality "
                    + "gate fails closed.",
                org,
                project,
                raw is null ? "(absent or non-object body)" : raw
            );
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real caller cancellation (poll aborted) — propagate; nobody is waiting for this result.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else — an egress denial from the operation policy, an HttpClient TIMEOUT (which
            // surfaces as a TaskCanceledException even though the caller's token was NOT cancelled), a
            // malformed body — leaves the visibility unknown, which fails closed. It must never fault the poll.
            _logger.LogDebug(
                ex,
                "ADO project fetch for {Org}/{Project} failed; visibility stays unknown.",
                org,
                project
            );
            return null;
        }
    }

    /// <summary>Raw per-PR metadata materialized from one ADO PR-list page, before the async recency
    /// resolution (a <see cref="JsonElement"/> can't outlive its document).</summary>
    private sealed record RawAdoPr(
        long PrId,
        string HeadSha,
        string BaseSha,
        DateTimeOffset? CreatedAt,
        string? SourceRefName,
        string? Status,
        PrDraftState DraftState,
        string? Author,
        string? Title,
        string? Description,
        string? TargetRefName,
        bool? IsForkPr,
        bool? IsTargetRepoPublic,
        VisibilityEvidence Visibility
    );

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
        CancellationToken cancellationToken
    )
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
        CancellationToken cancellationToken
    )
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
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url =
                $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/pushes"
                + $"?searchCriteria.refName={Uri.EscapeDataString(sourceRefName)}"
                + $"&$top=1&api-version={ApiVersion}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url).WithOperation(
                SandboxOperation.ReadProviderMetadata
            );
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
                    (int)response.StatusCode
                );
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (
                !document.RootElement.TryGetProperty("value", out var pushes)
                || pushes.ValueKind is not JsonValueKind.Array
            )
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
    public async Task<PrStatus> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(prId);

        var org = repo.OrgOrOwner;
        var project = repo.Project;
        var repoName = repo.RepoName;
        var url =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repoName}/pullrequests/{prId}"
            + $"?api-version={ApiVersion}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url).WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var status =
            root.TryGetProperty("status", out var statusValue) && statusValue.ValueKind is JsonValueKind.String
                ? statusValue.GetString()
                : null;
        return new PrStatus(MapPrLifecycle(status), MapDraftState(root, "isDraft"));
    }

    /// <summary>
    /// Maps ADO's single-PR <c>status</c> to <see cref="PrLifecycle"/>: <c>active</c> is Open,
    /// <c>completed</c> is Merged, <c>abandoned</c> is Abandoned. An unrecognized status is treated as Open
    /// so the sweep leaves the notes branch untouched rather than risk a wrong merge or delete.
    /// </summary>
    private static PrLifecycle MapPrLifecycle(string? status) =>
        status switch
        {
            "active" => PrLifecycle.Open,
            "completed" => PrLifecycle.Merged,
            "abandoned" => PrLifecycle.Abandoned,
            _ => PrLifecycle.Open,
        };

    private static PrDraftState MapDraftState(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
                ? PrDraftState.Draft
                : PrDraftState.Ready
            : PrDraftState.Unknown;

    private static PrLifecycleState MapLifecycle(string? status) =>
        status switch
        {
            "active" => PrLifecycleState.Open,
            "completed" => PrLifecycleState.Merged,
            "abandoned" => PrLifecycleState.Abandoned,
            _ => PrLifecycleState.Closed,
        };
}
