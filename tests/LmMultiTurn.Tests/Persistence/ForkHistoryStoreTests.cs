using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins how every <see cref="IConversationStore"/> flavour reads a forked conversation: the shared
/// messages come from the conversation it was forked from, by reference and with their own ids; the
/// fork's own messages continue the sequence and the parent chain from the fork point.
/// </summary>
public sealed class ForkHistoryStoreTests : IAsyncLifetime
{
    private const string Source = "thread-src";
    private const string Fork = "thread-fork";
    private const string ForkOfFork = "thread-fork-2";
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    public static TheoryData<string> DurableKinds => ConversationStoreHarness.DurableKinds;

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Fork_ReadsTheSharedMessagesByReference_ThenItsOwn_AsOneChain(string kind)
    {
        var store = _harness.Open(kind);
        await AppendAsync(store, Source, "a", "b", "c", "d", "e");
        await MarkForkAsync(store, Fork, Source, "c", 3);

        // Before the fork writes anything, its history is exactly the shared part.
        (await store.LoadMessagesAsync(Fork))
            .Select(m => m.Id)
            .Should()
            .Equal("a", "b", "c");
        (await store.GetMessageWatermarkAsync(Fork)).Should().Be(3);

        await AppendAsync(store, Fork, "f1", "f2");

        var history = await store.LoadMessagesAsync(Fork);
        history.Select(m => m.Id).Should().Equal("a", "b", "c", "f1", "f2");
        history.Select(m => m.Seq).Should().Equal(1, 2, 3, 4, 5);
        history.Select(m => m.ParentMessageId).Should().Equal(null, "a", "b", "c", "f1");
        history.Take(3).Should().OnlyContain(m => m.ThreadId == Source, "shared rows are not copied into the fork");

        (await store.GetMessageWatermarkAsync(Fork)).Should().Be(5);
        (await store.LoadMessageRangeAsync(Fork, 2, 4, 10)).Select(m => m.Id).Should().Equal("b", "c", "f1");
        (await store.LoadMessageRangeAsync(Fork, 1, 5, 2)).Select(m => m.Id).Should().Equal("a", "b");

        (await store.LoadMessagesAsync(Source))
            .Select(m => m.Id)
            .Should()
            .Equal(["a", "b", "c", "d", "e"], "forking never changes the source");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ForkOfAFork_ChainsThroughBothForkPoints(string kind)
    {
        var store = _harness.Open(kind);
        await AppendAsync(store, Source, "a", "b", "c", "d");
        await MarkForkAsync(store, Fork, Source, "b", 2);
        await AppendAsync(store, Fork, "f1", "f2");
        await MarkForkAsync(store, ForkOfFork, Fork, "f1", 3);
        await AppendAsync(store, ForkOfFork, "g1");

        var history = await store.LoadMessagesAsync(ForkOfFork);

        history.Select(m => m.Id).Should().Equal("a", "b", "f1", "g1");
        history.Select(m => m.Seq).Should().Equal(1, 2, 3, 4);
        history.Last().ParentMessageId.Should().Be("f1");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Append_ChainsParents_AndIgnoresACallerSuppliedParent(string kind)
    {
        var store = _harness.Open(kind);
        await store.AppendMessagesAsync(
            Source,
            [ConversationStoreHarness.Row(Source, "a", 100) with { ParentMessageId = "bogus" }]
        );
        await AppendAsync(store, Source, "b", "c");

        (await store.LoadMessagesAsync(Source)).Select(m => m.ParentMessageId).Should().Equal(null, "a", "b");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReplaceMessage_KeepsTheParent_AndCannotReachASharedRowThroughTheFork(string kind)
    {
        var store = _harness.Open(kind);
        await AppendAsync(store, Source, "a", "b");
        await MarkForkAsync(store, Fork, Source, "b", 2);
        await AppendAsync(store, Fork, "f1");

        // The replacement is built by MessagePersistenceConverter, which knows nothing of parents.
        var own = (await store.LoadMessagesAsync(Fork)).Single(m => m.Id == "f1");
        await store.ReplaceMessageAsync(Fork, own with { ParentMessageId = null, MessageJson = own.MessageJson });
        (await store.LoadMessagesAsync(Fork)).Single(m => m.Id == "f1").ParentMessageId.Should().Be("b");

        var shared = (await store.LoadMessagesAsync(Fork)).Single(m => m.Id == "a");
        var replaceShared = () => store.ReplaceMessageAsync(Fork, shared);
        await replaceShared
            .Should()
            .ThrowAsync<InvalidOperationException>("a fork owns only its own rows; the shared ones stay the source's");
    }

    [Theory]
    [MemberData(nameof(DurableKinds))]
    public async Task ParentsAndForkLineage_SurviveARestart(string kind)
    {
        var store = _harness.Open(kind);
        await AppendAsync(store, Source, "a", "b");
        await MarkForkAsync(store, Fork, Source, "a", 1);
        await AppendAsync(store, Fork, "f1");

        var reopened = _harness.Reopen(kind);

        var history = await reopened.LoadMessagesAsync(Fork);
        history.Select(m => (m.Id, m.ParentMessageId)).Should().Equal(("a", null), ("f1", "a"));
    }

    private static Task AppendAsync(IConversationStore store, string threadId, params string[] ids) =>
        store.AppendMessagesAsync(
            threadId,
            [.. ids.Select((id, i) => ConversationStoreHarness.Row(threadId, id, 100, i))]
        );

    private static Task MarkForkAsync(
        IConversationStore store,
        string forkThreadId,
        string forkedThreadId,
        string messageId,
        long seq
    ) =>
        store.SaveMetadataAsync(
            forkThreadId,
            ConversationLineage.Write(
                new ThreadMetadata { ThreadId = forkThreadId, LastUpdated = 1 },
                new ConversationLineage
                {
                    ForkedFrom = new ForkPoint
                    {
                        ThreadId = forkedThreadId,
                        MessageId = messageId,
                        Seq = seq,
                    },
                    RootThreadId = Source,
                }
            )
        );
}
