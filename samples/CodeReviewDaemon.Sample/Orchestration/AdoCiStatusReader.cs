using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>What the PR's continuous-integration build is doing, as far as the daemon could establish it.</summary>
internal enum AdoCiState
{
    /// <summary>
    /// Nothing established the CI state — the call was denied, non-success, or a shape this parser cannot
    /// read. Deliberately the default (value 0) so a record nobody filled in reads as "unknown", never as a
    /// healthy pipeline: a brief that claims a green build nobody read is worse than a brief with no CI line
    /// at all.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The PR has no Build policy, so there is no pipeline to report. Distinct from
    /// <see cref="Succeeded"/> — "CI passed" and "there is no CI" call for opposite reviewer behaviour.
    /// <para>UNVERIFIED: no PR in the sample this reader was built against lacked a Build policy, so this
    /// arm is pinned by test against the documented evaluation shape rather than an observed response.</para>
    /// </summary>
    NoBuildPolicy,

    /// <summary>A Build policy exists but has not produced a build for this iteration (the evaluation carries
    /// no <c>buildId</c>). CI has not run — there is nothing to have passed.</summary>
    NotStarted,

    /// <summary>A build exists and has not finished.</summary>
    Running,

    /// <summary>The build completed and succeeded.</summary>
    Succeeded,

    /// <summary>The build completed and did not succeed — <c>failed</c>, <c>canceled</c>, or
    /// <c>partiallySucceeded</c>.</summary>
    Failed,
}

/// <summary>
/// The PR's CI verdict, bounded so it can be dropped straight into a review brief. Every field is what the
/// daemon actually read: a value it could not establish stays <c>null</c> rather than defaulting to a number
/// the brief would render as fact.
/// </summary>
internal sealed record AdoCiStatus
{
    /// <summary>The overall verdict. See <see cref="AdoCiState"/>.</summary>
    public required AdoCiState State { get; init; }

    /// <summary>The build the PR's Build policy produced, or <c>null</c> when none ran.</summary>
    public string? BuildId { get; init; }

    /// <summary>ADO's raw build <c>status</c> (<c>completed</c>, <c>inProgress</c>, …).</summary>
    public string? BuildStatus { get; init; }

    /// <summary>ADO's raw build <c>result</c> (<c>succeeded</c>, <c>failed</c>, …); <c>null</c> while the
    /// build is still running, because an unfinished build has no result.</summary>
    public string? BuildResult { get; init; }

    /// <summary>Tests the build ran; <c>null</c> when no summary could be read.</summary>
    public int? TotalTests { get; init; }

    /// <summary>Tests that passed; <c>null</c> when no summary could be read.</summary>
    public int? PassedTests { get; init; }

    /// <summary>Tests that failed; <c>null</c> when no summary could be read.</summary>
    public int? FailedTests { get; init; }

    /// <summary>
    /// The build timeline's ERROR issues, de-duplicated, capped and truncated. On a test failure this is
    /// where ADO names the project — the line that mattered on PR 5505458 read
    /// <c>"…\TagService\TagService.UnitTests_Retail_Amd64__TEST Attempt: [2], 1 of 1 tests failed."</c> — but
    /// the same field carries a compile error or a task failure when that is what broke, so it is build issue
    /// text and not a list of test-case names.
    /// </summary>
    public IReadOnlyList<string> FailureMessages { get; init; } = [];

    /// <summary>
    /// How many distinct error issues the cap dropped. Reported rather than silently elided: a cut list reads
    /// to the reviewer as the complete set of failures, which is the one thing it must not do.
    /// </summary>
    public int OmittedFailureMessages { get; init; }

    /// <summary>Nothing could be established. Also the shape returned when the read throws.</summary>
    public static readonly AdoCiStatus Unavailable = new() { State = AdoCiState.Unavailable };
}

