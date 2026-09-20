using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins the execution view (#683; spec 679 §2.1, I3, I3a, §12.2): system prompt, one envelope for the
/// active checkpoint, the tail after its boundary, and never a checkpoint row of any status; and that
/// replaying the store yields the same view, before and after a restart.
/// </summary>
public sealed class AgentContextProjectionTests : IAsyncLifetime
{
    private const string Thread = "thread-view";
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    public static TheoryData<string> DurableKinds => ConversationStoreHarness.DurableKinds;

    private static CompactionCheckpointMessage Checkpoint(string id, long boundarySeq, string messageId = "m") =>
        new()
        {
            CheckpointId = id,
            Boundary = new CheckpointBoundary { Seq = boundarySeq, MessageId = messageId },
            Trigger = CompactionTrigger.Preemptive,
            Manifest = new ContextManifest { Goals = ["green CI"] },
            Narrative = $"narrative of {id}",
        };

    [Fact]
    public void WithoutACheckpoint_TheViewIsTheSystemPromptAndEveryRow_Unchanged()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(3).Assistant("done");

        var view = AgentContextProjection.Default.Build("be terse", thread.Messages, active: null);

        view.Should().HaveCount(thread.Messages.Count + 1);
        view[0].Should().BeEquivalentTo(new TextMessage { Text = "be terse", Role = Role.System });
        for (var i = 0; i < thread.Messages.Count; i++)
        {
            view[i + 1].Should().BeSameAs(thread.Messages[i], "rows are dispatched as they are, not copied");
        }
    }

    [Fact]
    public void WithoutASystemPrompt_TheViewStartsAtTheFirstRow()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(1);

        AgentContextProjection.Default.Build(null, thread.Messages, active: null).Should().HaveCount(3);
        AgentContextProjection.Default.Build("", thread.Messages, active: null).Should().HaveCount(3);
    }

    [Fact]
    public void WithAnActiveCheckpoint_OneEnvelopeReplacesEveryRowUpToTheBoundary()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(5); // 11 rows
        var active = Checkpoint("cp-1", boundarySeq: ThreadFixture.TurnEnd(2));

        var view = AgentContextProjection.Default.Build("sys", thread.Rows, active);

        view.Should().HaveCount(1 + 1 + 6);
        view[0].Should().BeEquivalentTo(new TextMessage { Text = "sys", Role = Role.System });
        var envelope = view[1].Should().BeOfType<TextMessage>().Subject;
        envelope.Role.Should().Be(Role.User);
        envelope.Text.Should().Be(active.RenderEnvelope(CheckpointRenderOptions.Default));
        view.Skip(2)
            .Select(m => m)
            .Should()
            .Equal(thread.Rows.Where(r => r.Seq > ThreadFixture.TurnEnd(2)).Select(r => r.Message));
    }

    [Fact]
    public void RenderOptions_ReachTheEnvelope()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(2);
        var active = Checkpoint("cp-1", boundarySeq: 3);

        var view = AgentContextProjection.Default.Build(
            null,
            thread.Rows,
            active,
            new CheckpointRenderOptions { RecallToolName = "RecallConversation" }
        );

        view[0].Should().BeOfType<TextMessage>().Which.Text.Should().Contain("Use RecallConversation to read");
    }

    [Fact]
    public void EveryCheckpointRow_IsDropped_WhateverItsStatus()
    {
        var superseded = Checkpoint("cp-1", boundarySeq: 3);
        var active = Checkpoint("cp-2", boundarySeq: 7);
        var rolledBack = Checkpoint("cp-3", boundarySeq: 11);
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn() // 2,3
            .Checkpoint(superseded) // 4
            .ToolTurn() // 5,6
            .ToolTurn() // 7,8
            .Checkpoint(active) // 9
            .ToolTurn() // 10,11
            .Checkpoint(rolledBack) // 12
            .ToolTurn(); // 13,14

        var view = AgentContextProjection.Default.Build(null, thread.Rows, active);

        view.Should().NotContain(m => m is CompactionCheckpointMessage);
        view.Skip(1)
            .Select(m => m)
            .Should()
            .Equal(
                thread.Rows.Where(r => r.Seq is 10 or 11 or 13 or 14).Select(r => r.Message),
                "the boundary splits the turn at 7-8, so the orphaned result at 8 goes with its hidden call"
            );
    }

    [Fact]
    public void WithoutAnActiveCheckpoint_RolledBackRowsAreStillDropped_AndEveryOtherRowIsSent()
    {
        var rolledBack = Checkpoint("cp-1", boundarySeq: 3);
        var thread = new ThreadFixture().Human("go").ToolTurn().Checkpoint(rolledBack).ToolTurn();

        var view = AgentContextProjection.Default.Build(null, thread.Messages, active: null);

        view.Should().HaveCount(5).And.NotContain(m => m is CompactionCheckpointMessage);
    }

    [Fact]
    public void Describe_CountsHiddenAndTailRows_WithoutTheCheckpointRows()
    {
        var active = Checkpoint("cp-2", boundarySeq: 7);
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn()
            .Checkpoint(Checkpoint("cp-1", 3))
            .ToolTurn()
            .ToolTurn()
            .Checkpoint(active)
            .ToolTurn();
        var projection = new AgentContextProjection(ThreadFixture.RowTokens);

        var described = projection.Describe(thread.Rows, active);

        described.ActiveCheckpointId.Should().Be("cp-2");
        described.BoundarySeq.Should().Be(7);
        described.RowsHidden.Should().Be(6, "seq 1,2,3,5,6,7 — the checkpoint row at 4 is not a hidden row");
        described.RowsInTail.Should().Be(2, "seq 10,11 — the result at 8 lost the call at 7 to the cut");
        described.EstimatedTokens.Should().Be(3 * ThreadFixture.TokensPerRow, "envelope plus two rows");

        var raw = projection.Describe(thread.Rows, active: null);
        raw.ActiveCheckpointId.Should().BeNull();
        raw.RowsHidden.Should().Be(0);
        raw.RowsInTail.Should().Be(9);
    }

    // ---- Tool pairing across the cut -----------------------------------------------------------

    [Fact]
    public void ACutBetweenAToolCallAndItsResult_DropsTheStrandedResultToo()
    {
        var thread = new ThreadFixture().Human("go").ToolTurn(); // 1 human, 2 call, 3 result
        var active = Checkpoint("cp-1", boundarySeq: 2);

        var view = AgentContextProjection.Default.Build(null, thread.Rows, active);

        view.Should().ContainSingle("only the envelope survives: a result the cut orphaned cannot be sent");
        view[0].Should().BeOfType<TextMessage>().Which.Role.Should().Be(Role.User);
    }

    [Fact]
    public void AResultWhoseSeqWasNeverRecovered_GoesWithTheCallTheCutHid()
    {
        // What a restored thread produces: a row whose identity the recovery walk could not match is
        // numbered long.MaxValue — "newer than any boundary" — so the result rides into the tail while
        // its call keeps a real seq below the boundary and is cut.
        var rows = Renumbered(
            new ThreadFixture().Human("go").ToolTurn().Human("and then"),
            m => m is ToolCallResultMessage
        );
        var active = Checkpoint("cp-1", boundarySeq: 3);

        var view = AgentContextProjection.Default.Build(null, rows, active);

        view.Should().NotContain(m => m is ToolCallResultMessage, "the call answering it was hidden by the cut");
        view.Should().HaveCount(2, "the envelope and the human row after the boundary");
    }

    [Fact]
    public void ACallWhoseResultTheCutHid_GoesWithIt()
    {
        var rows = Renumbered(new ThreadFixture().Human("go").ToolTurn().Human("and then"), m => m is ToolCallMessage);
        var active = Checkpoint("cp-1", boundarySeq: 3);

        var view = AgentContextProjection.Default.Build(null, rows, active);

        view.Should().NotContain(m => m is ToolCallMessage, "nothing left in the view answers it");
        view.Should().HaveCount(2);
    }

    /// <summary>The thread's rows with every row matching <paramref name="stranded" /> renumbered past any boundary.</summary>
    private static IReadOnlyList<SequencedMessage> Renumbered(ThreadFixture thread, Func<IMessage, bool> stranded) =>
        [.. thread.Rows.Select(r => stranded(r.Message) ? r with { Seq = long.MaxValue } : r)];

    // ---- Tool-result view shaping --------------------------------------------------------------

    [Fact]
    public void ToolResultCap_TrimsAnOversizedResultToHeadMarkerAndTail_AndLeavesTheRowWhole()
    {
        var big = string.Concat(Enumerable.Range(0, 12_400).Select(i => $"row {i:D5}\n")); // 124,000 chars
        var thread = new ThreadFixture().Human("go").ToolTurn(result: big).ToolTurn(result: "small");
        var shaping = new ToolResultViewOptions { CapChars = 10_000 };

        var view = AgentContextProjection.Default.Build(null, thread.Rows, active: null, toolResults: shaping);

        var trimmed = view[2].Should().BeOfType<ToolCallResultMessage>().Subject;
        trimmed.Result.Length.Should().BeLessThanOrEqualTo(10_000);
        trimmed.Result.Should().StartWith("row 00000\n").And.EndWith("row 12399\n");
        trimmed.Result.Should().Contain("call-2").And.Contain("RecallConversation").And.Contain("offset=");
        trimmed.Result.Should().Contain("124000", "the marker names the full size");
        trimmed.ToolCallId.Should().Be("call-2");
        ((ToolCallResultMessage)thread.Rows[2].Message)
            .Result.Should()
            .BeSameAs(big, "the canonical row is never edited");
        view[4].Should().BeSameAs(thread.Rows[4].Message, "a result under the cap is dispatched as it is");
    }

    [Fact]
    public void ClearWatermark_ReplacesOlderToolResultsWithAPlaceholder_AndKeepsDeferredAndNewerOnes()
    {
        var output = new string('o', 2_000);
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn(result: output) // 2,3
            .ToolTurn(result: "pending", deferred: true) // 4,5
            .ToolTurn(result: output); // 6,7
        var shaping = new ToolResultViewOptions { ClearedThroughSeq = 5 };

        var view = AgentContextProjection.Default.Build(null, thread.Rows, active: null, toolResults: shaping);

        var cleared = view[2].Should().BeOfType<ToolCallResultMessage>().Subject;
        cleared.Result.Should().Contain("seq 3").And.Contain("2000").And.Contain("RecallConversation");
        cleared.Result.Length.Should().BeLessThan(300);
        view[1].Should().BeSameAs(thread.Rows[1].Message, "tool calls keep their arguments");
        view[4].Should().BeSameAs(thread.Rows[4].Message, "a deferred placeholder is never rewritten");
        view[6].Should().BeSameAs(thread.Rows[6].Message, "rows after the watermark are untouched");
    }

    [Fact]
    public void TightenedTrim_KeepsTheSameShareOfEachShownResult_AboveItsFloor_AndLeavesClearedAndNewerResultsAlone()
    {
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn(result: new string('a', 30_000)) // 2,3: cleared
            .ToolTurn(result: new string('b', 40_000)) // 4,5: shown at the 20,000-char cap, tightened to half
            .ToolTurn(result: new string('c', 12_000)) // 6,7: tightened to half, 6,000
            .ToolTurn(result: new string('d', 6_000)) //  8,9: half is under the 4,000 floor
            .ToolTurn(result: new string('e', 30_000)); // 10,11: newer than the tightening, normal cap
        var shaping = new ToolResultViewOptions
        {
            CapChars = 20_000,
            ClearedThroughSeq = 3,
            TightenedThroughSeq = 9,
            TightenedPartsPerMillion = 500_000,
            TightenedFloorChars = 4_000,
        };

        var view = AgentContextProjection.Default.Build(null, thread.Rows, active: null, toolResults: shaping);

        string Result(int index) => view[index].Should().BeOfType<ToolCallResultMessage>().Subject.Result;
        Result(2).Should().StartWith("[Tool result cleared", "clearing wins over tightening");
        Result(4).Length.Should().BeLessThanOrEqualTo(10_000).And.BeGreaterThan(9_000);
        Result(4).Should().Contain("call-4").And.Contain("offset=").And.StartWith("bbb").And.EndWith("bbb");
        Result(6).Length.Should().BeLessThanOrEqualTo(6_000).And.BeGreaterThan(5_000);
        Result(8).Length.Should().BeLessThanOrEqualTo(4_000).And.BeGreaterThan(3_000, "the floor holds");
        Result(10).Length.Should().BeLessThanOrEqualTo(20_000).And.BeGreaterThan(19_000, "newer results keep the cap");
        shaping.CapFor(seq: 5, length: 40_000).Should().Be(10_000);
        shaping.CapFor(seq: 9, length: 6_000).Should().Be(4_000);
        shaping.CapFor(seq: 11, length: 30_000).Should().Be(20_000);
    }

    [Fact]
    public void ClearedThroughSeq_KeepsTheMostRecentToolTurnsWhole()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(5).Assistant("done"); // results at 3,5,7,9,11

        ToolResultView.ClearedThroughSeq(thread.Rows, keepTurns: 3).Should().Be(5, "turns 3-5 start at seq 6");
        ToolResultView.ClearedThroughSeq(thread.Rows, keepTurns: 1).Should().Be(9);
        ToolResultView.ClearedThroughSeq(thread.Rows, keepTurns: 5).Should().Be(0, "nothing older to clear");
    }

    [Fact]
    public void ClearedThroughSeq_AnsweredOnly_StopsBeforeTheLatestHumanInput()
    {
        // 1 human, tool turns at 2-7, 8 assistant, 9 human, tool turns at 10-15: the first exchange was answered,
        // the second is in progress.
        var thread = new ThreadFixture().Human("first").ToolTurns(3).Assistant("done").Human("second").ToolTurns(3);

        ToolResultView
            .ClearedThroughSeq(thread.Rows, keepTurns: 1)
            .Should()
            .Be(13, "unscoped, only the latest turn stays");
        ToolResultView
            .ClearedThroughSeq(thread.Rows, keepTurns: 1, answeredOnly: true)
            .Should()
            .Be(8, "the exchange in progress is never cleared");
        ToolResultView
            .ClearedThroughSeq(thread.Rows, keepTurns: 5, answeredOnly: true)
            .Should()
            .Be(3, "keep-turns still applies below the cap");

        var single = new ThreadFixture().Human("go").ToolTurns(5);
        ToolResultView
            .ClearedThroughSeq(single.Rows, keepTurns: 1, answeredOnly: true)
            .Should()
            .Be(0, "one exchange in progress clears nothing");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReplayingTheStore_WithTrimmedAndClearedResults_YieldsByteIdenticalViews(string kind)
    {
        var store = _harness.Open(kind);
        var big = new string('b', 30_000);
        var thread = new ThreadFixture().Human("go").ToolTurn(result: big).ToolTurn(result: big).ToolTurn(result: big);
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(thread.Messages, Thread, "run-1")
        );
        var shaping = new ToolResultViewOptions { CapChars = 8_000, ClearedThroughSeq = 3 };

        var first = AgentContextProjection.Default.Build(
            "sys",
            SequencedHistory.FromPersisted(await store.LoadMessagesAsync(Thread)),
            active: null,
            toolResults: shaping
        );
        var second = AgentContextProjection.Default.Build(
            "sys",
            SequencedHistory.FromPersisted(await store.LoadMessagesAsync(Thread)),
            active: null,
            toolResults: shaping
        );

        Wire(second).Should().Equal(Wire(first));
        Wire(first)[3].Should().Contain("seq 3", "seq 3 is cleared");
        Wire(first)[5].Length.Should().BeLessThan(9_000, "seq 5 is trimmed");
        (await store.LoadMessagesAsync(Thread)).Should().OnlyContain(r => !r.MessageJson.Contains("elided"));
    }

    // ---- SequencedHistory ---------------------------------------------------------------------

    [Fact]
    public void FromSnapshot_NumbersRowsPositionally_WithNoIdAndNoRun()
    {
        var thread = new ThreadFixture().Human("go").ToolTurn();

        var rows = SequencedHistory.FromSnapshot(thread.Messages);

        rows.Select(r => r.Seq).Should().Equal(1, 2, 3);
        rows.Should().OnlyContain(r => r.MessageId == null && r.RunId == null);
        rows[0].EffectiveRunId.Should().Be("run-1", "the message itself carries a run id");
    }

    [Fact]
    public void FromPersisted_CarriesTheStoresSeqIdAndRun_AndSortsBySeq()
    {
        var rows = new[]
        {
            ConversationStoreHarness.Row(Thread, "b", 101, runId: "run-9") with
            {
                Seq = 2,
            },
            ConversationStoreHarness.Row(Thread, "a", 100) with
            {
                Seq = 1,
            },
        };

        var sequenced = SequencedHistory.FromPersisted(rows);

        sequenced.Select(r => (r.Seq, r.MessageId, r.RunId)).Should().Equal((1L, "a", "run-1"), (2L, "b", "run-9"));
        sequenced[0].Message.Should().BeOfType<TextMessage>().Which.Text.Should().Be("a");
    }

    [Fact]
    public void FromPersisted_LeavesOutRowsWithoutASeq_AndReportsUnreadableOnes()
    {
        var rows = new[]
        {
            ConversationStoreHarness.Row(Thread, "legacy", 99),
            ConversationStoreHarness.Row(Thread, "a", 100) with
            {
                Seq = 1,
            },
            ConversationStoreHarness.Row(Thread, "broken", 101, messageJson: "{not json") with
            {
                Seq = 2,
            },
            ConversationStoreHarness.Row(Thread, "c", 102) with
            {
                Seq = 3,
            },
        };
        var skipped = new List<string>();

        var sequenced = SequencedHistory.FromPersisted(rows, (row, _) => skipped.Add(row.Id));

        sequenced.Select(r => r.MessageId).Should().Equal("a", "c");
        skipped.Should().Equal("broken");
    }

    // ---- Replay determinism -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReplayingTheStore_YieldsTheSameView_Twice(string kind)
    {
        var store = _harness.Open(kind);
        var active = await SeedStoreAsync(store);

        var first = await BuildFromStoreAsync(store, active);
        var second = await BuildFromStoreAsync(store, active);

        Wire(second).Should().Equal(Wire(first));
        first.Should().HaveCount(1 + 1 + 3, "system, envelope, then the three rows after the boundary");
        first.Should().NotContain(m => m is CompactionCheckpointMessage);
    }

    [Theory]
    [MemberData(nameof(DurableKinds))]
    public async Task ReplayingTheStore_AfterARestart_YieldsTheSameView(string kind)
    {
        var store = _harness.Open(kind);
        var active = await SeedStoreAsync(store);
        var before = await BuildFromStoreAsync(store, active);

        var reopened = _harness.Reopen(kind);
        var after = await BuildFromStoreAsync(reopened, active);

        Wire(after).Should().Equal(Wire(before));
    }

    /// <summary>The view as it would go on the wire: record equality is reference equality for the collections inside a message.</summary>
    private static IReadOnlyList<string> Wire(IReadOnlyList<IMessage> view) =>
        [.. view.Select(m => MessagePersistenceConverter.ToPersistedMessage(m, Thread, "run-1").MessageJson)];

    /// <summary>Human, tool pair, checkpoint row (boundary 3), tool pair, human: seven rows.</summary>
    private static async Task<CompactionCheckpointMessage> SeedStoreAsync(IConversationStore store)
    {
        var thread = new ThreadFixture().Human("go").ToolTurn();
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(thread.Messages, Thread, "run-1")
        );
        var rows = await store.LoadMessagesAsync(Thread);
        var active = Checkpoint("cp-1", boundarySeq: 3, messageId: rows[2].Id);
        await store.AppendMessagesAsync(
            Thread,
            [MessagePersistenceConverter.ToPersistedMessage(active, Thread, "run-1")]
        );
        var tail = new ThreadFixture().ToolTurn().Human("and then");
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(tail.Messages, Thread, "run-1")
        );
        return active;
    }

    private static async Task<IReadOnlyList<IMessage>> BuildFromStoreAsync(
        IConversationStore store,
        CompactionCheckpointMessage active
    )
    {
        var rows = SequencedHistory.FromPersisted(await store.LoadMessagesAsync(Thread));
        return AgentContextProjection.Default.Build("sys", rows, active);
    }
}
