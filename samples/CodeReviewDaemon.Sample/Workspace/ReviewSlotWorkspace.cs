using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>The repository worktree pool and remote script preparer factory.</summary>
internal sealed record ReviewSlotWorkspace(
    IReviewSlotPool Pool,
    Func<ReviewRunSession, IReviewSlotPreparer>? PreparerFactory = null,
    bool AutoDiscardCompletedSlotOnAdmission = true
)
{
    public IReviewSlotPreparer CreatePreparer(ReviewRunSession session) =>
        PreparerFactory?.Invoke(session)
        ?? new ReviewSetupScriptRunner(session.CommandRunner, session.FileSystem, AutoDiscardCompletedSlotOnAdmission);
}