/// <summary>
/// Reads a pull request's CI build and test results from Azure DevOps so the review brief can state them.
/// <para>
/// It exists because the reviewer had no pipeline signal at all and filled the gap with the author's own
/// claim — run 22 wrote "The PR commit states that representative restore and build validation succeeded"
/// — while on PR 5505458 the pipeline had already reported 45,051 tests, 45,050 passed, 1 failed, with the
/// failing project named in the build timeline. No sandbox reproduces that; ADO had it the whole time.
/// </para>
/// <para>
/// Three GETs in the common case, and fewer on the paths that do not need them: the policy evaluation names
/// the build (or shows there is none), the build object gives status/result, the test summary gives the
/// counts, and the timeline — 68 records on a real build, so fetched only when something actually failed —
/// gives the failing names. Requests go through the injected policy-enforced <see cref="HttpClient"/> tagged
/// <see cref="SandboxOperation.ReadProviderMetadata"/>, exactly as the other ADO readers do, so the same
/// per-run <see cref="OperationPolicy"/> that confines them confines this.
/// </para>
/// <para>
/// Nothing here throws for a failed read. Losing CI costs the brief a line; letting the failure escape would
/// cost the PR its review, which is the trade <c>AdoPrProvider</c>'s metadata lookups already make.
/// </para>
/// </summary>
internal sealed class AdoCiStatusReader
{
    private const string BaseUrl = "https://dev.azure.com";
    private const string ApiVersion = "7.1";

    /// <summary>
    /// The version the policy-evaluation and test-summary routes require. NOT interchangeable with
    /// <see cref="ApiVersion"/>: plain <c>api-version=7.1</c> on <c>_apis/policy/evaluations</c> returns 400.
    /// </summary>
    private const string PreviewApiVersion = "7.1-preview.1";

    /// <summary>
    /// Cap on the failure messages carried into the brief. A build timeline holds ~68 records on a real
    /// pipeline and a broken build can put an error on many of them; the brief has room for the shape of the
    /// failure, not for the log.
    /// </summary>
    public const int MaxFailureMessages = 10;

    /// <summary>Cap on each message. A single MSBuild error can be a several-KB dump, and one of those would
    /// crowd out everything else the brief carries.</summary>
    public const int MaxFailureMessageChars = 300;

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<AdoCiStatusReader> _logger;

    /// <summary>
    /// Resolved project GUIDs, keyed <c>{org}/{project}</c>. A project's GUID never changes, and the reader
    /// is used once per review on a small set of repos, so one lookup per project per process is the whole
    /// cost. Mirrors <c>AdoPrProvider</c>'s visibility cache, including its rule: only recognized answers are
    /// cached, so a 401 during a token refresh does not pin CI off for the life of the daemon.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _projectIds = new(StringComparer.OrdinalIgnoreCase);

