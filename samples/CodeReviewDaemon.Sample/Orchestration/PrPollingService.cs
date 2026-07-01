using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

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

    public PrPollingService(
        IEnumerable<PrPollTarget> targets,
        IEnumerable<IPrProvider> providers,
        ReviewStore store,
        PrOrchestrator orchestrator,
        ILogger<PrPollingService> logger,
        TimeSpan? pollInterval = null)
    {
        _targets = [.. targets];
        _providers = [.. providers];
        _store = store;
        _orchestrator = orchestrator;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
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
        var page = await provider.ListOpenPullRequestsAsync(
            new PrPollRequest
            {
                Repo = target.Repo,
                Scope = target.Scope,
                Cursor = cursorResult.ShouldResync ? null : cursorResult.Cursor,
            },
            cancellationToken);

        var repoId = _store.EnsureRepo(target.Repo);
        foreach (var pr in page.PullRequests)
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
                Stage = ReviewStage.Discovered,
                WorkflowStatus = WorkflowStatus.Pending,
                PrLifecycleState = pr.LifecycleState,
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
}
