namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Unit coverage for <see cref="NonOwningConversationStore"/> — the decorator that lets the sample hand
/// its single application-wide conversation store to spawned sub-agents WITHOUT letting a child dispose
/// it. Proves it (a) exposes neither disposal interface so <c>SubAgentManager</c>'s
/// <c>store is IAsyncDisposable</c> ownership checks skip it, and (b) forwards reads/writes across BOTH
/// <see cref="IConversationStore"/> and <see cref="IRunLedgerStore"/> to the wrapped instance.
/// </summary>
public sealed class NonOwningConversationStoreTests
{
    private const string ThreadId = "subagent-child-1";

    [Fact]
    public void NonOwningWrapper_ImplementsNeitherDisposalInterface()
    {
        var wrapper = new NonOwningConversationStore(new InMemoryConversationStore());

        ((object)wrapper is IAsyncDisposable).Should().BeFalse(
            "a child must never be able to dispose the shared store");
        ((object)wrapper is IDisposable).Should().BeFalse(
            "a child must never be able to dispose the shared store");
    }

    [Fact]
    public async Task NonOwningWrapper_ForwardsMessageWritesAndReads_ToUnderlyingStore()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = new NonOwningConversationStore(underlying);
        var message = MessagePersistenceConverter.ToPersistedMessage(
            new TextMessage { Text = "hi from child", Role = Role.Assistant },
            ThreadId,
            "run-1");

        // Write through the wrapper...
        await wrapper.AppendMessagesAsync(ThreadId, [message]);

        // ...and it must be visible on the UNDERLYING shared store (both directions forward).
        var fromUnderlying = await underlying.LoadMessagesAsync(ThreadId);
        fromUnderlying.Should().ContainSingle().Which.Id.Should().Be(message.Id);

        var fromWrapper = await wrapper.LoadMessagesAsync(ThreadId);
        fromWrapper.Should().ContainSingle().Which.Id.Should().Be(message.Id);
    }

    [Fact]
    public async Task NonOwningWrapper_ForwardsRunLedgerMembers_ToUnderlyingStore()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = new NonOwningConversationStore(underlying);
        var acceptedAt = DateTimeOffset.UtcNow;

        await wrapper.RecordAcceptedInputAsync(ThreadId, "input-1", acceptedAt);

        var fromUnderlying = await underlying.ListAcceptedInputIdsAsync(ThreadId);
        fromUnderlying.Should().Contain("input-1");

        var fromWrapper = await wrapper.ListAcceptedInputIdsAsync(ThreadId);
        fromWrapper.Should().Contain("input-1");
    }
}
