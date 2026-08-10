using System.Globalization;
using System.Text.Json;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The PR-watching loop. The daemon does not receive PR webhooks — it polls each configured target on
/// an interval, advancing the opaque poll cursor (§12) and handing every discovered PR to the
/// <see cref="PrOrchestrator"/>. Each poll cycle is isolated: a failure on one cycle is logged and the
/// loop continues, so one transient provider error does not stop the daemon. <see cref="PollOnceAsync"/>
/// is the testable unit; <see cref="ExecuteAsync"/> just repeats it.
/// <para>
/// This loop carries NO maintenance work. It used to host the PR-lifecycle and deep-link retention sweeps
/// through a <c>sweepAsync</c> seam, and that was wrong in both orderings: behind the poll body the sweeps
/// waited on a cycle that reviews every PR inline and never finishes, and ahead of it a 125-PR sweep backlog
/// held off reviewing for hours. Periodic maintenance now runs on <see cref="MaintenanceSweepService"/>,
/// one instance per sweep, so neither side can starve the other.
/// </para>
/// </summary>
internal sealed class PrPollingService : BackgroundService
{
    /// <summary>Cursor payload schema version this build understands (plan §12).</summary>
    public const int CursorVersion = 1;

    /// <summary>
    /// Reserved <c>poll_cursor</c> key holding the rotation position. Not a target: the leading underscores
    /// keep it out of any real provider/scope namespace, and <see cref="PollTargetAsync"/> never sees it
    /// because rotation is read and written by index, not by iterating cursor rows.
    /// </summary>
    private const string RotationProvider = "__daemon";
    private const string RotationScope = "__poll-rotation";

    /// <summary>
    /// PRs one target may review before the cycle moves on. Deliberately small: a cycle's worst case is this
    /// times the target count times a review's duration, and that product has to stay well inside a process
    /// lifetime or the later targets are never reached. At the live ~10 min per review and five targets, 3
    /// puts a full rotation near 2.5 h; raising it favours draining a busy repo over reaching a quiet one.
    /// </summary>
    public const int DefaultMaxReviewsPerTargetPerCycle = 3;

    private readonly IReadOnlyList<PrPollTarget> _targets;
    private readonly IReadOnlyList<IPrProvider> _providers;
    private readonly ReviewStore _store;
    private readonly PrOrchestrator _orchestrator;
    private readonly ILogger<PrPollingService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxReviewsPerTargetPerCycle;
    private readonly ReviewProgressReporter? _progress;

