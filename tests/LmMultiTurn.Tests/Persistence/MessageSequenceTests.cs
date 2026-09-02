using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins <see cref="PersistedMessage.Seq"/>, the message watermark and the bounded range read across
/// every <see cref="IConversationStore"/> flavour (#680; spec 679 §2.2, §8.3, §12.1).
/// </summary>
public sealed class MessageSequenceTests : IAsyncLifetime
{
    private const string Thread = "thread-seq";
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    public static TheoryData<string> DurableKinds => ConversationStoreHarness.DurableKinds;

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Append_AssignsMonotonicGapFreeSeq_AndTheWatermarkFollowsIt(string kind)
    {
        var store = _harness.Open(kind);

        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(0, "an empty thread has no rows");

        await store.AppendMessagesAsync(
            Thread,
            [ConversationStoreHarness.Row(Thread, "a", 100), ConversationStoreHarness.Row(Thread, "b", 100, 1)]
        );
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(2);

        await store.AppendMessagesAsync(
            Thread,
            [
                ConversationStoreHarness.Row(Thread, "c", 200),
                ConversationStoreHarness.Row(Thread, "d", 200, 1),
                ConversationStoreHarness.Row(Thread, "e", 200, 2),
            ]
        );
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(5);

        var loaded = await store.LoadMessagesAsync(Thread);
        loaded.Select(m => m.Seq).Should().Equal(1, 2, 3, 4, 5);
        loaded.Select(m => m.Id).Should().Equal("a", "b", "c", "d", "e");

        (await store.GetMessageWatermarkAsync("other-thread")).Should().Be(0, "the watermark is per thread");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Append_IgnoresACallerSuppliedSeq_TheStoreOwnsIt(string kind)
    {
        var store = _harness.Open(kind);

        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "a", 100) with { Seq = 99 }]);

        (await store.LoadMessagesAsync(Thread)).Single().Seq.Should().Be(1);
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task LoadMessages_OrdersBySeq_WhenTimestampsDisagree(string kind)
    {
        // The distinguishing case: a row appended LATER with an EARLIER timestamp. (timestamp, idx) is
        // not a total order (a clock step, two rows in one ms); Seq is.
        var store = _harness.Open(kind);
        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "later-ts", 200)]);
        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "earlier-ts", 100)]);

        var loaded = await store.LoadMessagesAsync(Thread);

        loaded.Select(m => m.Id).Should().Equal("later-ts", "earlier-ts");
        loaded.Select(m => m.Seq).Should().Equal(1, 2);
    }

    [Theory]
    [MemberData(nameof(DurableKinds))]
    public async Task Backfill_AssignsSeqToLegacyRowsInLoadOrder_OnceOnly(string kind)
    {
        _ = _harness.Open(kind);
        await _harness.SeedLegacyRowsAsync(
            kind,
            Thread,
            [
                ConversationStoreHarness.Row(Thread, "ts100-idx1", 100, 1),
                ConversationStoreHarness.Row(Thread, "ts50", 50),
                ConversationStoreHarness.Row(Thread, "ts100-idx0", 100, 0),
            ]
        );

        var store = _harness.Reopen(kind);

        // Until backfilled the watermark is 0 (§8.3) and the legacy order is (timestamp, idx).
        (await store.GetMessageWatermarkAsync(Thread))
            .Should()
            .Be(0);
        var legacy = await store.LoadMessagesAsync(Thread);
        legacy.Select(m => m.Seq).Should().AllSatisfy(seq => seq.Should().BeNull());
        legacy.Select(m => m.Id).Should().Equal("ts50", "ts100-idx0", "ts100-idx1");

        // First append backfills in that order, then continues past it.
        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "new-1", 300)]);
        var afterFirst = await store.LoadMessagesAsync(Thread);
        afterFirst
            .Select(m => (m.Id, m.Seq))
            .Should()
            .Equal(("ts50", 1L), ("ts100-idx0", 2L), ("ts100-idx1", 3L), ("new-1", 4L));
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(4);

        // A second append (and a fresh handle) leaves the backfilled numbers exactly where they were.
        await _harness.Reopen(kind).AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "new-2", 400)]);
        var afterSecond = await _harness.Reopen(kind).LoadMessagesAsync(Thread);
        afterSecond
            .Select(m => (m.Id, m.Seq))
            .Should()
            .Equal(("ts50", 1L), ("ts100-idx0", 2L), ("ts100-idx1", 3L), ("new-1", 4L), ("new-2", 5L));
    }

    [Fact]
    public async Task Backfill_NumbersLegacyRowsThatTieOnTimestampAndIdx_InTheOrderTheyLoaded()
    {
        // The distinguishing case for the rowid tiebreak. (timestamp, message_order_idx) is not a
        // total order over legacy rows: message_order_idx restarts per generation, and two generations
        // can share a millisecond. The backfill numbers such ties by rowid; the load must break them the
        // same way, or the order a reader saw before the first append is not the order Seq records
        // after it (spec 679 §8.3: Seq is assigned in current load order). Ids are chosen so that
        // neither lexical order nor reverse insertion order coincides with rowid order. Dropping the
        // tiebreak happens to pass today (the sorter preserves the rowid-ordered scan it is fed), so
        // what this pins is the contract SQL does not promise; reversing the tiebreak fails it.
        var store = _harness.Open("sqlite");
        _ = await store.GetMessageWatermarkAsync(Thread);
        await _harness.InsertTiedLegacySqliteRowsAsync(
            Thread,
            ["z-first", "a-second", "m-third"],
            timestamp: 100,
            orderIdx: 0
        );

        var before = (await store.LoadMessagesAsync(Thread)).Select(m => m.Id).ToList();
        before.Should().Equal(["z-first", "a-second", "m-third"], "ties load in insertion (rowid) order");

        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "new", 200)]);

        var after = await _harness.Reopen("sqlite").LoadMessagesAsync(Thread);
        after
            .Select(m => (m.Id, m.Seq))
            .Should()
            .Equal(("z-first", 1L), ("a-second", 2L), ("m-third", 3L), ("new", 4L));
        after.Take(3).Select(m => m.Id).Should().Equal(before, "the backfill numbers ties in the order they loaded");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task LoadMessageRange_HonoursBothBounds_AndTheLimit(string kind)
    {
        var store = _harness.Open(kind);
        await store.AppendMessagesAsync(
            Thread,
            [.. Enumerable.Range(1, 6).Select(i => ConversationStoreHarness.Row(Thread, $"m{i}", 100 + i))]
        );

        (await store.LoadMessageRangeAsync(Thread, 2, 5, limit: 2)).Select(m => m.Seq).Should().Equal(2, 3);
        (await store.LoadMessageRangeAsync(Thread, 4, 100, limit: 10)).Select(m => m.Seq).Should().Equal(4, 5, 6);
        (await store.LoadMessageRangeAsync(Thread, 7, 9, limit: 10)).Should().BeEmpty();
        (await store.LoadMessageRangeAsync(Thread, 3, 2, limit: 10)).Should().BeEmpty("an inverted range is empty");
        (await store.LoadMessageRangeAsync("other-thread", 1, 6, limit: 10)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReplaceMessage_KeepsTheRowsSeq_AndItsPosition(string kind)
    {
        var store = _harness.Open(kind);
        var placeholder = ConversationStoreHarness.Row(
            Thread,
            "tcr:thread-seq:call-1",
            100,
            messageType: "ToolCallResultMessage",
            role: "Tool",
            messageJson: """{"$type":"tool_call_result","tool_call_id":"call-1","result":"pending"}"""
        );
        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "before", 50), placeholder]);
        await store.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "after", 150)]);

        // The replacement is built by MessagePersistenceConverter with no Seq (and a new timestamp).
        await store.ReplaceMessageAsync(
            Thread,
            placeholder with
            {
                Timestamp = 999,
                MessageJson = """{"$type":"tool_call_result","tool_call_id":"call-1","result":"done"}""",
            }
        );

        var loaded = await store.LoadMessagesAsync(Thread);
        loaded.Select(m => (m.Id, m.Seq)).Should().Equal(("before", 1L), ("tcr:thread-seq:call-1", 2L), ("after", 3L));
        loaded[1].MessageJson.Should().Contain("done");
        (await store.GetMessageWatermarkAsync(Thread)).Should().Be(3, "a replacement is not an append");
    }

    [Theory]
    [MemberData(nameof(DurableKinds))]
    public async Task Seq_SurvivesARestart_AndIsSeenByASecondHandle(string kind)
    {
        var first = _harness.Open(kind);
        await first.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "a", 100)]);

        // Another process appends...
        var second = _harness.Reopen(kind);
        await second.AppendMessagesAsync(Thread, [ConversationStoreHarness.Row(Thread, "b", 200)]);

        // ...and the first handle's watermark moves, because the watermark is read from the store, not
        // from anything the handle remembers. This is the precondition the checkpoint guard relies on.
        (await first.GetMessageWatermarkAsync(Thread))
            .Should()
            .Be(2);
        (await _harness.Reopen(kind).LoadMessagesAsync(Thread)).Select(m => m.Seq).Should().Equal(1, 2);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task CheckpointRow_RoundTripsThroughTheStore_WithItsTypeDiscriminator(string kind)
    {
        var store = _harness.Open(kind);
        var checkpoint = SampleCheckpoint(Thread, "cp-1", boundarySeq: 2);
        await store.AppendMessagesAsync(
            Thread,
            [ConversationStoreHarness.Row(Thread, "a", 100), ConversationStoreHarness.Row(Thread, "b", 101)]
        );
        await store.AppendMessagesAsync(
            Thread,
            [MessagePersistenceConverter.ToPersistedMessage(checkpoint, Thread, "run-1")]
        );

        var rows = await store.LoadMessagesAsync(Thread);
        var row = rows[2];
        row.Seq.Should().Be(3);
        row.MessageType.Should().Be(nameof(CompactionCheckpointMessage));
        row.Role.Should().Be("User");
        row.MessageJson.Should().Contain("\"$type\":\"compaction_checkpoint\"");

        var restored = MessagePersistenceConverter.FromPersistedMessagesResilient(rows);
        restored.Should().HaveCount(3, "a checkpoint row is neither a tool call nor a tool result");
        var typed = restored[2].Should().BeOfType<CompactionCheckpointMessage>().Subject;
        typed.CheckpointId.Should().Be("cp-1");
        typed.Boundary.Should().Be(new CheckpointBoundary { Seq = 2, MessageId = "b" });
        typed.Trigger.Should().Be(CompactionTrigger.Preemptive);
        typed.Manifest.CurrentInstruction.Should().ContainSingle().Which.Quote.Should().Be("fix the flaky test");
        typed.Manifest.Index.Should().ContainSingle().Which.Headline.Should().Be("first run");
        typed.Narrative.Should().Be("We fixed things.");
        typed.Text.Should().Contain("<context-checkpoint").And.Contain("fix the flaky test");
    }

    internal static CompactionCheckpointMessage SampleCheckpoint(string threadId, string id, long boundarySeq) =>
        new()
        {
            CheckpointId = id,
            Boundary = new CheckpointBoundary { Seq = boundarySeq, MessageId = "b" },
            Trigger = CompactionTrigger.Preemptive,
            Manifest = new ContextManifest
            {
                CurrentInstruction = [new QuotedItem { Seq = 1, Quote = "fix the flaky test" }],
                Instructions = [new QuotedItem { Seq = 1, Quote = "never push" }],
                Goals = ["green CI"],
                Index =
                [
                    new IndexEntry
                    {
                        FromSeq = 1,
                        ToSeq = boundarySeq,
                        RunId = "run-1",
                        Headline = "first run",
                    },
                ],
            },
            Narrative = "We fixed things.",
            Stats = new CheckpointStats { RowsCovered = boundarySeq },
            CreatedAtUtc = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
            ThreadId = threadId,
            RunId = "run-1",
        };
}