    public AdoCiStatusReader(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<AdoCiStatusReader> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads the CI verdict for one pull request.
    /// </summary>
    /// <param name="repo">The run's repository identity; supplies the org and project the routes are built from.</param>
    /// <param name="prId">The pull request id.</param>
    /// <param name="projectId">
    /// The project's GUID, which is what ADO keys the PR's policy artifact by. Pass <c>null</c> when the
    /// caller does not have it — neither <see cref="RepoIdentity"/> nor a persisted run carries one — and it
    /// is resolved from the org-scoped project API, a route the operation policy already permits for the
    /// visibility lookup.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The verdict, or <see cref="AdoCiStatus.Unavailable"/> when nothing could establish it. Never throws for
    /// a failed read.
    /// </returns>
    public async Task<AdoCiStatus> ReadAsync(
        RepoIdentity repo,
        string prId,
        string? projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(prId);

        if (string.IsNullOrEmpty(repo.Project))
        {
            // Every route below is /{org}/{project}/_apis/…, and the operation policy's CI exception is
            // likewise built per project. Without one there is nothing to ask and nothing that would be
            // allowed; returning early keeps that a silent no-op rather than three denied requests.
            return AdoCiStatus.Unavailable;
        }

        try
        {
            var resolvedProjectId = projectId;
            if (string.IsNullOrEmpty(resolvedProjectId))
            {
                resolvedProjectId = await TryGetProjectIdAsync(repo, cancellationToken).ConfigureAwait(false);
                if (resolvedProjectId is null)
                {
                    return AdoCiStatus.Unavailable;
                }
            }

            var buildId = await TryGetPolicyBuildIdAsync(repo, prId, resolvedProjectId, cancellationToken)
                .ConfigureAwait(false);
            if (buildId.Outcome is not BuildLookup.Found)
            {
                // queued / no-Build-policy are answered by the evaluation alone. Fetching the build object to
                // discover there isn't one would be a round trip that can only confirm what we already read.
                return new AdoCiStatus
                {
                    State = buildId.Outcome switch
                    {
                        BuildLookup.NoBuildPolicy => AdoCiState.NoBuildPolicy,
                        BuildLookup.NotStarted => AdoCiState.NotStarted,
                        _ => AdoCiState.Unavailable,
                    },
                };
            }

            var build = await ReadBuildAsync(repo, buildId.BuildId!, cancellationToken).ConfigureAwait(false);
            var tests = await ReadTestSummaryAsync(repo, buildId.BuildId!, cancellationToken).ConfigureAwait(false);

            // The timeline is fetched ONLY when there is a failure to name. On a green build every one of its
            // ~68 records is noise, and the second condition covers the lenient-pipeline case where the build
            // is allowed to report success with failing tests underneath it.
            (IReadOnlyList<string> Messages, int Omitted) failures =
                build.State is AdoCiState.Failed || tests.Failed > 0
                    ? await ReadFailureMessagesAsync(repo, buildId.BuildId!, cancellationToken).ConfigureAwait(false)
                    : ([], 0);

            var status = new AdoCiStatus
            {
                State = build.State,
                BuildId = buildId.BuildId,
                BuildStatus = build.Status,
                BuildResult = build.Result,
                TotalTests = tests.Total,
                PassedTests = tests.Passed,
                FailedTests = tests.Failed,
                FailureMessages = failures.Messages,
                OmittedFailureMessages = failures.Omitted,
            };

            _logger.LogDebug(
                "Read ADO CI for {Org}/{Project} PR {PrId}: {State} (build {BuildId} {BuildStatus}/{BuildResult}), "
                    + "{TotalTests} test(s), {FailedTests} failed, {FailureCount} failure message(s) "
                    + "({OmittedCount} over the cap).",
                repo.OrgOrOwner,
                repo.Project,
                prId,
                status.State,
                status.BuildId,
                status.BuildStatus,
                status.BuildResult,
                status.TotalTests,
                status.FailedTests,
                status.FailureMessages.Count,
                status.OmittedFailureMessages);

            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real caller cancellation (the review was abandoned) — propagate; nobody wants this result.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else — an egress denial from the operation policy, an HttpClient TIMEOUT (which
            // surfaces as a TaskCanceledException even though the caller's token was NOT cancelled), a
            // malformed body — leaves CI unknown. The brief then says so, which is the honest outcome; it must
            // never fault the review.
            _logger.LogDebug(
                ex,
                "ADO CI read for {Org}/{Project} PR {PrId} failed; CI status stays unavailable.",
                repo.OrgOrOwner,
                repo.Project,
                prId);
            return AdoCiStatus.Unavailable;
        }
    }

    /// <summary>How the policy evaluation answered "which build is this PR's?".</summary>
    private enum BuildLookup
    {
        /// <summary>The evaluation could not be read at all.</summary>
        Unavailable,

        /// <summary>No evaluation carried a <c>Build</c> policy.</summary>
        NoBuildPolicy,

        /// <summary>A Build policy exists but none of its evaluations names a build.</summary>
        NotStarted,

        /// <summary>A build id was found.</summary>
        Found,
    }

    /// <summary>
    /// Finds the build the PR's Build policy produced, from
    /// <c>GET /{org}/{project}/_apis/policy/evaluations?artifactId=vstfs:///CodeReview/CodeReviewId/{projectId}/{prId}</c>.
    /// <para>
    /// A PR can carry SEVERAL Build evaluations — 5505458 had two, one <c>queued</c> and one <c>rejected</c>
    /// — so the choice is not "the first one". A <c>rejected</c> evaluation wins outright, because it is the
    /// one the reviewer must be told about and the one whose build has the failure on it; otherwise the first
    /// evaluation that names a build wins. Taking the first entry regardless would have reported "CI has not
    /// run" on a PR whose pipeline had already failed.
    /// </para>
    /// </summary>
    private async Task<(BuildLookup Outcome, string? BuildId)> TryGetPolicyBuildIdAsync(
        RepoIdentity repo,
        string prId,
        string projectId,
        CancellationToken cancellationToken)
    {
        var artifactId = $"vstfs:///CodeReview/CodeReviewId/{projectId}/{prId}";
        var url =
            $"{BaseUrl}/{repo.OrgOrOwner}/{repo.Project}/_apis/policy/evaluations"
            + $"?artifactId={Uri.EscapeDataString(artifactId)}"
            + $"&api-version={PreviewApiVersion}";

        using var document = await GetJsonAsync(url, "policy evaluations", cancellationToken).ConfigureAwait(false);
        if (document is null
            || !document.RootElement.TryGetProperty("value", out var evaluations)
            || evaluations.ValueKind is not JsonValueKind.Array)
        {
            return (BuildLookup.Unavailable, null);
        }

        var sawBuildPolicy = false;
        string? firstBuildId = null;
        foreach (var evaluation in evaluations.EnumerateArray())
        {
            if (!IsBuildPolicy(evaluation))
            {
                continue;
            }

            sawBuildPolicy = true;
            var buildId = BuildIdOf(evaluation);
            if (buildId is null)
            {
                continue;
            }

            if (string.Equals(StringOf(evaluation, "status"), "rejected", StringComparison.OrdinalIgnoreCase))
            {
                return (BuildLookup.Found, buildId);
            }

            firstBuildId ??= buildId;
        }

        if (!sawBuildPolicy)
        {
            _logger.LogDebug(
                "ADO PR {PrId} on {Org}/{Project} has no Build policy; there is no pipeline to report.",
                prId,
                repo.OrgOrOwner,
                repo.Project);
            return (BuildLookup.NoBuildPolicy, null);
        }

        return firstBuildId is null ? (BuildLookup.NotStarted, null) : (BuildLookup.Found, firstBuildId);
    }

    /// <summary>
    /// True when an evaluation's policy is the Build policy, matched on
    /// <c>configuration.type.displayName == "Build"</c>. Matched on the display name rather than the type
    /// GUID because that is the field the observed payloads carry and the one a reader can verify by eye.
    /// </summary>
    private static bool IsBuildPolicy(JsonElement evaluation) =>
        evaluation.ValueKind is JsonValueKind.Object
        && evaluation.TryGetProperty("configuration", out var configuration)
        && configuration.ValueKind is JsonValueKind.Object
        && configuration.TryGetProperty("type", out var type)
        && type.ValueKind is JsonValueKind.Object
        && string.Equals(StringOf(type, "displayName"), "Build", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The build named by an evaluation's <c>context.buildId</c>, or <c>null</c> when it names none (the
    /// "CI never ran" signal). Rendered invariantly from the NUMBER ADO sent rather than passed through as
    /// text, so nothing shaped like a path or a query can reach the build URL built from it.
    /// </summary>
    private static string? BuildIdOf(JsonElement evaluation)
    {
        if (!evaluation.TryGetProperty("context", out var context)
            || context.ValueKind is not JsonValueKind.Object
            || !context.TryGetProperty("buildId", out var buildId))
        {
            return null;
        }

        return buildId.ValueKind switch
        {
            JsonValueKind.Number when buildId.TryGetInt64(out var id) =>
                id.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String when long.TryParse(
                buildId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) =>
                id.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>
    /// Reads the build object (<c>GET .../_apis/build/builds/{buildId}</c>) and maps its status/result to an
    /// <see cref="AdoCiState"/>. The verdict comes from the BUILD, not from the policy evaluation that named
    /// it, so there is one source of truth for "did CI pass" once a build exists.
    /// </summary>
    private async Task<(AdoCiState State, string? Status, string? Result)> ReadBuildAsync(
        RepoIdentity repo,
        string buildId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BaseUrl}/{repo.OrgOrOwner}/{repo.Project}/_apis/build/builds/{buildId}"
            + $"?api-version={ApiVersion}";

        using var document = await GetJsonAsync(url, "build", cancellationToken).ConfigureAwait(false);
        if (document is null || document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            return (AdoCiState.Unavailable, null, null);
        }

        var status = StringOf(document.RootElement, "status");
        var result = StringOf(document.RootElement, "result");

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            // notStarted / inProgress / cancelling / postponed all mean the same thing to a reviewer: the
            // verdict is not in yet. An unfinished build carries no result, so none is reported.
            return (AdoCiState.Running, status, null);
        }

        var state = result?.ToLowerInvariant() switch
        {
            "succeeded" => AdoCiState.Succeeded,
            // partiallySucceeded means something in the pipeline failed and the build was allowed to carry on
            // — which is precisely the case the reviewer must see, not one to round up to green.
            "failed" or "canceled" or "partiallysucceeded" => AdoCiState.Failed,
            // A result nobody has ruled on is not guessed into a verdict. The raw value is still carried on
            // the record so the brief can print it, and named here because it IS the remedy: a parser cannot
            // be taught a value nobody wrote down. It is a build-result enum, not EUII.
            _ => AdoCiState.Unavailable,
        };

        if (state is AdoCiState.Unavailable)
        {
            _logger.LogDebug(
                "ADO build {BuildId} on {Org}/{Project} completed with a result this parser does not map to a "
                    + "verdict (got {BuildResult}); CI status stays unavailable.",
                buildId,
                repo.OrgOrOwner,
                repo.Project,
                result ?? "(absent)");
        }

        return (state, status, result);
    }

    /// <summary>
    /// Reads the build's test totals from <c>GET .../_apis/test/ResultSummaryByBuild?buildId={id}</c>. The
    /// path is <c>_apis/test/ResultSummaryByBuild</c>: the similarly-named
    /// <c>_apis/testresults/resultsummarybybuild</c> 404s on this org.
    /// <para>
    /// Each count stays <c>null</c> when the payload does not carry it, so "the build ran no tests yet"
    /// (a genuine zero, which is what a running build reports) stays distinguishable from "nothing could read
    /// the summary". A brief that renders those the same way tells the reviewer a build ran no tests when in
    /// fact nobody looked.
    /// </para>
    /// </summary>
    private async Task<(int? Total, int? Passed, int? Failed)> ReadTestSummaryAsync(
        RepoIdentity repo,
        string buildId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BaseUrl}/{repo.OrgOrOwner}/{repo.Project}/_apis/test/ResultSummaryByBuild"
            + $"?buildId={buildId}"
            + $"&api-version={PreviewApiVersion}";

        using var document = await GetJsonAsync(url, "test summary", cancellationToken).ConfigureAwait(false);
        if (document is null
            || document.RootElement.ValueKind is not JsonValueKind.Object
            || !document.RootElement.TryGetProperty("aggregatedResultsAnalysis", out var analysis)
            || analysis.ValueKind is not JsonValueKind.Object)
        {
            _logger.LogDebug(
                "ADO test summary for build {BuildId} on {Org}/{Project} carried no aggregatedResultsAnalysis; "
                    + "test counts stay unknown.",
                buildId,
                repo.OrgOrOwner,
                repo.Project);
            return (null, null, null);
        }

        var total = analysis.TryGetProperty("totalTests", out var totalTests)
            && totalTests.ValueKind is JsonValueKind.Number
            && totalTests.TryGetInt32(out var parsedTotal)
                ? parsedTotal
                : (int?)null;

        if (!analysis.TryGetProperty("resultsByOutcome", out var byOutcome)
            || byOutcome.ValueKind is not JsonValueKind.Object)
        {
            return (total, null, null);
        }

        // An outcome with no results is OMITTED from resultsByOutcome rather than sent as zero — a build with
        // nothing failing has no "Failed" bucket at all. So an absent bucket under a present resultsByOutcome
        // is a real zero, while an absent resultsByOutcome (above) is genuinely unknown.
        return (total, OutcomeCount(byOutcome, "Passed"), OutcomeCount(byOutcome, "Failed"));
    }

    /// <summary>The <c>count</c> under one <c>resultsByOutcome</c> bucket, or 0 when the bucket is absent.
    /// Matched case-insensitively — the keys are outcome names, not a contract this reader should hinge on.</summary>
    private static int OutcomeCount(JsonElement byOutcome, string outcome)
    {
        foreach (var bucket in byOutcome.EnumerateObject())
        {
            if (!string.Equals(bucket.Name, outcome, StringComparison.OrdinalIgnoreCase)
                || bucket.Value.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            return bucket.Value.TryGetProperty("count", out var count)
                && count.ValueKind is JsonValueKind.Number
                && count.TryGetInt32(out var parsed)
                    ? parsed
                    : 0;
        }

        return 0;
    }

    /// <summary>
    /// Pulls the failure text off the build timeline (<c>GET .../_apis/build/builds/{id}/timeline</c>) — the
    /// only place ADO names WHAT failed. On 5505458 the timeline held 68 records and 3 error issues, one of
    /// which read
    /// <c>"clr\src\Plane0\MetricLibrary\TagService\TagService.UnitTests_Retail_Amd64__TEST Attempt: [2], 1 of 1 tests failed."</c>
    /// <para>
    /// Bounded three ways, because the brief is the scarce resource: only <c>error</c> issues (a warning is
    /// noise in a review), each message collapsed to one line and truncated, the list de-duplicated (ADO
    /// repeats an error on a task record and again on its parent job) and then capped — with the drop counted,
    /// so a cut list can never read as the complete set of failures.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<string> Messages, int Omitted)> ReadFailureMessagesAsync(
        RepoIdentity repo,
        string buildId,
        CancellationToken cancellationToken)
    {
        var url =
            $"{BaseUrl}/{repo.OrgOrOwner}/{repo.Project}/_apis/build/builds/{buildId}/timeline"
            + $"?api-version={ApiVersion}";

        using var document = await GetJsonAsync(url, "build timeline", cancellationToken).ConfigureAwait(false);
        if (document is null
            || !document.RootElement.TryGetProperty("records", out var records)
            || records.ValueKind is not JsonValueKind.Array)
        {
            return ([], 0);
        }

        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("issues", out var issues) || issues.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            foreach (var issue in issues.EnumerateArray())
            {
                if (!string.Equals(StringOf(issue, "type"), "error", StringComparison.OrdinalIgnoreCase)
                    || StringOf(issue, "message") is not { } message)
                {
                    continue;
                }

                var condensed = Condense(message);
                if (seen.Add(condensed))
                {
                    distinct.Add(condensed);
                }
            }
        }

        return distinct.Count <= MaxFailureMessages
            ? (distinct, 0)
            : (distinct[..MaxFailureMessages], distinct.Count - MaxFailureMessages);
    }

    /// <summary>
    /// Collapses one build issue to a single truncated line. The line collapse is not cosmetic: these are
    /// rendered as list items in a brief whose structure the reviewer reads as fact, and a multi-line MSBuild
    /// error would forge entries the daemon never wrote.
    /// </summary>
    private static string Condense(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxFailureMessageChars ? line : line[..(MaxFailureMessageChars - 1)] + "…";
    }

    /// <summary>
    /// Resolves the project's GUID from <c>GET /{org}/_apis/projects/{project}</c> — the route
    /// <c>AdoPrProvider</c> already reads visibility from, and one the operation policy already permits, so
    /// this adds no reach. Needed because ADO keys a PR's policy artifact by project GUID and nothing on a
    /// <see cref="RepoIdentity"/> or a persisted run carries one.
    /// </summary>
    private async Task<string?> TryGetProjectIdAsync(RepoIdentity repo, CancellationToken cancellationToken)
    {
        var key = $"{repo.OrgOrOwner}/{repo.Project}";
        if (_projectIds.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var url =
            $"{BaseUrl}/{repo.OrgOrOwner}/_apis/projects/{Uri.EscapeDataString(repo.Project!)}"
            + $"?api-version={ApiVersion}";

        using var document = await GetJsonAsync(url, "project", cancellationToken).ConfigureAwait(false);
        var id = document is not null && document.RootElement.ValueKind is JsonValueKind.Object
            ? StringOf(document.RootElement, "id")
            : null;

        if (id is null)
        {
            _logger.LogDebug(
                "Could not resolve the project id for {Org}/{Project}; CI status stays unavailable.",
                repo.OrgOrOwner,
                repo.Project);
            return null;
        }

        _projectIds[key] = id;
        return id;
    }

    /// <summary>
    /// One authenticated GET, parsed. Returns <c>null</c> on a non-success status so each caller degrades to
    /// its own "could not establish" answer rather than throwing; transport and parse failures still throw and
    /// are caught once, in <see cref="ReadAsync"/>.
    /// <para>
    /// ADO authenticates REST with HTTP basic carrying the token in the PASSWORD field (the username is
    /// ignored), so the bearer is sent base64-encoded as <c>:{token}</c> — the same shape
    /// <c>AdoPrProvider</c> and <c>AdoReviewCommentPublisher</c> use. The token is fetched per request so an
    /// expiry mid-read refreshes rather than 401s.
    /// </para>
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, string label, CancellationToken cancellationToken)
    {
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
                "ADO CI {Label} fetch returned {Status}; that part of the CI status stays unknown.",
                label,
                (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>A direct string property, or <c>null</c> when absent, non-string, or blank.</summary>
    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
