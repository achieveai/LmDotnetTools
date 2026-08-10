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

    /// <summary>
    /// Reported once per daemon start: how many recent first-ever reviews answered that nothing had changed.
    /// Healthy is ZERO — a first review has no previous review to have findings since.
    /// </summary>
    /// <remarks>
    /// The sentinel-authorization guard makes a single false no-change claim impossible. It cannot tell an
    /// operator whether the fleet is healthy, because a guard only ever speaks about the run it refused. This
    /// is the population view, and it is the only thing that would catch the defect returning.
    /// <para>
    /// It lands in the reporter rather than a service logger on purpose. Console is filtered to Warning by
    /// default and lifts only this category to Information (appsettings <c>Logging:Console</c>), so a line
    /// logged anywhere else is a line an operator does not see — and "nobody looked" would be indistinguishable
    /// from "nothing was wrong". Reported on EVERY start, healthy case included, for the same reason the
    /// existing-comment census is: a fleet with no warnings and a fleet nobody is watching produce the same log.
    /// </para>
    /// </remarks>
    public void FirstReviewSentinelRate(int firstReviews, int sentinels, int lookbackDays)
    {
        if (firstReviews == 0)
        {
            _logger.LogInformation(
                "startup - no first reviews in the last {LookbackDays}d, so the no-change-on-a-first-review "
                    + "rate has nothing to report yet.",
                lookbackDays);
            return;
        }

        if (sentinels == 0)
        {
            _logger.LogInformation(
                "startup - {FirstReviews} first review(s) in the last {LookbackDays}d, none of which claimed "
                    + "\"no new findings since the last review\". That is the healthy value.",
                firstReviews, lookbackDays);
            return;
        }

        _logger.LogWarning(
            "startup - {Sentinels} of {FirstReviews} first review(s) in the last {LookbackDays}d ({Percent:F1}%) "
                + "answered \"no new findings since the last review\" on a PR that had no last review. Healthy "
                + "is zero. This regressed once before at ~49% and delivered nothing on those PRs.",
            sentinels, firstReviews, lookbackDays, 100.0 * sentinels / firstReviews);
    }
}
