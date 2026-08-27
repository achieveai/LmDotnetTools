using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

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
/// loop continues, so one transient provider error does not stop the daemon. <see cref="PollOnceAsync"/>
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

    public PrPollingService(
        IEnumerable<PrPollTarget> targets,
        IEnumerable<IPrProvider> providers,
        ReviewStore store,
        PrOrchestrator orchestrator,
        ILogger<PrPollingService> logger,
        TimeSpan? pollInterval = null,
        Func<CancellationToken, Task>? sweepAsync = null,
        TimeProvider? timeProvider = null)
    {
        _targets = [.. targets];
        _providers = [.. providers];
        _store = store;
        _orchestrator = orchestrator;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _sweepAsync = sweepAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            if (!await RunMaintenanceSweepAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
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
    /// Runs the maintenance seam once (design §4.5): the PR-lifecycle sweep, the deep-link retention
    /// sweep, the stranded-run reconciler and the eval corpus sweep, chained at the composition root.
    /// Isolated from the poll, so a sweep failure never stops the poller — each sweeper is itself
    /// degrade-not-throw per PR, and the composition already skips the rest of the cycle on the first
    /// throw.
    /// <para>
    /// Internal and separated from <see cref="ExecuteAsync"/> for the reason
    /// <see cref="PollOnceAsync"/> is: the loop above has a delay in it, and the one thing here worth
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
                MaintenanceSweepException.NameOf(ex));
        }

        return true;
    }

    /// <summary>
    /// Runs one poll pass over every target: read the cursor (resyncing if missing/old/future/invalid),
    /// ask the provider for open PRs, orchestrate each, then persist the advanced cursor.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        foreach (var target in _targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
        foreach (var pr in ApplyRecencyFilter(target, cutoff, page.PullRequests))
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            };

            // Per-PR isolation: one poison PR must not abort the rest of the target's PRs. The
            // orchestrator has already marked the failed run RetryPending before rethrowing, so it will
            // resume from its first incomplete stage on a later poll; here we just log and move on.
            try
            {
                _ = await _orchestrator.RunAsync(seed, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Orchestrating PR {PrId} on {Scope} failed; the run is left RetryPending and polling continues.",
                    pr.PrId,
                    target.Scope);
            }
        }

        _store.SaveCursor(page.NextCursor);
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
