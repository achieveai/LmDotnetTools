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
            .Equal(thread.Rows.Where(r => r.Seq is 8 or 10 or 11 or 13 or 14).Select(r => r.Message));
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
        described.RowsInTail.Should().Be(3, "seq 8,10,11");
        described.EstimatedTokens.Should().Be(4 * ThreadFixture.TokensPerRow, "envelope plus three rows");

        var raw = projection.Describe(thread.Rows, active: null);
        raw.ActiveCheckpointId.Should().BeNull();
        raw.RowsHidden.Should().Be(0);
        raw.RowsInTail.Should().Be(9);
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
