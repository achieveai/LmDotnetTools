namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// Pipeline position of a <c>review_run</c> — the resume axis. The orchestrator (P2.2) resumes a run
/// from the first stage that is not yet complete, so this records how far the deterministic stage
/// machine has progressed. Persisted as TEXT.
/// </summary>
internal enum ReviewStage
{
    Discovered,
    ContextReady,
    Reviewed,
    Judged,
    Posted,
}

/// <summary>
/// Health/disposition of the run as a unit of work. Distinct from <see cref="ReviewStage"/> (where it
/// is) and <see cref="PrLifecycleState"/> (what the PR itself is doing). Persisted as TEXT.
/// </summary>
internal enum WorkflowStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    RetryPending,
}

/// <summary>
/// The pull request's own lifecycle as last observed from the provider. Lets the daemon stop working
/// a run whose PR has merged/closed without conflating that with workflow failure. Persisted as TEXT.
/// </summary>
internal enum PrLifecycleState
{
    Open,
    Merged,
    Closed,
    Abandoned,
}
