using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     #672: an assignee name may not silently decide ownership. Every path that writes
///     <c>Assignee</c> — <c>claim-task</c>, <c>assign-task</c>, and <c>update-task</c> moving a row to
///     in-progress on an agent's behalf — asks the host's <see cref="TaskManager.AssigneeResolver" />
///     first, refuses a name that matches more than one agent or no agent, and stores the resolved
///     canonical identity so the ordinal comparisons that decide ownership compare one stable string.
///     With no resolver wired the board behaves exactly as it did before.
/// </summary>
public class TaskManagerAssigneeResolutionTests
{
    private const string TaskTitle = "Wire the SSE endpoint";

    private static TaskManager BoardWithOneTask(out string taskId)
    {
        var board = new TaskManager();
        _ = board.AddTask(TaskTitle);
        taskId = "1";
        return board;
    }

    private static TaskManager.AssigneeResolution Ambiguous(params string[] candidates) =>
        new(null, null, TaskManager.AssigneeLiveness.Unknown, candidates);

    private static TaskManager.AssigneeResolution Unknown() => new(null, null, TaskManager.AssigneeLiveness.Unknown);

    private static TaskManager.AssigneeResolution Live(string agentId) =>
        new(agentId, agentId, TaskManager.AssigneeLiveness.Live);

    [Fact]
    public void ClaimTask_WithAmbiguousAgent_IsRefusedAndLeavesTheRowUntouched()
    {
        // Mutation that must go red: dropping the ambiguity arm from ResolveAssignee.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Ambiguous("agent-1", "agent-4");

        var result = board.ClaimTask(taskId, "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_ambiguous");
        result.Text.Should().Contain("agent-1").And.Contain("agent-4");

        var task = board.GetTasks().Single();
        task.Assignee.Should().BeNull();
        task.Status.Should().Be(TaskManager.TaskStatus.NotStarted);
    }

    [Fact]
    public void AssignTask_WithAmbiguousAgent_IsRefused()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Ambiguous("agent-1", "agent-4");

        var result = board.AssignTask(taskId, "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_ambiguous");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void UpdateTask_ToInProgress_WithAmbiguousAgent_IsRefused()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Ambiguous("agent-1", "agent-4");

        var result = board.UpdateTask(taskId, "in progress", "reviewer");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_ambiguous");
        board.GetTasks().Single().Status.Should().Be(TaskManager.TaskStatus.NotStarted);
    }

    [Fact]
    public void ClaimTask_WithUnknownAgent_IsRefused()
    {
        // Mutation that must go red: dropping the Unknown arm from ResolveAssignee.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Unknown();

        var result = board.ClaimTask(taskId, "ghost");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void AssignTask_WithUnknownAgent_IsRefused()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Unknown();

        var result = board.AssignTask(taskId, "ghost");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void ClaimTask_StoresTheResolvedCanonicalIdentity_NotTheTypedName()
    {
        // Mutation that must go red: writing the caller's text instead of CanonicalName.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Live("agent-3");

        var result = board.ClaimTask(taskId, "Reviewer Bot");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void AssignTask_StoresTheResolvedCanonicalIdentity_NotTheTypedName()
    {
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => Live("agent-3");

        var result = board.AssignTask(taskId, "Reviewer Bot");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void ASingleCandidateIsNotAmbiguous()
    {
        // Pins the qualifier, not just the clause: a resolver that reports the one agent it matched
        // has decided ownership, and refusing that would make Candidates unusable as an audit trail.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => new TaskManager.AssigneeResolution(
            "agent-3",
            "agent-3",
            TaskManager.AssigneeLiveness.Live,
            ["agent-3"]
        );

        var result = board.ClaimTask(taskId, "alpha");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void UnreachableAgent_StillOwnsTheClaim()
    {
        // Reachability is not authority: an agent that stopped responding keeps its lease so the
        // stale-lease path stays the single way a claim changes hands.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => new TaskManager.AssigneeResolution(
            "agent-3",
            "agent-3",
            TaskManager.AssigneeLiveness.Unreachable
        );

        var result = board.ClaimTask(taskId, "Reviewer Bot");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("agent-3");
    }

    [Fact]
    public void UnreachableWithNoResolvedIdentity_IsRefused()
    {
        // #676 made a restarted-away agent resolvable as not-live, and a tombstone carries no directory
        // entry — so a resolver can now legitimately report "not reachable" while being unable to name
        // the agent. Recording that would put the caller's raw text back in the ownership key, which is
        // the exact free-text ownership this whole feature removes.
        var board = BoardWithOneTask(out var taskId);
        board.AssigneeResolver = _ => new TaskManager.AssigneeResolution(
            null,
            null,
            TaskManager.AssigneeLiveness.Unreachable
        );

        var result = board.ClaimTask(taskId, "ghost");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("assignee_unknown");
        board.GetTasks().Single().Assignee.Should().BeNull();
    }

    [Fact]
    public void WithNoResolver_TheRawNameStillDecidesOwnership()
    {
        // The board must keep working with no collaboration layer wired at all.
        var board = BoardWithOneTask(out var taskId);

        var result = board.ClaimTask(taskId, "reviewer");

        result.IsError.Should().BeFalse();
        board.GetTasks().Single().Assignee.Should().Be("reviewer");
    }

    [Fact]
    public void TheResolverIsAskedOnceWithTheTrimmedName()
    {
        var board = BoardWithOneTask(out var taskId);
        var asked = new List<string>();
        board.AssigneeResolver = name =>
        {
            asked.Add(name);
            return Live("agent-3");
        };

        _ = board.ClaimTask(taskId, "  Reviewer Bot  ");

        asked.Should().Equal("Reviewer Bot");
    }
}
