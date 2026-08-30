using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     The board-loss watch (#621 Part B). F2 in the #617 corpus was twenty silent losses: the server
///     logged nothing while agents discovered rows had gone by getting <c>task_not_found</c> back. These
///     tests pin the detector that separates that from the ordinary case — a model naming an id that was
///     never there.
/// </summary>
/// <remarks>
///     <para>
///         The discrimination line under test: the manager keeps a ledger of ids it minted and was never
///         told to delete, so a not-found for a ledger id is a lost row and a not-found for anything else
///         is silence. Every negative assertion here is paired, in the same test, with a positive one on
///         the SAME manager and the SAME tool call shape — an absence assertion passes just as happily
///         when the detector never ran at all, and the paired positive is what rules that out.
///     </para>
/// </remarks>
public class TaskManagerVanishDetectorTests
{
    private const string ThreadId = "thread-621b";
    private static readonly DateTimeOffset LastSeen = new(2026, 8, 30, 11, 22, 33, TimeSpan.Zero);
    private static readonly DateTimeOffset CapturedAt = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     A board that still holds task 1 but has LOST task 2 — the shape a truncated persisted snapshot
    ///     has after the previous process dropped a row it still owned.
    /// </summary>
    private static TodoBoardSnapshot SnapshotWithVanishedTaskTwo()
    {
        return new TodoBoardSnapshot
        {
            ThreadId = ThreadId,
            CapturedAtUtc = CapturedAt,
            Tasks =
            [
                new TodoTaskNode
                {
                    Id = "1",
                    Title = "Survivor",
                    Status = TodoTaskStatus.NotStarted,
                    SubTasks =
                    [
                        new TodoTaskNode
                        {
                            Id = "1.1",
                            Title = "Survivor child",
                            Status = TodoTaskStatus.NotStarted,
                        },
                    ],
                },
            ],
            MissingTaskIds = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
            {
                ["2"] = LastSeen,
                ["1.2"] = LastSeen,
            },
        };
    }

    private static (TaskManager Manager, CapturingLogger<TaskManager> Log) Rehydrated()
    {
        var log = new CapturingLogger<TaskManager>();
        var manager = TaskManager.FromSnapshot(SnapshotWithVanishedTaskTwo());
        manager.Logger = log;
        manager.ThreadId = ThreadId;
        return (manager, log);
    }

    private static IReadOnlyList<string> Vanishes(CapturingLogger<TaskManager> log)
    {
        return
        [
            .. log.MessagesAtLevel(LogLevel.Warning)
                .Where(m => m.StartsWith("TodoBoardIdVanished", StringComparison.Ordinal)),
        ];
    }

    [Fact]
    public void GetTask_ForVanishedId_LogsTheWarningWithThreadTaskAndLastSeen()
    {
        var (manager, log) = Rehydrated();

        _ = manager.GetTask("2");

        var warnings = Vanishes(log);
        warnings.Should().ContainSingle();

        // One line must carry all three facts: asserting them separately would still pass if they were
        // scattered across unrelated records, and the whole value of this line is that it ties them together.
        warnings[0].Should().Contain("task 2").And.Contain(ThreadId).And.Contain("2026-08-30T11:22:33");

        log.EventNamesAtLevel(LogLevel.Warning).Should().Contain("TodoBoardIdVanished");
    }

    /// <summary>
    ///     The negative pin the issue asks for, with its non-vacuity proof welded on: the never-existed id
    ///     is silent, and the SAME manager, through the SAME call, still reports the id that genuinely
    ///     existed. Without the second half a broken-and-unreachable detector would pass this test.
    /// </summary>
    [Fact]
    public void GetTask_ForNeverExistedId_IsSilent_WhileAVanishedIdOnTheSameBoardStillWarns()
    {
        var (manager, log) = Rehydrated();

        _ = manager.GetTask("9");
        Vanishes(log).Should().BeEmpty("id 9 was never minted on this board — that is a model typo, not data loss");

        _ = manager.GetTask("2");
        Vanishes(log)
            .Should()
            .ContainSingle(
                "the identical call on an id this board really held must warn, which is what proves the silence above was a decision and not an unreached code path"
            );
    }

    /// <summary>
    ///     The other false positive, and the one that would have made the warning worthless in production:
    ///     an id the agent itself deleted. Paired with the same non-vacuity proof.
    /// </summary>
    [Fact]
    public void GetTask_AfterDeliberateDelete_IsSilent_WhileAVanishedIdOnTheSameBoardStillWarns()
    {
        var (manager, log) = Rehydrated();

        _ = manager.DeleteTask("1");
        _ = manager.GetTask("1");
        Vanishes(log)
            .Should()
            .BeEmpty("the board was ASKED to remove task 1; a later reference to it is the model's own mistake");

        _ = manager.GetTask("2");
        Vanishes(log).Should().ContainSingle();
    }

    /// <summary>
    ///     Deleting a row must take its whole subtree out of the ledger, not just the row: the children go
    ///     with it, and each would otherwise be reported as lost the first time anyone looked for it.
    /// </summary>
    [Fact]
    public void GetTask_ForChildOfADeletedTask_IsSilent_WhileAVanishedIdOnTheSameBoardStillWarns()
    {
        var (manager, log) = Rehydrated();

        _ = manager.DeleteTask("1");
        _ = manager.GetTask("1.1");
        Vanishes(log).Should().BeEmpty("1.1 was deleted along with its parent");

        _ = manager.GetTask("2");
        Vanishes(log).Should().ContainSingle();
    }

    /// <summary>
    ///     The subtask lookup route is a separate not-found exit with its own error, so it needs its own
    ///     pin: a vanished nested id warns, a never-minted ordinal under a live parent does not.
    /// </summary>
    [Fact]
    public void SubtaskLookup_ForVanishedOrdinal_Warns_WhileANeverMintedOrdinalIsSilent()
    {
        var (manager, log) = Rehydrated();

        _ = manager.GetTask("1", subtaskId: 7);
        Vanishes(log).Should().BeEmpty("task 1 never had a seventh subtask");

        _ = manager.GetTask("1", subtaskId: 2);
        Vanishes(log)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("task 1.2", "the warning names the dotted id the lookup addressed, not the parent");
    }

    /// <summary>
    ///     delete-task resolves subtasks through its own lookup rather than the shared one (it is carved
    ///     out of the <c>subtaskId &lt;= 0</c> tolerance, #631), so that exit is wired separately and pinned
    ///     separately.
    /// </summary>
    [Fact]
    public void DeleteTask_ForVanishedSubtask_Warns_WhileANeverMintedOrdinalIsSilent()
    {
        var (manager, log) = Rehydrated();

        _ = manager.DeleteTask("1", subtaskId: 7);
        Vanishes(log).Should().BeEmpty();

        _ = manager.DeleteTask("1", subtaskId: 2);
        Vanishes(log).Should().ContainSingle().Which.Should().Contain("delete-task");
    }

    /// <summary>
    ///     Lookup resolves <c>" 02"</c>, <c>"+2"</c> and <c>"2"</c> to the same row, so the ledger probe has
    ///     to use the PARSED id. Probing the raw input instead would let a loss go unreported purely
    ///     because of how the model spelled the number.
    /// </summary>
    [Theory]
    [InlineData("2")]
    [InlineData(" 2")]
    [InlineData("+2")]
    [InlineData("02")]
    public void GetTask_ForVanishedId_WarnsWhateverSpellingTheModelUsed(string spelling)
    {
        var (manager, log) = Rehydrated();

        _ = manager.GetTask(spelling);

        Vanishes(log).Should().ContainSingle();
    }

    /// <summary>
    ///     Every attempt against a lost row is a data-loss symptom, so the detector does not self-silence:
    ///     the #617 storm was 48 identical calls, and a report-once detector would have sized it at one.
    /// </summary>
    [Fact]
    public void RepeatedLookupsOfAVanishedId_WarnEveryTime()
    {
        var (manager, log) = Rehydrated();

        _ = manager.GetTask("2");
        _ = manager.GetTask("2");
        _ = manager.GetTask("2");

        Vanishes(log).Should().HaveCount(3);
    }

    /// <summary>
    ///     The tool's answer to the model is observability-free: this is a watch bolted alongside the
    ///     existing behavior, not a change to it. Pinned by comparing the two configurations that differ
    ///     only in whether anyone is listening.
    /// </summary>
    [Fact]
    public void NotFoundResponse_IsIdenticalWithAndWithoutTheDetectorListening()
    {
        var (watched, _) = Rehydrated();
        var unwatched = TaskManager.FromSnapshot(SnapshotWithVanishedTaskTwo());

        foreach (var (taskId, subtaskId) in new (string, int?)[] { ("2", null), ("9", null), ("1", 7), ("1", 2) })
        {
            var watchedResult = watched.GetTask(taskId, subtaskId);
            var unwatchedResult = unwatched.GetTask(taskId, subtaskId);

            watchedResult.ErrorCode.Should().Be(unwatchedResult.ErrorCode);
            watchedResult.Text.Should().Be(unwatchedResult.Text);
        }

        // And the text itself is still the wording the tool has always returned.
        watched.GetTask("2").Text.Should().Be("Error: Task '2' not found.");
        watched.GetTask("1", 7).ErrorCode.Should().Be("task_not_found");
    }

    /// <summary>
    ///     A manager with no logger wired — every throwaway construction site in the host — must behave
    ///     exactly as before rather than throwing on the detector path.
    /// </summary>
    [Fact]
    public void WithNoLoggerWired_TheDetectorIsInert()
    {
        var manager = TaskManager.FromSnapshot(SnapshotWithVanishedTaskTwo());

        var act = () => manager.GetTask("2");

        act.Should().NotThrow();
        act().Text.Should().Be("Error: Task '2' not found.");
    }

    /// <summary>
    ///     The durability half. A row lost in one process must still be reportable in the next one, which
    ///     it only is if the ledger's unresolved entries ride the persisted snapshot: a board that
    ///     rebuilt its ledger from surviving rows alone would read every lost id as never-minted.
    /// </summary>
    [Fact]
    public void LedgerSurvivesACaptureAndRehydrateRoundTrip()
    {
        var (first, _) = Rehydrated();

        var captured = first.GetTodoBoardSnapshot(ThreadId);
        captured
            .MissingTaskIds.Should()
            .ContainKey("2")
            .WhoseValue.Should()
            .Be(LastSeen, "the capture carries the id the board still owes and when it was last seen");
        captured.MissingTaskIds.Should().NotContainKey("1", "task 1 is on the board, so it is not missing");

        var log = new CapturingLogger<TaskManager>();
        var second = TaskManager.FromSnapshot(captured);
        second.Logger = log;
        second.ThreadId = ThreadId;

        _ = second.GetTask("9");
        Vanishes(log).Should().BeEmpty();

        _ = second.GetTask("2");
        Vanishes(log).Should().ContainSingle().Which.Should().Contain("2026-08-30T11:22:33");
    }

    /// <summary>
    ///     A row added and then lost within one live instance is the same event seen from the other side:
    ///     the id was minted here, so the ledger holds it, and the capture reports it as owed.
    /// </summary>
    [Fact]
    public void ARowMintedInThisProcessIsOwedByTheLedgerUntilItIsDeleted()
    {
        var manager = new TaskManager();
        _ = manager.AddTask("Kept");
        _ = manager.AddTask("Deleted later");

        manager.GetTodoBoardSnapshot(ThreadId).MissingTaskIds.Should().BeEmpty("both rows are present");

        _ = manager.DeleteTask("2");

        manager
            .GetTodoBoardSnapshot(ThreadId)
            .MissingTaskIds.Should()
            .BeEmpty("a deleted row is not owed — the board was asked to drop it");
    }

    /// <summary>
    ///     bulk-initialize with clearExisting is a requested reset that also renumbers from 1. Keeping the
    ///     old ids would report every re-used id as vanished the moment the new board is smaller.
    /// </summary>
    [Fact]
    public void BulkInitializeWithClearExisting_ResetsTheLedger_ButAVanishedIdOnAFreshBoardStillWarns()
    {
        var (manager, log) = Rehydrated();

        _ = manager.BulkInitialize([new TaskManager.BulkTaskItem { Task = "Fresh" }], clearExisting: true);

        _ = manager.GetTask("2");
        Vanishes(log).Should().BeEmpty("the board was reset on request; id 2 belongs to a board that no longer exists");

        var (control, controlLog) = Rehydrated();
        _ = control.GetTask("2");
        Vanishes(controlLog)
            .Should()
            .ContainSingle("the same call on a board that was NOT reset still reports the loss");
    }
}
