using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Emits concise, human-readable one-line progress markers as a review moves through the
/// <see cref="StageMachine"/> — "picked → setup → reviewing → done". It uses its OWN logger category so
/// the console filter (appsettings <c>Logging:Console</c>) can keep these at Information while quieting
/// the verbose per-run/agent/streaming detail (which still flows in full to the JSONL sink). Messages
/// use structured templates so they stay queryable; no <c>Console.WriteLine</c>. This is operator UX
/// only — it changes no review behavior.
/// </summary>
internal sealed class ReviewProgressReporter
{
    private readonly ILogger<ReviewProgressReporter> _logger;

    public ReviewProgressReporter(ILogger<ReviewProgressReporter> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>A PR was selected for review. <paramref name="reason"/> is e.g. "new PR",
    /// "new commit {sha}", or "resuming at {stage}".</summary>
    public void Picked(ReviewRun run, string reason) =>
        _logger.LogInformation("PR #{PrId}: picked - {Reason} (run {RunId})", run.PrId, reason, run.Id);

    /// <summary>A stage is about to execute. <see cref="ReviewStage.Discovered"/> is the seed and is
    /// never emitted (the executor only runs the post-Discovered stages).</summary>
    public void StageStarting(ReviewRun run, ReviewStage stage)
    {
        var label = stage switch
        {
            ReviewStage.ContextReady => "setup - fetching diff & preparing workspace",
            ReviewStage.Reviewed => $"reviewing ({run.ModelId ?? "review agent"})",
            ReviewStage.Judged => "judging",
            ReviewStage.Posted => "finalizing",
            _ => stage.ToString(),
        };
        _logger.LogInformation("PR #{PrId}: {Phase}", run.PrId, label);
    }

    /// <summary>The run reached a terminal state. <paramref name="outcome"/> is e.g.
    /// "complete (collect-only)", "complete (posted)", "halted (PR Merged)", or "failed at Reviewed".</summary>
    public void Finished(ReviewRun run, string outcome, TimeSpan elapsed) =>
        _logger.LogInformation(
            "PR #{PrId}: done - {Outcome} ({Seconds:F0}s)", run.PrId, outcome, elapsed.TotalSeconds);
}
