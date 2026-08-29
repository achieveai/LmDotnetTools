using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     Rehydration from a persisted board (#583 PR 2, review F-002): a <see cref="TaskManager" />
///     recreated via <see cref="TaskManager.FromSnapshot(TodoBoardSnapshot)" /> starts from the durable
///     board — same ids, statuses, titles, notes, and nesting — instead of empty, where its first
///     mutation would persist a one-row board over the real one (the writer's empty-board guard and the
///     projection's monotonic guard both pass that write, because the fresh capture is non-empty and
///     genuinely newer).
/// </summary>
public class TaskManagerFromSnapshotTests
{
    /// <summary>Builds a manager with nesting, notes, and every persistable status but Removed.</summary>
    private static TaskManager BuildPopulatedManager()
    {
        var manager = new TaskManager();
        _ = manager.AddTask("Wire the SSE endpoint"); // 1
        _ = manager.AddTask("Add the map", "1"); // 1.1
        _ = manager.AddTask("Pin the schema", "1.1"); // 1.1.1
        _ = manager.AddTask("Vitest coverage"); // 2
        _ = manager.AddTask("Ship it"); // 3
        _ = manager.AddNote("1", noteText: "waiting on schema");
        _ = manager.AddNote("1", noteText: "unblocked by 2");
        _ = manager.ClaimTask("1", "agent-a");
        _ = manager.ClaimTask("2", "agent-b");
        _ = manager.UpdateTask("2", "completed", "agent-b");
        _ = manager.BlockTask("3", ["1"]);
        return manager;
    }

    [Fact]
    public void FromSnapshot_RestoresIdsStatusesTitlesNotesAndNesting_Exactly()
    {
        // Mutation that must go red: hydrating through BulkInitialize (renumbers from 1, flattens
        // nesting, resets every status), or casting statuses instead of mapping member-by-member.
        var original = BuildPopulatedManager().GetTodoBoardSnapshot("conv-1");

        var rehydrated = TaskManager.FromSnapshot(original).GetTodoBoardSnapshot("conv-1");

        rehydrated.Tasks.Should().HaveCount(original.Tasks.Count);
        rehydrated
            .Tasks.Select(t => (t.Id, t.Status, t.Title))
            .Should()
            .Equal(original.Tasks.Select(t => (t.Id, t.Status, t.Title)));

        var task1 = rehydrated.Tasks.Single(t => t.Id == "1");
        task1.Status.Should().Be(TodoTaskStatus.InProgress);
        task1.Notes.Should().Equal("waiting on schema", "unblocked by 2");
        task1.SubTasks.Should().ContainSingle().Which.Id.Should().Be("1.1");
        task1.SubTasks[0].SubTasks.Should().ContainSingle().Which.Id.Should().Be("1.1.1");

        rehydrated.Tasks.Single(t => t.Id == "2").Status.Should().Be(TodoTaskStatus.Completed);
        rehydrated.Tasks.Single(t => t.Id == "3").Status.Should().Be(TodoTaskStatus.Blocked);
    }

    [Fact]
    public void FromSnapshot_AdvancesIdCounters_SoNewRowsNeverCollideWithHydratedOnes()
    {
        // Mutation that must go red: resetting NextId/NextSubTaskId to 1 during hydration — the next
        // add-task would then mint "1" again and two rows would share an id.
        var snapshot = BuildPopulatedManager().GetTodoBoardSnapshot("conv-1");

        var rehydrated = TaskManager.FromSnapshot(snapshot);
        var newRoot = rehydrated.AddTask("Fresh root row");
        var newNested = rehydrated.AddTask("Fresh nested row", "1");

        newRoot.ErrorCode.Should().BeNull();
        newNested.ErrorCode.Should().BeNull();

        var board = rehydrated.GetTodoBoardSnapshot("conv-1");
        board.Tasks.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        board.Tasks.Single(t => t.Title == "Fresh root row").Id.Should().Be("4");
        board.Tasks.Single(t => t.Id == "1").SubTasks.Single(t => t.Title == "Fresh nested row").Id.Should().Be("1.2");
    }

    [Fact]
    public void FromSnapshot_ThenOneMutation_YieldsTheFullBoardPlusTheNewRow_NotATruncatedOne()
    {
        // The F-002 defect in miniature: recreate, mutate once, and the capture the writer would
        // persist must still carry every pre-existing row. Mutation that must go red: hydrating with
        // `new TaskManager()` instead of FromSnapshot.
        var persisted = BuildPopulatedManager().GetTodoBoardSnapshot("conv-1");

        var recreated = TaskManager.FromSnapshot(persisted);
        _ = recreated.AddTask("First task after the recreate");
        var captured = recreated.GetTodoBoardSnapshot("conv-1");

        captured.Tasks.Should().HaveCount(persisted.Tasks.Count + 1);
        captured
            .Tasks.Select(t => t.Id)
            .Should()
            .Contain(persisted.Tasks.Select(t => t.Id), "no persisted row may be truncated by the recreate");
        captured.Tasks.Should().ContainSingle(t => t.Title == "First task after the recreate");
    }

    [Fact]
    public void FromSnapshot_RejectsNull()
    {
        var act = () => TaskManager.FromSnapshot(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
