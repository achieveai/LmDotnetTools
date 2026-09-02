using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins the checkpoint state machine and its watermark guard on every store flavour (#680; spec 679
/// §3.5, §12.1): activation only when the watermark captured at prepare is still current, an idempotent
/// commit/activate retry, durably distinguishable states, last-known-good on rollback, and a newer
/// schema an older runtime must not overwrite.
/// </summary>
public sealed class CompactionStateProjectionTests : IAsyncLifetime
{
    private const string Thread = "thread-cp";
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    private static async Task<long> SeedRowsAsync(IConversationStore store, int count, string prefix = "r")
    {
        await store.AppendMessagesAsync(
            Thread,
            [.. Enumerable.Range(1, count).Select(i => ConversationStoreHarness.Row(Thread, $"{prefix}{i}", 100 + i))]
        );
        return await store.GetMessageWatermarkAsync(Thread);
    }

    private static Task AppendCheckpointRowAsync(IConversationStore store, string id, long boundarySeq) =>
        store.AppendMessagesAsync(
            Thread,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    MessageSequenceTests.SampleCheckpoint(Thread, id, boundarySeq),
                    Thread,
                    "run-1"
                ),
            ]
        );

    /// <summary>Prepare → Validated → Committed → row → Active, reading back through a fresh handle at each step.</summary>
    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task HappyPath_EveryStateIsDurablyDistinguishable(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);

        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            boundarySeq: 2,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );
        (await Reload(kind, "cp-1"))
            .Should()
            .BeEquivalentTo(
                new
                {
                    Status = CheckpointStatus.Prepared,
                    BoundarySeq = 2L,
                    WatermarkAtPrepare = 3L,
                    RowSeq = (long?)null,
                }
            );

        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, "cp-1", T0.AddSeconds(1));
        (await Reload(kind, "cp-1"))!.Status.Should().Be(CheckpointStatus.Validated);

        var committed = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0.AddSeconds(2));
        committed!.Find("cp-1")!.Status.Should().Be(CheckpointStatus.Committed);
        (await Reload(kind, "cp-1"))!.Status.Should().Be(CheckpointStatus.Committed);
        (await ReloadState(kind)).ActiveCheckpointId.Should().BeNull("nothing is active until the row exists");

        await AppendCheckpointRowAsync(store, "cp-1", 2);
        var activated = await CompactionStateProjection.ActivateAsync(
            store,
            Thread,
            "cp-1",
            rowSeq: 4,
            T0.AddSeconds(3)
        );
        activated!.ActiveCheckpointId.Should().Be("cp-1");

        var state = await ReloadState(kind);
        state.ActiveCheckpointId.Should().Be("cp-1");
        state.ActiveBoundarySeq.Should().Be(2);
        state.LastKnownGoodCheckpointId.Should().Be("cp-1");
        state
            .Find("cp-1")
            .Should()
            .BeEquivalentTo(
                new
                {
                    Status = CheckpointStatus.Active,
                    RowSeq = (long?)4L,
                    At = T0.AddSeconds(3),
                }
            );
        state.History.Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task TryCommit_RejectsWhenARowWasAppendedBetweenPrepareAndCommit_AndKeepsTheView(string kind)
    {
        var store = _harness.Open(kind);

        // cp-0 is live so the test can show the VIEW did not change, not only that cp-1 was refused.
        var watermark = await SeedRowsAsync(store, 2);
        await Activate(store, "cp-0", boundarySeq: 1, watermark);

        watermark = await SeedRowsAsync(store, 2, "tail");
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            boundarySeq: 3,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, "cp-1", T0);

        // A second process appends while the summarizer is running.
        await _harness
            .Reopen(kind)
            .AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "concurrent", 999)]);

        var result = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0.AddSeconds(1));

        result!
            .Find("cp-1")
            .Should()
            .BeEquivalentTo(new { Status = CheckpointStatus.Rejected, Reason = CheckpointReasons.StaleWatermark });
        result.ActiveCheckpointId.Should().Be("cp-0");
        result.ActiveBoundarySeq.Should().Be(1);
        var state = await ReloadState(kind);
        state.ActiveCheckpointId.Should().Be("cp-0", "a rejected commit never touches the active pointer");
        state.Find("cp-0")!.Status.Should().Be(CheckpointStatus.Active);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Activate_RejectsWhenARowSlippedInBetweenCommitAndTheCheckpointRow(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            2,
            watermark,
            CompactionTrigger.Reactive,
            T0
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, "cp-1", T0);
        _ = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0);

        await _harness
            .Reopen(kind)
            .AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "concurrent", 999)]);
        await AppendCheckpointRowAsync(store, "cp-1", 2);
        var rowSeq = await store.GetMessageWatermarkAsync(Thread);
        rowSeq.Should().Be(5, "the concurrent row took seq 4");

        var result = await CompactionStateProjection.ActivateAsync(store, Thread, "cp-1", rowSeq, T0.AddSeconds(1));

        result!
            .Find("cp-1")
            .Should()
            .BeEquivalentTo(
                new
                {
                    Status = CheckpointStatus.Rejected,
                    Reason = CheckpointReasons.StaleWatermark,
                    RowSeq = (long?)5L,
                }
            );
        result.ActiveCheckpointId.Should().BeNull();
        (await ReloadState(kind)).ActiveCheckpointId.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task CommitAndActivate_Retried_AreNoOps(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            2,
            watermark,
            CompactionTrigger.Manual,
            T0
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, "cp-1", T0);

        var first = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0.AddSeconds(1));
        // A crash-and-retry between commit and the row append: the retry sees Committed and does nothing,
        // even though a retry that re-checked the watermark would still pass here.
        var retried = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0.AddSeconds(2));
        retried.Should().BeEquivalentTo(first, "a repeated commit changes nothing, not even the timestamp");

        await AppendCheckpointRowAsync(store, "cp-1", 2);
        var activated = await CompactionStateProjection.ActivateAsync(store, Thread, "cp-1", 4, T0.AddSeconds(3));
        var reactivated = await CompactionStateProjection.ActivateAsync(store, Thread, "cp-1", 4, T0.AddSeconds(4));
        reactivated.Should().BeEquivalentTo(activated);

        // ...and a retry of the whole sequence after a row exists is refused, not re-run: a second row
        // would have a different seq, so it fails the same guard as any other stale activation.
        await _harness.Reopen(kind).AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "later", 999)]);
        var stale = await CompactionStateProjection.ActivateAsync(store, Thread, "cp-1", 5, T0.AddSeconds(5));
        stale.Should().BeEquivalentTo(activated, "an already-active checkpoint ignores a mismatched retry");

        var state = await ReloadState(kind);
        state.History.Should().ContainSingle();
        state.ActiveCheckpointId.Should().Be("cp-1");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task NewerSchema_IsNeverOverwritten_AndReadsAsAbsent(string kind)
    {
        var store = _harness.Open(kind);
        _ = await SeedRowsAsync(store, 2);
        const string future =
            """{"schema_version":2,"active_checkpoint_id":"cp-future","history":[],"a_new_field":true}""";
        await store.UpdateMetadataAsync(
            Thread,
            existing =>
                (existing ?? new ThreadMetadata { ThreadId = Thread, LastUpdated = 0 }) with
                {
                    Properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                        CompactionStateProjection.PropertyKey,
                        future
                    ),
                }
        );

        (await CompactionStateProjection.LoadAsync(store, Thread))
            .Should()
            .BeNull("this build cannot interpret schema 2");

        var prepared = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            1,
            2,
            CompactionTrigger.Preemptive,
            T0
        );
        var rejected = await CompactionStateProjection.RejectAsync(store, Thread, "cp-1", "validation_failed:V3", T0);
        var rolledBack = await CompactionStateProjection.RollBackAsync(store, Thread, "killed", T0);

        prepared.Should().BeNull();
        rejected.Should().BeNull();
        rolledBack.Should().BeNull();
        var raw = (await store.LoadMetadataAsync(Thread))!.Properties![CompactionStateProjection.PropertyKey];
        RawString(raw).Should().Be(future);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task RollBack_RestoresTheLastKnownGood_ThenRawHistory(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        await Activate(store, "cp-1", boundarySeq: 2, watermark);
        watermark = await SeedRowsAsync(store, 2, "tail");
        await Activate(store, "cp-2", boundarySeq: 5, watermark);

        var chained = await ReloadState(kind);
        chained.ActiveCheckpointId.Should().Be("cp-2");
        chained.LastKnownGoodCheckpointId.Should().Be("cp-2");
        chained.Find("cp-1")!.Status.Should().Be(CheckpointStatus.Superseded);

        var rolled = await CompactionStateProjection.RollBackAsync(
            store,
            Thread,
            "overflow_after_compaction",
            T0.AddMinutes(1)
        );

        rolled!
            .Find("cp-2")
            .Should()
            .BeEquivalentTo(new { Status = CheckpointStatus.RolledBack, Reason = "overflow_after_compaction" });
        rolled.ActiveCheckpointId.Should().Be("cp-1", "the previous good checkpoint is the fallback view");
        rolled.ActiveBoundarySeq.Should().Be(2);
        rolled.LastKnownGoodCheckpointId.Should().Be("cp-1");
        rolled.Find("cp-1")!.Status.Should().Be(CheckpointStatus.Active);

        var raw = await CompactionStateProjection.RollBackAsync(store, Thread, "killed", T0.AddMinutes(2));

        raw!.ActiveCheckpointId.Should().BeNull("with no good checkpoint left the view is raw history");
        raw.ActiveBoundarySeq.Should().BeNull();
        raw.LastKnownGoodCheckpointId.Should().BeNull();
        raw.Find("cp-1")!.Status.Should().Be(CheckpointStatus.RolledBack);
        (await ReloadState(kind)).Should().BeEquivalentTo(raw);

        (await CompactionStateProjection.RollBackAsync(store, Thread, "killed", T0.AddMinutes(3)))
            .Should()
            .BeEquivalentTo(raw, "rolling back with nothing active changes nothing");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Reconcile_MarksACommittedCheckpointWithoutItsRow_Rejected_RowMissing(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            2,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, "cp-1", T0);
        _ = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0);

        // The process died before AppendMessagesAsync(checkpoint). On restart canonical history wins.
        var reconciled = await CompactionStateProjection.ReconcileAsync(
            _harness.Reopen(kind),
            Thread,
            T0.AddMinutes(1)
        );

        reconciled!
            .Find("cp-1")
            .Should()
            .BeEquivalentTo(new { Status = CheckpointStatus.Rejected, Reason = CheckpointReasons.RowMissing });
        reconciled.ActiveCheckpointId.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Reconcile_ActivatesACommittedCheckpointWhoseRowExists(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            2,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, "cp-1", T0);
        _ = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0);
        await AppendCheckpointRowAsync(store, "cp-1", 2);

        // The process died between the row append and the activation write.
        var reconciled = await CompactionStateProjection.ReconcileAsync(
            _harness.Reopen(kind),
            Thread,
            T0.AddMinutes(1)
        );

        reconciled!.Find("cp-1").Should().BeEquivalentTo(new { Status = CheckpointStatus.Active, RowSeq = (long?)4L });
        reconciled.ActiveCheckpointId.Should().Be("cp-1");
        reconciled.ActiveBoundarySeq.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Reconcile_RejectsACommittedCheckpointWhoseRowIsSomeoneElses(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            2,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, "cp-1", T0);
        _ = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-1", T0);

        // The row at watermark+1 is an ordinary message, not cp-1's row.
        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "not-a-checkpoint", 999)]);

        var reconciled = await CompactionStateProjection.ReconcileAsync(store, Thread, T0.AddMinutes(1));

        reconciled!
            .Find("cp-1")
            .Should()
            .BeEquivalentTo(new { Status = CheckpointStatus.Rejected, Reason = CheckpointReasons.RowMissing });
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Prepare_AbandonsAnInFlightCheckpoint_SoAtMostOneIsEverInFlight(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-1",
            2,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );

        var state = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-2",
            2,
            watermark,
            CompactionTrigger.Manual,
            T0.AddSeconds(1)
        );

        state!
            .Find("cp-1")
            .Should()
            .BeEquivalentTo(new { Status = CheckpointStatus.Rejected, Reason = CheckpointReasons.Abandoned });
        state.Find("cp-2")!.Status.Should().Be(CheckpointStatus.Prepared);
        state.InFlight.Should().ContainSingle().Which.CheckpointId.Should().Be("cp-2");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Reject_RecordsTheTypedReason_AndLeavesTheActiveViewAlone(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        await Activate(store, "cp-1", 2, watermark);
        watermark = await SeedRowsAsync(store, 1, "tail");
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            "cp-2",
            4,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );

        var state = await CompactionStateProjection.RejectAsync(
            store,
            Thread,
            "cp-2",
            "validation_failed:V6",
            T0.AddSeconds(1)
        );

        state!
            .Find("cp-2")
            .Should()
            .BeEquivalentTo(new { Status = CheckpointStatus.Rejected, Reason = "validation_failed:V6" });
        state.ActiveCheckpointId.Should().Be("cp-1");
        state.LastKnownGoodCheckpointId.Should().Be("cp-1");

        // A terminal entry does not move again.
        var again = await CompactionStateProjection.TryCommitAsync(store, Thread, "cp-2", T0.AddSeconds(2));
        again!.Find("cp-2")!.Status.Should().Be(CheckpointStatus.Rejected);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task History_IsBounded_ButNeverDropsTheActiveOrLastKnownGoodEntry(string kind)
    {
        var store = _harness.Open(kind);
        var watermark = await SeedRowsAsync(store, 3);
        await Activate(store, "cp-active", 2, watermark);

        for (var i = 0; i < CompactionState.HistoryLength + 5; i++)
        {
            _ = await CompactionStateProjection.PrepareAsync(
                store,
                Thread,
                $"cp-{i}",
                2,
                await store.GetMessageWatermarkAsync(Thread),
                CompactionTrigger.Shadow,
                T0.AddSeconds(i)
            );
            _ = await CompactionStateProjection.RejectAsync(
                store,
                Thread,
                $"cp-{i}",
                "validation_failed:V7",
                T0.AddSeconds(i)
            );
        }

        var state = await ReloadState(kind);
        state.History.Should().HaveCount(CompactionState.HistoryLength);
        state.Find("cp-active")!.Status.Should().Be(CheckpointStatus.Active);
        state.Find("cp-0").Should().BeNull("the oldest rejected entries are what gets trimmed");
        state.ActiveCheckpointId.Should().Be("cp-active");
    }

    private async Task Activate(IConversationStore store, string id, long boundarySeq, long watermark)
    {
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            id,
            boundarySeq,
            watermark,
            CompactionTrigger.Preemptive,
            T0
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, id, T0);
        var committed = await CompactionStateProjection.TryCommitAsync(store, Thread, id, T0);
        committed!.Find(id)!.Status.Should().Be(CheckpointStatus.Committed);
        await AppendCheckpointRowAsync(store, id, boundarySeq);
        var activated = await CompactionStateProjection.ActivateAsync(store, Thread, id, watermark + 1, T0);
        activated!.Find(id)!.Status.Should().Be(CheckpointStatus.Active);
    }

    private async Task<CompactionState> ReloadState(string kind) =>
        (await CompactionStateProjection.LoadAsync(_harness.Reopen(kind), Thread))
        ?? throw new InvalidOperationException("no compaction state persisted");

    private async Task<CheckpointEntry?> Reload(string kind, string checkpointId) =>
        (await ReloadState(kind)).Find(checkpointId);

    private static string RawString(object raw) =>
        raw switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString()!,
            _ => raw.ToString()!,
        };
}
