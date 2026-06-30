using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.2 — the pure stage machine: deterministic linear order, terminal detection, and the
/// resume-from-first-incomplete contract (<see cref="StageMachine.RemainingStages"/>) that lets a
/// crashed run replay exactly the outstanding stages and no completed stage twice.
/// </summary>
public sealed class StageMachineTests
{
    [Fact]
    public void Order_is_the_fixed_linear_pipeline()
    {
        StageMachine.Order.Should().Equal(
            ReviewStage.Discovered,
            ReviewStage.ContextReady,
            ReviewStage.Reviewed,
            ReviewStage.Judged,
            ReviewStage.Posted);
    }

    [Fact]
    public void Terminal_is_the_last_stage()
    {
        StageMachine.Terminal.Should().Be(ReviewStage.Posted);
        StageMachine.IsComplete(ReviewStage.Posted).Should().BeTrue();
        StageMachine.IsComplete(ReviewStage.Discovered).Should().BeFalse();
    }

    [Fact]
    public void Next_stage_advances_one_step()
    {
        StageMachine.NextStage(ReviewStage.Discovered).Should().Be(ReviewStage.ContextReady);
        StageMachine.NextStage(ReviewStage.ContextReady).Should().Be(ReviewStage.Reviewed);
        StageMachine.NextStage(ReviewStage.Reviewed).Should().Be(ReviewStage.Judged);
        StageMachine.NextStage(ReviewStage.Judged).Should().Be(ReviewStage.Posted);
    }

    [Fact]
    public void Next_stage_after_terminal_is_null()
    {
        StageMachine.NextStage(ReviewStage.Posted).Should().BeNull();
    }

    [Fact]
    public void Remaining_stages_are_everything_strictly_after_the_last_completed()
    {
        StageMachine.RemainingStages(ReviewStage.Discovered).Should().Equal(
            ReviewStage.ContextReady,
            ReviewStage.Reviewed,
            ReviewStage.Judged,
            ReviewStage.Posted);

        StageMachine.RemainingStages(ReviewStage.Reviewed).Should().Equal(
            ReviewStage.Judged,
            ReviewStage.Posted);
    }

    [Fact]
    public void Remaining_stages_at_terminal_is_empty()
    {
        StageMachine.RemainingStages(ReviewStage.Posted).Should().BeEmpty();
    }
}
