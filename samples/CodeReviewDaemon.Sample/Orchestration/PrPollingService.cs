using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// A maintenance sweep that threw, carrying <b>which</b> one.
/// <para>
/// The poller has one maintenance seam and four sweeps are chained into it — PR lifecycle, deep-link
/// retention, stranded-run reconciliation and the eval corpus sweep. Its failure log named the first
/// of them, which was true when there was one and is now wrong three times out of four; an operator
/// reading "PR-lifecycle sweep failed" while the eval sweep was the one throwing looks in the wrong
/// place first (#455 item 5).
/// </para>
/// <para>
/// The name is carried on the exception rather than pushed into the seam's signature because the
/// seam is a single <c>Func</c> by design — the poller does not know how many sweeps are behind it,
/// and giving it a list would move the composition decision out of the composition root. Cancellation
/// is deliberately NOT wrapped: the poller tells a cancelled shutdown from a failure by type, and a
/// wrapped <see cref="OperationCanceledException"/> would be logged as an error on every clean stop.
/// </para>
/// </summary>
/// <param name="sweepName">Short, stable name of the sweep that failed.</param>
/// <param name="inner">What it threw.</param>
internal sealed class MaintenanceSweepException(string sweepName, Exception inner)
    : Exception($"The {sweepName} maintenance sweep failed.", inner)
{
    /// <summary>Which sweep threw.</summary>
    public string SweepName { get; } = sweepName;

    /// <summary>
    /// The sweep name to log for <paramref name="exception"/>, or <c>"unnamed"</c> when it did not
    /// come through a named composition — which in production it always does, and in a test with a
    /// hand-rolled seam it does not.
    /// </summary>
    public static string NameOf(Exception exception) =>
        (exception as MaintenanceSweepException)?.SweepName ?? "unnamed";
}

/// <summary>
/// The PR-watching loop. The daemon does not receive PR webhooks — it polls each configured target on
/// an interval, advancing the opaque poll cursor (§12) and handing every discovered PR to the
/// <see cref="PrOrchestrator"/>. Each poll cycle is isolated: a failure on one cycle is logged and the
/// loop continues, so one transient provider error does not stop the daemon. <see cref="PollOnceAsync(CancellationToken)"/>
/// is the testable unit; <see cref="ExecuteAsync"/> just repeats it.
/// </summary>
internal sealed class PrPollingService : BackgroundService
{
    /// <summary>Cursor payload schema version this build understands (plan §12).</summary>
    public const int CursorVersion = 1;

    private readonly IReadOnlyList<PrPollTarget> _targets;
    private readonly IReadOnlyList<IPrProvider> _providers;
    private readonly ReviewStore _store;
    private readonly PrOrchestrator _orchestrator;
    private readonly ILogger<PrPollingService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Func<CancellationToken, Task>? _sweepAsync;
    private readonly TimeProvider _timeProvider;
    private readonly ReviewProgressReporter? _progress;
    private readonly int _firstReviewLookbackDays;
    private readonly IReadOnlyList<IReviewCommentReader> _commentReaders;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ReviewBranchInventoryEntry>>>? _readCompletionBacklog;
    private readonly IReadOnlyDictionary<string, int>? _slotsByRepository;
    private readonly HostGitPushAuthorization? _pushAuthorization;
    private readonly Func<IReadOnlyList<ReviewBranchInventoryEntry>, CancellationToken, Task>? _processTerminal;

