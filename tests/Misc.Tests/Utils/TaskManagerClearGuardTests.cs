using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     Bug 19: <c>bulk-initialize(clearExisting: true)</c> used to drop every row — completed work,
///     notes and artifacts included — for any caller that asked, and a sub-agent asked. Two guards
///     now stand between that call and the rows: the board is handed to <see cref="TaskManager.OnCleared" />
///     before it is dropped, and a clear requested by a sub-agent is refused outright.
/// </summary>
public class TaskManagerClearGuardTests
{
    private static TaskManager BoardWithWork()
    {
        var board = new TaskManager { ThreadId = "conv-1" };
        _ = board.AddTask("Wire the SSE endpoint"); // 1
        _ = board.AddTask("Add the map", "1"); // 1.1
        _ = board.AddTask("Vitest coverage"); // 2
        _ = board.AddNote("1", noteText: "waiting on schema");
        _ = board.AttachArtifact("1", "src/LmCore/Models/TodoBoardSnapshot.cs");
        _ = board.ClaimTask("1.1", "agent-a");
        _ = board.UpdateTask("1.1", "completed", agent: "agent-a");
        return board;
    }

    private static List<TaskManager.BulkTaskItem> FreshPlan() => [new TaskManager.BulkTaskItem { Task = "Start over" }];

    [Fact]
    public void ClearExisting_HandsThePreClearBoardToOnCleared()
    {
        // Mutation that must go red: capturing the snapshot AFTER RootTasks.Clear(), or not raising
        // the hook at all. Either way the archive would carry nothing worth recovering.
        var board = BoardWithWork();
        TodoBoardSnapshot? archived = null;
        board.OnCleared += snapshot => archived = snapshot;

        _ = board.BulkInitialize(FreshPlan(), clearExisting: true);

        archived.Should().NotBeNull();
        archived!.ThreadId.Should().Be("conv-1");
        archived.Tasks.Should().HaveCount(2);
        archived.Tasks[0].Title.Should().Be("Wire the SSE endpoint");
        archived.Tasks[0].Notes.Should().ContainSingle().Which.Should().Be("waiting on schema");
        archived.Tasks[0].Artifacts.Should().ContainSingle();
        archived.Tasks[0].SubTasks.Should().ContainSingle().Which.Status.Should().Be(TodoTaskStatus.Completed);
        archived.Tasks[1].Title.Should().Be("Vitest coverage");
    }

    [Fact]
    public void ClearExisting_StillClears_ForTheRootAgent()
    {
        var board = BoardWithWork();

        var result = board.BulkInitialize(FreshPlan(), clearExisting: true);

        result.ErrorCode.Should().BeNull();
        board.GetTasks().Should().ContainSingle().Which.Title.Should().Be("Start over");
    }

    [Fact]
    public void WithoutClearExisting_DoesNotRaiseOnCleared()
    {
        var board = BoardWithWork();
        var raised = 0;
        board.OnCleared += _ => raised++;

        _ = board.BulkInitialize(FreshPlan(), clearExisting: false);

        raised.Should().Be(0);
        board.GetTasks().Should().HaveCount(3);
    }

    [Fact]
    public void ClearExisting_OnAnEmptyBoard_DoesNotRaiseOnCleared()
    {
        // Nothing to recover, so nothing to archive: an empty archive entry would push a real one out
        // of the bounded list for free.
        var board = new TaskManager { ThreadId = "conv-1" };
        var raised = 0;
        board.OnCleared += _ => raised++;

        _ = board.BulkInitialize(FreshPlan(), clearExisting: true);

        raised.Should().Be(0);
    }

    [Fact]
    public void ClearExisting_FromASubAgent_IsRefusedAndTheBoardIsUntouched()
    {
        // The actual bug 19 incident: agent-7 called clearExisting=true on the shared conversation
        // board. Mutation that must go red: dropping the AgentActorScope check in BulkInitializeCore.
        var board = BoardWithWork();
        var before = board.ListTasks().Text;
        var raised = 0;
        board.OnCleared += _ => raised++;

        using var _scope = AgentActorScope.Begin("agent-7", "researcher");
        var result = board.BulkInitialize(FreshPlan(), clearExisting: true);

        result.ErrorCode.Should().Be("board_clear_not_permitted");
        result.Text.Should().Contain("clearExisting=false").And.Contain("add-task");
        result.Text.Should().Contain("agent-7");
        board.ListTasks().Text.Should().Be(before);
        raised.Should().Be(0);
    }

    [Fact]
    public void WithoutClearExisting_ASubAgentMayStillExtendTheBoard()
    {
        // The refusal is about the clear, not about the tool: a sub-agent extending the plan is the
        // behaviour the refusal message sends it to.
        var board = BoardWithWork();

        using var _scope = AgentActorScope.Begin("agent-7", "researcher");
        var result = board.BulkInitialize(FreshPlan(), clearExisting: false);

        result.ErrorCode.Should().BeNull();
        board.GetTasks().Should().HaveCount(3);
    }

    [Fact]
    public void ClearExisting_LogsAWarningNamingWhatWasDropped()
    {
        // A wipe must leave a trace in the host's own logs, not only in the archive blob.
        var board = BoardWithWork();
        var logger = new RecordingLogger();
        board.Logger = logger;

        _ = board.BulkInitialize(FreshPlan(), clearExisting: true);

        var warning = logger.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("conv-1");
        warning.Should().Contain("2"); // root rows
        warning.Should().Contain("root");
    }

    [Fact]
    public void ActorScope_DoesNotLeakPastItsUsing()
    {
        // The refusal keys on "there is an ambient actor". A scope that outlived its sub-agent run
        // would start refusing the root agent's own clears.
        using (AgentActorScope.Begin("agent-7"))
        {
            AgentActorScope.Current!.AgentId.Should().Be("agent-7");
        }

        AgentActorScope.Current.Should().BeNull();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