    public PrPollingService(
        IEnumerable<PrPollTarget> targets,
        IEnumerable<IPrProvider> providers,
        ReviewStore store,
        PrOrchestrator orchestrator,
        ILogger<PrPollingService> logger,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null,
        int? maxReviewsPerTargetPerCycle = null,
        ReviewProgressReporter? progress = null)
    {
        _targets = [.. targets];
        _providers = [.. providers];
        _store = store;
        _orchestrator = orchestrator;
        _logger = logger;
        _progress = progress;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxReviewsPerTargetPerCycle = maxReviewsPerTargetPerCycle is > 0
            ? maxReviewsPerTargetPerCycle.Value
            : DefaultMaxReviewsPerTargetPerCycle;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReportFirstReviewSentinelRate();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
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

    /// <summary>
    /// Measures, once per start, how many recent first-ever reviews claimed there was nothing new since a
    /// review that never happened. Startup is the moment worth measuring: a deploy is when this defect
    /// entered the fleet, and — since its repair is not in the record — a deploy is when it could return.
    /// </summary>
    /// <remarks>
    /// Never allowed to stop the daemon. A detector that can take the poll loop down with it is worse than the
    /// blind spot it closes, so the whole thing is best-effort and a failure is reported and dropped.
    /// </remarks>
    private void ReportFirstReviewSentinelRate()
    {
        if (_progress is null)
        {
            return;
        }

        try
        {
            var since = _timeProvider.GetUtcNow().AddDays(-FirstReviewLookbackDays)
                .ToString("O", CultureInfo.InvariantCulture);
            var payloads = _store.GetFirstReviewPayloadsSince(
                since, DaemonReviewStageExecutor.ReviewArtifactKind);
            var sentinels = payloads.Count(static p =>
                DaemonReviewStageExecutor.IsNoNewFindingsSentinel(ReadReviewText(p)));
            _progress.FirstReviewSentinelRate(payloads.Count, sentinels, FirstReviewLookbackDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not measure the no-change-on-a-first-review rate; polling continues regardless.");
        }
    }

    private static string? ReadReviewText(string payload)
    {
        try
        {
            return JsonSerializer
                .Deserialize<ReviewArtifactPayload>(payload, DaemonReviewStageExecutor.PayloadOptions)?
                .ReviewText;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Window for the startup rate. Long enough to span a quiet night, short enough that a
    /// regression is not diluted by weeks of healthy history.</summary>
    private const int FirstReviewLookbackDays = 7;

    /// <summary>
    /// Runs one poll pass over every target: read the cursor (resyncing if missing/old/future/invalid),
    /// ask the provider for open PRs, orchestrate each, then persist the advanced cursor.
    /// <para>
    /// Targets are visited starting from the PERSISTED rotation position, and each target reviews at most
    /// <see cref="_maxReviewsPerTargetPerCycle"/> PRs before the loop moves on. Both halves are needed and
    /// they fix different failures. Without the bound, <see cref="PollTargetAsync"/> awaits a whole review
    /// per discovered PR, so target[1] waits for target[0]'s entire backlog — measured live at ~43 in-window
    /// PRs × ~10 min, about seven hours. Without the persisted position, every restart begins at target[0]
    /// again, and this daemon restarted eight times in one day, so the later targets were never reached: four
    /// of five enabled repos had never been polled once.
    /// </para>
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (_targets.Count == 0)
        {
            return;
        }

        var start = ReadRotationStart();
        for (var offset = 0; offset < _targets.Count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var index = (start + offset) % _targets.Count;
            var target = _targets[index];

            // Advance the rotation BEFORE the work, not after. Persisting on completion would never fire for
            // precisely the target that needs rotating away from — one whose backlog outlives the process —
            // so a restart would return to it forever. Written first, an interrupted target resumes at the
            // NEXT one and cannot monopolize across restarts.
            SaveRotationStart((index + 1) % _targets.Count);

            // Per-target isolation: a provider fetch (or any failure) on one target must not starve the
            // rest of the cycle. Log and continue with the next target; the cursor is only advanced on a
            // clean pass, so a failed fetch is retried next interval.
            try
            {
                await PollTargetAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll of target {Scope} failed; continuing with the next target.", target.Scope);
            }
        }
    }

    /// <summary>
    /// The index of the target this cycle starts from, persisted so rotation survives a restart. Stored as an
    /// ordinary <c>poll_cursor</c> row under a RESERVED provider/scope that no real target can collide with —
    /// deliberately reusing the existing cursor table rather than adding a table and a migration for one
    /// integer. Anything unreadable, out of range, or absent means "start at the top", which is also the
    /// first-run answer.
    /// </summary>
    private int ReadRotationStart()
    {
        var stored = _store.ReadCursor(RotationProvider, RotationScope, CursorVersion);
        if (stored.ShouldResync || stored.Cursor is null)
        {
            return 0;
        }

        return int.TryParse(stored.Cursor.CursorPayload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var next)
            && next >= 0
            && next < _targets.Count
            ? next
            : 0;
    }

    private void SaveRotationStart(int next)
    {
        _store.SaveCursor(new OpaqueCursor
        {
            Provider = RotationProvider,
            Scope = RotationScope,
            CursorVersion = CursorVersion,
            CursorPayload = next.ToString(CultureInfo.InvariantCulture),
        });
    }

    private async Task PollTargetAsync(PrPollTarget target, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Provider, target.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            _logger.LogWarning("No IPrProvider registered for '{Provider}'; skipping target.", target.Provider);
            return;
        }

        var cursorResult = _store.ReadCursor(target.Provider, target.Scope, CursorVersion);

        // The recency-window cutoff, computed once so the provider (which may fetch a per-PR activity
        // signal for borderline PRs) and the filter below agree on the same instant.
        var cutoff = target.MaxPrAgeDays > 0
            ? _timeProvider.GetUtcNow() - TimeSpan.FromDays(target.MaxPrAgeDays)
            : (DateTimeOffset?)null;

        var page = await provider.ListOpenPullRequestsAsync(
            new PrPollRequest
            {
                Repo = target.Repo,
                Scope = target.Scope,
                Cursor = cursorResult.ShouldResync ? null : cursorResult.Cursor,
                RecencyCutoff = cutoff,
            },
            cancellationToken);

        var repoId = _store.EnsureRepo(target.Repo);
        var reviewed = 0;
        var truncated = false;
        foreach (var pr in ApplyRecencyFilter(target, cutoff, page.PullRequests))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reviewed >= _maxReviewsPerTargetPerCycle)
            {
                truncated = true;
                _logger.LogInformation(
                    "Target {Scope}: reviewed {Reviewed} PR(s) this cycle, the per-target cap; yielding to the "
                        + "next target with more of this page still pending. The cursor is NOT advanced, so the "
                        + "remainder is re-listed and resumed next cycle.",
                    target.Scope,
                    reviewed);
                break;
            }

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
                // Captured now, while the PR is still open and the poll payload is in hand: the at-close
                // feedback extraction runs much later, against a PR that may already be closed.
                PrAuthor = pr.Author,
                // Same reason, and the same one-shot window: what the PR claims to do is read off the poll
                // payload now so the review — which runs later, possibly after the PR has moved on — can
                // judge the diff against the intent that was actually in force when it was picked up.
                PrTitle = pr.Title,
                PrDescription = pr.Description,
                PrTargetBranch = pr.TargetBranch,
                // The confidentiality trust signal, collapsed HERE and nowhere else. A provider reports
                // null when its payload could not establish the answer; the run's fields are plain bools,
                // so this is the one seam where "could not tell" has to become a decision — and it becomes
                // the fail-closed one. Getting either default backwards would co-locate a private sibling
                // repo beside a diff whose trust was never established, which is the whole risk the gate in
                // DaemonReviewStageExecutor.AllowsCrossRepoCoLocation exists to hold shut.
                IsForkPr = pr.IsForkPr ?? true,
                IsTargetRepoPublic = pr.IsTargetRepoPublic ?? true,
            };

            // Only the degraded case is logged, and only at Debug: when a provider could establish the trust
            // signal there is nothing to say, but when it could not, the run silently loses access to every
            // cross-repo sibling and the only symptom downstream is a wall of allow-list denials. This line is
            // what makes that attributable to the payload rather than to the allow-list.
            if (pr.IsForkPr is null || pr.IsTargetRepoPublic is null)
            {
                _logger.LogDebug(
                    "PR {PrId} on {Scope}: provider could not establish the confidentiality trust signal "
                        + "(fork={ProviderIsForkPr}, public={ProviderIsTargetRepoPublic}); defaulting fail-closed, "
                        + "so cross-repo siblings will not be co-located for this run.",
                    pr.PrId, target.Scope, pr.IsForkPr, pr.IsTargetRepoPublic);
            }

            // The cap counts WORK, not PRs seen, and only the run's state BEFORE this cycle can tell the two
            // apart: RunAsync returns the finished run either way, so its outcome says "complete" both for a
            // PR that was already done and for one this cycle just reviewed. Reading the pre-state here is
            // what separates them. It matters because the two halves of the fairness fix would otherwise
            // deadlock: a capped pass deliberately leaves the cursor put, so the next cycle re-lists the same
            // page, and if the finished PRs at its head ate the cap every time, the PRs past the cap would
            // never be reached on any cycle — #88's own starvation one level down, with the page as the queue.
            // This is a create-or-get on the row the orchestrator is about to resolve anyway, not extra work.
            var alreadyComplete = StageMachine.IsComplete(_store.CreateOrGetReviewRun(seed).Stage);

            // Per-PR isolation: one poison PR must not abort the rest of the target's PRs. The
            // orchestrator has already marked the failed run RetryPending before rethrowing, so it will
            // resume from its first incomplete stage on a later poll; here we just log and move on.
            try
            {
                _ = await _orchestrator.RunAsync(seed, cancellationToken);

                if (!alreadyComplete)
                {
                    reviewed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failed attempt DID consume the slot: the work was done, it just did not land.
                reviewed++;
                _logger.LogError(
                    ex,
                    "Orchestrating PR {PrId} on {Scope} failed; the run is left RetryPending and polling continues.",
                    pr.PrId,
                    target.Scope);
            }
        }

        // A truncated pass must NOT advance the cursor: NextCursor points past the WHOLE page, so saving it
        // here would step over the PRs this cycle deliberately did not review and they would never be seen
        // again. Leaving it put means the next cycle re-lists the same page; the PRs already reviewed resolve
        // to existing runs with no stages left, so re-listing costs a lookup rather than a second review.
        if (!truncated)
        {
            _store.SaveCursor(page.NextCursor);
        }
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
        IReadOnlyList<PullRequestDescriptor> pullRequests)
    {
        if (cutoff is null || pullRequests.Count == 0)
        {
            return pullRequests;
        }

        var kept = new List<PullRequestDescriptor>(pullRequests.Count);
        foreach (var pr in pullRequests)
        {
            var activity = pr.UpdatedAt ?? pr.CreatedAt;
            if (activity is null || activity.Value >= cutoff.Value)
            {
                kept.Add(pr);
            }
        }

        if (kept.Count < pullRequests.Count)
        {
            _logger.LogInformation(
                "Recency filter ({Days}d) on {Scope}: reviewing {Kept} of {Total} open PR(s); {Skipped} outside the window.",
                target.MaxPrAgeDays,
                target.Scope,
                kept.Count,
                pullRequests.Count,
                pullRequests.Count - kept.Count);
        }

        return kept;
    }
}