    public PrPollingService(
        IEnumerable<PrPollTarget> targets,
        IEnumerable<IPrProvider> providers,
        ReviewStore store,
        PrOrchestrator orchestrator,
        ILogger<PrPollingService> logger,
        TimeSpan? pollInterval = null,
        Func<CancellationToken, Task>? sweepAsync = null,
        TimeProvider? timeProvider = null,
        ReviewProgressReporter? progress = null,
        int? firstReviewLookbackDays = null,
        IEnumerable<IReviewCommentReader>? commentReaders = null,
        Func<CancellationToken, Task<IReadOnlyList<ReviewBranchInventoryEntry>>>? readCompletionBacklog = null,
        IReadOnlyList<ReviewSlot>? slots = null,
        HostGitPushAuthorization? pushAuthorization = null,
        Func<IReadOnlyList<ReviewBranchInventoryEntry>, CancellationToken, Task>? processTerminal = null
    )
    {
        _targets = [.. targets];
        _providers = [.. providers];
        _store = store;
        _orchestrator = orchestrator;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _sweepAsync = sweepAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _progress = progress;
        _firstReviewLookbackDays = firstReviewLookbackDays is > 0
            ? firstReviewLookbackDays.Value
            : CodeReviewDaemonOptions.DefaultFirstReviewSentinelLookbackDays;
        _commentReaders = commentReaders is null ? [.. _providers.OfType<IReviewCommentReader>()] : [.. commentReaders];
        _readCompletionBacklog = readCompletionBacklog;
        _pushAuthorization = pushAuthorization;
        _processTerminal = processTerminal;
        _slotsByRepository = slots
            ?.GroupBy(slot => slot.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _orchestrator.RecoverActiveWorkspacesAsync(stoppingToken).ConfigureAwait(false);
        ReportFirstReviewSentinelRate();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Inspect every branch before intake. Historical branches stay outside new-PR admission.
                await _orchestrator.RecoverPendingOwnersAsync(stoppingToken).ConfigureAwait(false);
                var branches = await ReadCompletionBacklogAsync(stoppingToken).ConfigureAwait(false);
                await PollOnceAsync(stoppingToken, branches).ConfigureAwait(false);
                if (
                    !HasUnresolvedBranchIdentity(branches)
                    && !branches.Any(entry => entry.BlocksNewIntake)
                    && !await RunMaintenanceSweepAsync(stoppingToken).ConfigureAwait(false)
                )
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll cycle failed; continuing after the interval.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Only an observed all-open (or empty) branch inventory permits maintenance.</summary>
    internal async Task<bool> CompletionBacklogIsClearAsync(CancellationToken cancellationToken)
    {
        var entries = await ReadCompletionBacklogAsync(cancellationToken).ConfigureAwait(false);
        return !HasUnresolvedBranchIdentity(entries) && !entries.Any(entry => entry.BlocksNewIntake);
    }

    private async Task<IReadOnlyList<ReviewBranchInventoryEntry>> ReadCompletionBacklogAsync(
        CancellationToken cancellationToken
    ) => _readCompletionBacklog is null ? [] : await _readCompletionBacklog(cancellationToken).ConfigureAwait(false);

    private bool HasUnresolvedBranchIdentity(IReadOnlyList<ReviewBranchInventoryEntry> entries)
    {
        var duplicate = entries
            .Where(entry => entry.RepositoryKey is not null && int.TryParse(entry.PrId, out _))
            .GroupBy(entry =>
                (entry.RepositoryKey!, int.Parse(entry.PrId!, System.Globalization.CultureInfo.InvariantCulture))
            )
            .FirstOrDefault(group => group.Count() != 1);
        var unresolved = entries.FirstOrDefault(entry =>
            entry.Status != "discovered"
            || entry.RepositoryKey is null
            || entry.Lifecycle is not ("Open" or "Merged" or "Abandoned")
            || !int.TryParse(
                entry.PrId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number
            )
            || number <= 0
        );
        if (duplicate is null && unresolved is null)
            return false;
        _logger.LogWarning(
            "Review branch inventory has {Reason}; holding all intake and maintenance until identity is reconciled.",
            duplicate is not null ? "duplicate PR identity" : unresolved!.Status
        );
        return true;
    }

    /// <summary>
    /// Measures, once per start, how many recent first-ever reviews claimed there was nothing new since a
    /// review that never happened. Startup is the moment worth measuring: a deploy is when this defect
    /// entered the fleet, and — since its repair is not in the record — a deploy is when it could return.
    /// </summary>
    /// <remarks>
    /// Never allowed to stop the daemon. A detector that can take the poll loop down with it is worse than the
    /// blind spot it closes, so the whole thing is best-effort and a failure is reported and dropped. It is
    /// inert when no reporter was supplied, because the line's whole value is that it reaches the ONE logger
    /// category the console keeps at Information — logged anywhere else it is a line nobody reads.
    /// </remarks>
    private void ReportFirstReviewSentinelRate()
    {
        if (_progress is null)
        {
            return;
        }

        try
        {
            var since = _timeProvider.GetUtcNow().AddDays(-_firstReviewLookbackDays);
            var payloads = _store.GetFirstReviewPayloadsSince(since, ReviewArtifactKinds.ReviewArtifactKind);
            var sentinels = payloads.Count(static p => ReviewArtifactKinds.IsNoNewFindingsSentinel(ReadReviewText(p)));
            _progress.FirstReviewSentinelRate(payloads.Count, sentinels, _firstReviewLookbackDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not measure the no-change-on-a-first-review rate; polling continues regardless."
            );
        }
    }

    private static string? ReadReviewText(string payload)
    {
        try
        {
            return JsonSerializer
                .Deserialize<ReviewArtifactPayload>(payload, ReviewArtifactKinds.PayloadOptions)
                ?.ReviewText;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the maintenance seam once (design §4.5): the PR-lifecycle sweep, the deep-link retention
    /// sweep, the stranded-run reconciler and the eval corpus sweep, chained at the composition root.
    /// Isolated from the poll, so a sweep failure never stops the poller — each sweeper is itself
    /// degrade-not-throw per PR, and the composition already skips the rest of the cycle on the first
    /// throw.
    /// <para>
    /// Internal and separated from <see cref="ExecuteAsync"/> for the reason
    /// <see cref="PollOnceAsync(CancellationToken)"/> is: the loop above has a delay in it, and the one thing here worth
    /// asserting — which sweep the failure line names — would otherwise be reachable only by starting
    /// the service and racing its interval.
    /// </para>
    /// </summary>
    /// <returns>
    /// False when the sweep was cancelled by shutdown, which is the caller's signal to stop looping —
    /// never a report about whether the sweep succeeded, because a failed sweep is a logged event the
    /// poller deliberately continues past.
    /// </returns>
    internal async Task<bool> RunMaintenanceSweepAsync(CancellationToken cancellationToken)
    {
        if (_sweepAsync is null)
        {
            return true;
        }

        try
        {
            await _sweepAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Named, because four sweeps share this one seam. The name rides on the exception rather
            // than on the seam's signature — the poller does not know how many sweeps are behind it,
            // and it should not have to.
            _logger.LogError(
                ex,
                "The {SweepName} maintenance sweep failed; continuing after the interval.",
                MaintenanceSweepException.NameOf(ex)
            );
        }

        return true;
    }

    /// <summary>
    /// Runs one poll pass over every target: read the cursor (resyncing if missing/old/future/invalid),
    /// ask the provider for open PRs, orchestrate each, then persist the advanced cursor.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var branches = await ReadCompletionBacklogAsync(cancellationToken).ConfigureAwait(false);
        await PollOnceAsync(cancellationToken, branches).ConfigureAwait(false);
    }

    private async Task PollOnceAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<ReviewBranchInventoryEntry> branches
    )
    {
        var terminal = ProcessTerminalSafelyAsync(branches, cancellationToken);
        if (_slotsByRepository is null)
        {
            foreach (var target in _targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PollTargetSafelyAsync(target, null, null, branches, cancellationToken).ConfigureAwait(false);
            }
            await terminal.ConfigureAwait(false);
            return;
        }

        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var lanes = _slotsByRepository.ToDictionary(
            entry => entry.Key,
            entry => new SemaphoreSlim(entry.Value),
            StringComparer.OrdinalIgnoreCase
        );
        var targets = _targets.Select(target =>
            PollTargetSafelyAsync(
                target,
                seen,
                lanes.GetValueOrDefault(target.Repo.RepoName),
                branches,
                cancellationToken
            )
        );
        await Task.WhenAll(targets.Append(terminal)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task ProcessTerminalSafelyAsync(
        IReadOnlyList<ReviewBranchInventoryEntry> branches,
        CancellationToken ct
    )
    {
        if (_processTerminal is null)
            return;
        try
        {
            await _processTerminal(branches, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal processing failed; unrelated PR intake continues.");
        }
    }

    private async Task PollTargetSafelyAsync(
        PrPollTarget target,
        ConcurrentDictionary<string, byte>? seen,
        SemaphoreSlim? lane,
        IReadOnlyList<ReviewBranchInventoryEntry> branches,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await PollTargetAsync(target, seen, lane, branches, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll of target {Scope} failed; leaving its cursor unchanged.", target.Scope);
        }
    }

    private async Task PollTargetAsync(
        PrPollTarget target,
        ConcurrentDictionary<string, byte>? seen,
        SemaphoreSlim? lane,
        IReadOnlyList<ReviewBranchInventoryEntry> branches,
        CancellationToken cancellationToken
    )
    {
        var provider = PrPollTargetBuilder.ResolveProvider(_providers, target);
        if (provider is null)
        {
            _logger.LogWarning("No IPrProvider registered for '{Provider}'; skipping target.", target.Provider);
            return;
        }

        var cursorResult = _store.ReadCursor(target.Provider, target.Scope, CursorVersion);

        // The recency-window cutoff, computed once so the provider (which may fetch a per-PR activity
        // signal for borderline PRs) and the filter below agree on the same instant.
        var cutoff = PrRecencyFilter.ComputeCutoff(target.MaxPrAgeDays, _timeProvider);

        var page = await provider.ListOpenPullRequestsAsync(
            new PrPollRequest
            {
                Repo = target.Repo,
                Scope = target.Scope,
                Cursor = cursorResult.ShouldResync ? null : cursorResult.Cursor,
                RecencyCutoff = cutoff,
            },
            cancellationToken
        );

        var repoId = _store.EnsureRepo(target.Repo);
        var pending = new List<Task<bool>>();
        var handled = true;
        try
        {
            foreach (var pr in ApplyRecencyFilter(target, cutoff, page.PullRequests))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    !int.TryParse(
                        pr.PrId,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var prNumber
                    )
                    || prNumber <= 0
                    || branches.Any(entry =>
                        entry.RepositoryKey == target.Repo.NormalizedKey
                        && int.TryParse(
                            entry.PrId,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var branchPr
                        )
                        && branchPr == prNumber
                        && entry.BlocksNewIntake
                    )
                )
                {
                    handled = false;
                    _logger.LogDebug(
                        "PR {PrId} on {Scope} has a prior or uncertain review branch; retaining its cursor.",
                        pr.PrId,
                        target.Scope
                    );
                    continue;
                }
                if (lane is not null)
                    await lane.WaitAsync(cancellationToken).ConfigureAwait(false);
                var key = target.Repo.NormalizedKey + ":" + pr.PrId;
                if (seen is not null && !seen.TryAdd(key, 0))
                {
                    handled = false;
                    lane!.Release();
                    _logger.LogDebug(
                        "PR {PrId} on {Scope} is already admitted in this cycle; retaining its cursor.",
                        pr.PrId,
                        target.Scope
                    );
                    continue;
                }

                try
                {
                    var seed = new ReviewRun
                    {
                        RepoId = repoId,
                        PrId = pr.PrId,
                        HeadSha = pr.HeadSha,
                        BaseSha = pr.BaseSha,
                        TriggerWatermark = pr.TriggerWatermark,
                        ReviewKind = target.ReviewKind,
                        VariantId = target.VariantId,
                        Mode = target.Mode,
                        ModelId = target.ModelId,
                        Stage = ReviewStage.Discovered,
                        WorkflowStatus = WorkflowStatus.Pending,
                        PrLifecycleState = pr.LifecycleState,
                        PrAuthor = pr.Author,
                        PrTitle = pr.Title,
                        PrDescription = pr.Description,
                    };
                    pending.Add(RunOneAsync(pr, seed));
                }
                catch
                {
                    lane?.Release();
                    throw;
                }
            }
        }
        finally
        {
            // Host shutdown and the next poll must never race an admitted workflow.
            var results = await Task.WhenAll(pending).ConfigureAwait(false);
            handled &= results.All(result => result);
        }
        if (handled && !cancellationToken.IsCancellationRequested)
        {
            _store.SaveCursor(page.NextCursor);
            _logger.LogDebug("Poll target {Scope} completed; cursor advanced.", target.Scope);
        }
        else
        {
            _logger.LogDebug("Poll target {Scope} has unfinished PR work; cursor retained.", target.Scope);
        }

        async Task<bool> RunOneAsync(PullRequestDescriptor pr, ReviewRun seed)
        {
            try
            {
                var descriptor = pr;
                {
                    var current = await provider
                        .GetPullRequestAsync(target.Repo, pr.PrId, cancellationToken)
                        .ConfigureAwait(false);
                    if (
                        current?.LifecycleState != PrLifecycleState.Open
                        || current.PrId != pr.PrId
                        || current.HeadSha != pr.HeadSha
                        || current.BaseSha != pr.BaseSha
                    )
                    {
                        _logger.LogWarning(
                            "PR {PrId} on {Scope} changed or closed before admission; retaining the cursor.",
                            pr.PrId,
                            target.Scope
                        );
                        return false;
                    }
                    descriptor = current;
                }
                using var artifactScope = ReviewArtifactBranchCapability.EnterScope();
                using var pushScope = _pushAuthorization?.EnterScope();
                if (_pushAuthorization is not null)
                {
                    var capability = ReviewArtifactBranchCapability.Grant(
                        target.Repo.NormalizedKey,
                        descriptor.PrId,
                        descriptor.HeadSha
                    );
                    ReviewArtifactBranchCapability.SetCurrent(capability);
                    _pushAuthorization.AuthorizePush(capability.AuthorizedBranch(target.Repo));
                }
                return await RunWorkflowAsync(
                        target,
                        descriptor,
                        seed with
                        {
                            TriggerWatermark = descriptor.TriggerWatermark,
                            PrAuthor = descriptor.Author,
                            PrTitle = descriptor.Title,
                            PrDescription = descriptor.Description,
                        },
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Orchestrating PR {PrId} on {Scope} failed; leaving the cursor unchanged for retry.",
                    pr.PrId,
                    target.Scope
                );
                return false;
            }
            finally
            {
                lane?.Release();
            }
        }
    }

    private async Task<bool> RunWorkflowAsync(
        PrPollTarget target,
        PullRequestDescriptor descriptor,
        ReviewRun seed,
        CancellationToken cancellationToken
    )
    {
        var existing = _store.FindReviewRunByIdentity(seed);
        if (_store.GetAmbiguousWorkflowRoundId(seed) is { } ambiguousRoundId)
        {
            _logger.LogWarning(
                "PR {PrId} on {Scope} has round {RoundId} with ambiguous run ownership; retaining its cursor for reconciliation.",
                seed.PrId,
                target.Scope,
                ambiguousRoundId
            );
            return false;
        }
        if (
            _store
                .ListActiveWorkflowWorkspaceRuns()
                .Any(owner => owner.RepoId == seed.RepoId && owner.PrId == seed.PrId)
        )
        {
            _logger.LogDebug(
                "PR {PrId} on {Scope} already owns an active workspace; retaining its cursor without starting a review.",
                seed.PrId,
                target.Scope
            );
            return false;
        }
        var run = _store.CreateOrGetReviewRun(seed);
        if (run.WorkflowStatus != WorkflowStatus.Completed)
        {
            var initial =
                run.Mode == "collect-only"
                    ? WorkflowMarkdown.InitialContext(descriptor)
                    : await ReadCommentContextAsync(
                            target,
                            descriptor,
                            run,
                            new Dictionary<string, string>(StringComparer.Ordinal),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
            run = await _orchestrator.RunAsync(seed, initial, cancellationToken).ConfigureAwait(false);
            if (run.WorkflowStatus != WorkflowStatus.Completed)
            {
                return false;
            }
            return true; // This provider snapshot is the new-head baseline; a later poll admits changes.
        }

        foreach (var pending in _store.GetPendingWorkflowRoundsForRun(run.Id))
        {
            if (
                await _orchestrator.RunRoundAsync(run, pending, cancellationToken).ConfigureAwait(false)
                != WorkflowInvocationStatus.Completed
            )
            {
                return false;
            }
        }

        var previousRound = _store.GetLatestWorkflowRound(run.Id, WorkflowRoundKind.Discussion);
        if (previousRound is null && !_orchestrator.HasFrozenContext(run, null))
        {
            var baseline = await ReadCommentContextAsync(
                    target,
                    descriptor,
                    run,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var baselineRound = _store.CreateOrGetWorkflowRound(
                new WorkflowRoundSeed
                {
                    ReviewRunId = run.Id,
                    RepoId = run.RepoId,
                    PrId = run.PrId,
                    HeadSha = run.HeadSha,
                    Kind = WorkflowRoundKind.Discussion,
                    EventKey = $"legacy-window:{BuildWindowId(baseline["CommentWindow"]!.AsArray())}",
                    FrozenInputJson = baseline.ToJsonString(),
                }
            );
            return await _orchestrator.RunRoundAsync(run, baselineRound, cancellationToken).ConfigureAwait(false)
                == WorkflowInvocationStatus.Completed;
        }
        var previousContext = await _orchestrator
            .ReadFrozenContextAsync(run, previousRound, cancellationToken)
            .ConfigureAwait(false);
        if (previousRound is null && previousContext["DiscussionDeferred"]?.GetValue<bool>() == true)
        {
            previousContext = JsonNode
                .Parse(
                    _store.TryGetLatestArtifact(run.Id, "workflow-deferred-discussion")?.Payload
                        ?? throw new InvalidDataException("Deferred discussion baseline is missing.")
                )!
                .AsObject();
        }
        var previousVersions = ReadVersions(previousContext);
        var current = await ReadCommentContextAsync(target, descriptor, run, previousVersions, cancellationToken)
            .ConfigureAwait(false);
        var window = current["CommentWindow"]!.AsArray();
        if (window.Count == 0)
        {
            return true;
        }
        var eventKey = BuildWindowId(window);
        var round = _store.CreateOrGetWorkflowRound(
            new WorkflowRoundSeed
            {
                ReviewRunId = run.Id,
                RepoId = run.RepoId,
                PrId = run.PrId,
                HeadSha = run.HeadSha,
                Kind = WorkflowRoundKind.Discussion,
                EventKey = eventKey,
                FrozenInputJson = current.ToJsonString(),
            }
        );
        return await _orchestrator.RunRoundAsync(run, round, cancellationToken).ConfigureAwait(false)
            == WorkflowInvocationStatus.Completed;
    }

    private Task<JsonObject> ReadCommentContextAsync(
        PrPollTarget target,
        PullRequestDescriptor descriptor,
        ReviewRun run,
        IReadOnlyDictionary<string, string> previousVersions,
        CancellationToken cancellationToken
    ) =>
        PrCommentContextReader.ReadAsync(
            _commentReaders,
            _store,
            target.Repo,
            target.Provider,
            run.RepoId,
            run.PrId,
            descriptor,
            previousVersions,
            cancellationToken
        );

    internal static JsonObject BuildCommentContext(
        PullRequestDescriptor descriptor,
        IReadOnlyList<ExistingReviewComment> comments,
        IReadOnlySet<string> ownIds,
        IReadOnlyDictionary<string, string> previousVersions
    )
    {
        var baseline = new JsonArray();
        var window = new JsonArray();
        foreach (
            var comment in comments
                .Where(value => value.ProviderCommentId is null || !ownIds.Contains(value.ProviderCommentId))
                .OrderBy(value => value.ProviderCommentId, StringComparer.Ordinal)
        )
        {
            if (
                string.IsNullOrWhiteSpace(comment.ProviderCommentId)
                || string.IsNullOrWhiteSpace(comment.ProviderVersion)
            )
            {
                throw new InvalidDataException("Provider comment data is missing its stable id or version.");
            }
            var item = CommentToJson(comment);
            baseline.Add(item);
            if (
                !previousVersions.TryGetValue(comment.ProviderCommentId, out var previous)
                || !string.Equals(previous, comment.ProviderVersion, StringComparison.Ordinal)
            )
            {
                window.Add(item.DeepClone());
            }
        }
        return new JsonObject
        {
            ["PullRequest"] = new JsonObject
            {
                ["PrId"] = descriptor.PrId,
                ["HeadSha"] = descriptor.HeadSha,
                ["BaseSha"] = descriptor.BaseSha,
                ["Title"] = descriptor.Title,
                ["Description"] = descriptor.Description,
                ["Author"] = descriptor.Author,
            },
            ["CommentBaseline"] = baseline,
            ["CommentWindow"] = window,
        };
    }

    private static IReadOnlyDictionary<string, string> ReadVersions(JsonObject context)
    {
        if (context["CommentBaseline"] is not JsonArray comments)
        {
            throw new InvalidDataException("Frozen comment baseline is missing.");
        }
        return comments.ToDictionary(
            value =>
                value?["ProviderCommentId"]?.GetValue<string>()
                ?? throw new InvalidDataException("Frozen comment id is missing."),
            value =>
                value?["ProviderVersion"]?.GetValue<string>()
                ?? throw new InvalidDataException("Frozen comment version is missing."),
            StringComparer.Ordinal
        );
    }

    private static JsonObject CommentToJson(ExistingReviewComment comment) =>
        new()
        {
            ["ProviderCommentId"] = comment.ProviderCommentId,
            ["ProviderVersion"] = comment.ProviderVersion,
            ["Path"] = comment.Path,
            ["Line"] = comment.Line,
            ["Body"] = comment.Body,
            ["Author"] = comment.Author,
            ["IsActive"] = comment.IsActive,
            ["PublishedAt"] = comment.PublishedAt?.ToString("O"),
            ["ThreadId"] = comment.ThreadId,
        };

    private static string BuildWindowId(JsonArray window)
    {
        var identity = string.Join(
            '\n',
            window.Select(value =>
                value!["ProviderCommentId"]!.GetValue<string>() + "\0" + value["ProviderVersion"]!.GetValue<string>()
            )
        );
        return "comments-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    /// <summary>
    /// Applies the operator recency bound (<see cref="PrPollTarget.MaxPrAgeDays"/>): drops PRs whose last
    /// activity (GitHub <c>updated_at</c>; ADO the source branch's last push, resolved by the provider) or,
    /// as a fallback, opened date is older than the window. A PR the provider gave no date for is kept — the
    /// filter never silently drops a PR it can't date. When the bound is off (<paramref name="cutoff"/> is
    /// null) the full list passes through unchanged. The cursor still advances off the full page, so
    /// filtering here never strands the poll's high-water mark.
    /// </summary>
    private IReadOnlyList<PullRequestDescriptor> ApplyRecencyFilter(
        PrPollTarget target,
        DateTimeOffset? cutoff,
        IReadOnlyList<PullRequestDescriptor> pullRequests
    )
    {
        var kept = PrRecencyFilter.Apply(cutoff, pullRequests);

        if (kept.Count < pullRequests.Count)
        {
            _logger.LogInformation(
                "Recency filter ({Days}d) on {Scope}: reviewing {Kept} of {Total} open PR(s); {Skipped} outside the window.",
                target.MaxPrAgeDays,
                target.Scope,
                kept.Count,
                pullRequests.Count,
                pullRequests.Count - kept.Count
            );
        }

        return kept;
    }
}
