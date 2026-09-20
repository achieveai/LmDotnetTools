using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// How a restored row learns its <c>Seq</c>, and what the view does with a row that never learns one.
/// <para>
/// In-memory rows carry no sequence number: the runtime pairs the rows a recovering loop restored against
/// the rows the store holds, in order, and remembers the pairing. Everything the compaction boundary does
/// is expressed in those numbers, so a row the pairing cannot place is a row the boundary cannot reach.
/// </para>
/// <para>
/// Both tests below share one arrangement — a history holding a row that was never persisted — because
/// that single row is what exercised both halves of the defect. The walk spent its cursor searching for
/// the unpersisted row and so failed to identify everything after it, and an unidentified row was then
/// numbered "newer than any boundary", which put the whole remainder of the thread out of the cut's
/// reach. Sending a tool result whose call the cut had taken is how that reached a provider, as an
/// unrecoverable 400 that repeated on every retry.
/// </para>
/// </summary>
public class CompactionRowIdentityTests
{
    private const string Thread = "thread-row-identity";
    private const string CheckpointId = "cp-identity";

    /// <summary>
    /// The cursor half. A row the store cannot account for must cost only itself: the rows after it are
    /// still in store order and still findable, so they must still be identified — and therefore still be
    /// cuttable. "two" sits below the boundary and belongs to the summary, not to the tail.
    /// </summary>
    [Fact]
    public async Task ARowTheStoreNeverSaw_DoesNotStrandTheRowsAfterIt()
    {
        var view = await ViewAfterRestoringAsync(boundarySeq: 2);

        view.Should().NotContain(m => TextOf(m) == "two", "seq 2 is at the boundary, so the cut covers it");
        view.Select(TextOf).Should().Contain(["three", "four"], "the rows above the boundary are the tail");
    }

    /// <summary>
    /// The sentinel half. "Unknown seq" is only ever evidence of "appended after the last reconciliation"
    /// for rows at the very end of a history. For a row with identified rows after it the walk simply
    /// failed to place it, and calling that newer than every boundary is how it outlived a cut that took
    /// its neighbours on both sides.
    /// </summary>
    [Fact]
    public async Task AnUnidentifiedRowAmongIdentifiedOnes_IsCutWithItsNeighbours_NotKeptAsTheNewestRow()
    {
        var view = await ViewAfterRestoringAsync(boundarySeq: 2);

        view.Should()
            .NotContain(
                m => TextOf(m) == "never-persisted",
                "it sits between two rows the cut took, so it is older than the boundary whatever its seq says"
            );
    }

    /// <summary>
    /// A history whose middle row was never persisted, restored against a store that holds every other row,
    /// with a checkpoint active at <paramref name="boundarySeq"/>. Returns the view the model would be sent.
    /// </summary>
    private static async Task<IReadOnlyList<IMessage>> ViewAfterRestoringAsync(long boundarySeq)
    {
        var store = new InMemoryConversationStore();
        var checkpoint = new CompactionCheckpointMessage
        {
            CheckpointId = CheckpointId,
            Boundary = new CheckpointBoundary { Seq = boundarySeq, MessageId = "m" + boundarySeq },
            Trigger = CompactionTrigger.Manual,
            Manifest = new ContextManifest(),
            Narrative = "Earlier context.",
        };

        // The store's rows, and only these: "never-persisted" is deliberately absent, which is the whole
        // arrangement. Seqs come out 1..4 in this order, and the checkpoint row lands at 5 below.
        IMessage[] persisted = [Text("one"), Text("two"), Text("three"), Text("four")];
        await Append(persisted);

        // Driven through the real transitions rather than by poking state: adoption reads the committed
        // projection, and activation insists the checkpoint row be the very next row after the watermark
        // it prepared against — a half-written state would make the test pass for the wrong reason.
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Thread,
            CheckpointId,
            boundarySeq,
            watermarkAtPrepare: persisted.Length,
            CompactionTrigger.Manual
        );
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Thread, CheckpointId);
        _ = await CompactionStateProjection.TryCommitAsync(store, Thread, CheckpointId);
        await Append([checkpoint]);
        _ = await CompactionStateProjection.ActivateAsync(store, Thread, CheckpointId, persisted.Length + 1);

        Task Append(IReadOnlyList<IMessage> rows) =>
            store.AppendMessagesAsync(
                Thread,
                [.. rows.Select(m => MessagePersistenceConverter.ToPersistedMessage(m, Thread, "run-1"))]
            );

        IReadOnlyList<IMessage> history =
        [
            persisted[0],
            Text("never-persisted"),
            persisted[1],
            persisted[2],
            persisted[3],
            checkpoint,
        ];

        var runtime = new CompactionRuntime(
            new CompactionSetup { Options = new CompactionOptions { Mode = CompactionMode.Compact } },
            new CompactionRuntimeHost
            {
                ThreadId = Thread,
                Store = store,
                DefaultOptions = new GenerateReplyOptions { ModelId = "test-model" },
                HistorySnapshot = () => history,
                AppendInMemory = _ => { },
            },
            new SilentAgent()
        );

        await runtime.TrackRestoredAsync(history, CancellationToken.None);
        runtime.Active.Should().NotBeNull("the arrangement is meaningless without a cut to survive");

        return runtime.BuildView() ?? throw new InvalidOperationException("an active checkpoint always builds a view");
    }

    private static TextMessage Text(string text) => new() { Text = text, Role = Role.User };

    private static string? TextOf(IMessage message) => (message as TextMessage)?.Text;

    /// <summary>The runtime needs a provider agent to summarize with; nothing here ever asks it to.</summary>
    private sealed class SilentAgent : IAgent
    {
        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("no summary pass runs in these tests");
    }
}
