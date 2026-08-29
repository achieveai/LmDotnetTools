using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     The board's change hook (#583, PR 2): fires once per SUCCESSFUL mutating tool call, never on
///     refusals or reads, and always AFTER the mutation is visible — the frame the host publishes from
///     it must carry the changed board, not the stale one. Each test names the mutation that makes it
///     red so a green run certifies the behaviour rather than the plumbing.
/// </summary>
public class TaskManagerOnChangedTests
{
    private readonly TaskManager _taskManager = new();
    private int _changes;

    public TaskManagerOnChangedTests()
    {
        _taskManager.OnChanged = () => _changes++;
    }

    [Fact]
    public void AddTask_FiresOnce()
    {
        // Mutation that must go red: removing the NotifyIfChanged wrapper from AddTask.
        _ = _taskManager.AddTask("Wire the SSE endpoint");

        _changes.Should().Be(1);
    }

    [Fact]
    public void BulkInitialize_ManyTasks_FiresExactlyOnce()
    {
        // The coalescing story: one frame per tool CALL, so a bulk-initialize of many rows is one
        // invocation, not one per row. Mutation that must go red: invoking the hook inside the per-task
        // loop rather than around the whole call.
        var tasks = Enumerable
            .Range(1, 30)
            .Select(i => new TaskManager.BulkTaskItem { Task = $"Task {i}", SubTasks = ["a", "b"] })
            .ToList();

        _ = _taskManager.BulkInitialize(tasks);

        _changes.Should().Be(1);
    }

    [Fact]
    public void EverySuccessfulMutatingTool_FiresOncePerCall()
    {
        _ = _taskManager.AddTask("Task one");
        _ = _taskManager.AddTask("Nested", "1");
        _ = _taskManager.UpdateTask("1", "in progress");
        _ = _taskManager.AddNote("1", noteText: "schema landed");
        _ = _taskManager.EditNote("1", noteIndex: 1, noteText: "schema landed, unblocked 3");
        _ = _taskManager.DeleteNote("1", noteIndex: 1);
        _ = _taskManager.DeleteTask("1.1");

        _changes.Should().Be(7);
    }

    [Fact]
    public void ManageNotes_DelegatesThroughTheHookedNoteTools()
    {
        _ = _taskManager.AddTask("Task one");
        _changes = 0;

        _ = _taskManager.ManageNotes("1", noteText: "note", action: "add");
        _ = _taskManager.ManageNotes("1", noteText: "edited", noteIndex: 1, action: "edit");
        _ = _taskManager.ManageNotes("1", noteIndex: 1, action: "delete");

        _changes.Should().Be(3);
    }

    [Fact]
    public void FailedMutations_DoNotFire()
    {
        // The board did not change, so no frame: a refusal repainting the client with the state it
        // already has is noise at best and a stale-clock update at worst. Mutation that must go red:
        // firing unconditionally instead of only when ErrorCode is null.
        _ = _taskManager.AddTask("Task one");
        _changes = 0;

        _ = _taskManager.AddTask(""); // invalid_args
        _ = _taskManager.AddTask("Orphan", "99"); // task_not_found
        _ = _taskManager.UpdateTask("1", "definitely-not-a-status"); // invalid_status
        _ = _taskManager.UpdateTask("42", "completed"); // task_not_found
        _ = _taskManager.DeleteTask("42"); // task_not_found
        _ = _taskManager.AddNote("1", noteText: ""); // invalid_args
        _ = _taskManager.EditNote("1", noteIndex: 9, noteText: "x"); // note_index_out_of_range
        _ = _taskManager.DeleteNote("1", noteIndex: 9); // note_index_out_of_range
        _ = _taskManager.ManageNotes("1", noteText: "x", action: "explode"); // invalid_action

        _changes.Should().Be(0);
    }

    [Fact]
    public void ReadOnlyTools_DoNotFire()
    {
        _ = _taskManager.AddTask("Task one");
        _changes = 0;

        _ = _taskManager.ListTasks();
        _ = _taskManager.GetTask("1");
        _ = _taskManager.ListNotes("1");
        _ = _taskManager.SearchTasks("Task");
        _ = _taskManager.GetTasks();
        _ = _taskManager.GetTodoBoardSnapshot("conv-1");
        _ = _taskManager.GetMarkdown();
        _ = _taskManager.JsonSerializeTasks();

        _changes.Should().Be(0);
    }

    [Fact]
    public void OnChanged_ObservesTheMutationAlreadyApplied_NotTheStaleBoard()
    {
        // The host's callback snapshots the board to build the frame, so the hook must fire after the
        // mutation is committed. Mutation that must go red: invoking the hook before the core mutation
        // runs.
        var titlesSeenAtCallback = new List<string>();
        _taskManager.OnChanged = () =>
            titlesSeenAtCallback.Add(
                string.Join("|", _taskManager.GetTodoBoardSnapshot("conv-1").Tasks.Select(t => t.Title))
            );

        _ = _taskManager.AddTask("Fresh row");

        titlesSeenAtCallback.Should().ContainSingle().Which.Should().Contain("Fresh row");
    }

    [Fact]
    public void OnChanged_SeesTheNewStatus_AfterUpdateTask()
    {
        _ = _taskManager.AddTask("Task one");
        var statusesSeen = new List<AchieveAi.LmDotnetTools.LmCore.Models.TodoTaskStatus>();
        _taskManager.OnChanged = () => statusesSeen.Add(_taskManager.GetTodoBoardSnapshot("conv-1").Tasks[0].Status);

        _ = _taskManager.UpdateTask("1", "in progress");

        statusesSeen
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(AchieveAi.LmDotnetTools.LmCore.Models.TodoTaskStatus.InProgress);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotFailTheToolCall()
    {
        // The mutation succeeded; a broken UI push must not convert it into a tool error the model then
        // retries. Mutation that must go red: removing the catch around the OnChanged invocation.
        _taskManager.OnChanged = () => throw new InvalidOperationException("subscriber exploded");

        var result = _taskManager.AddTask("Survives the push failure");

        result.ErrorCode.Should().BeNull();
        result.Text.Should().StartWith("Added task 1:");
        _taskManager.GetTasks().Should().ContainSingle();
    }

    [Fact]
    public void NoSubscriber_MutationsStillSucceed()
    {
        var bare = new TaskManager();

        var result = bare.AddTask("No hook wired");

        result.ErrorCode.Should().BeNull();
        bare.GetTasks().Should().ContainSingle();
    }
}
