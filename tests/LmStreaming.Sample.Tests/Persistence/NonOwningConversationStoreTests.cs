using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

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

    private static NonOwningConversationStore WithProvenance(
        IConversationStore underlying,
        string childThreadId,
        string parentThreadId) =>
        new(
            underlying,
            childThreadId,
            () => SubAgentProvenance.Build(
                parentThreadId,
                new SubAgentSnapshot(
                    AgentId: "child-1",
                    Name: "alpha",
                    TemplateName: "code-reviewer:security",
                    Task: "check auth",
                    Status: SubAgentStatus.Running,
                    ThreadId: childThreadId,
                    LastActivityUtc: null)));

    [Fact]
    public async Task NonOwningWrapper_StampsProvenance_OnBothMetadataWritePaths()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = WithProvenance(underlying, ThreadId, "thread-parent");

        await wrapper.SaveMetadataAsync(
            ThreadId,
            new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 });

        var saved = await underlying.LoadMetadataAsync(ThreadId);
        SubAgentProvenance.TryProject(saved!, "thread-parent")!.Template
            .Should().Be("code-reviewer:security");

        // UpdateMetadataAsync is the path MultiTurnAgentBase actually takes after each run, and it must
        // preserve properties the caller set as well as add the stamp.
        await wrapper.UpdateMetadataAsync(
            ThreadId,
            existing => (existing ?? new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 1 }) with
            {
                LastUpdated = 2,
                Properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
                    .SetItem("usage.records", "kept"),
            });

        var updated = await underlying.LoadMetadataAsync(ThreadId);
        updated!.Properties!["usage.records"].Should().Be("kept");
        SubAgentProvenance.TryProject(updated, "thread-parent")!.Name.Should().Be("alpha");
    }

    /// <summary>
    /// A child's store is also written to on behalf of OTHER threads — the conversation usage
    /// projection persists under the ROOT conversation id. Those writes must not be stamped, or the
    /// root would list itself as its own child.
    /// </summary>
    [Fact]
    public async Task NonOwningWrapper_DoesNotStamp_WritesForOtherThreads()
    {
        var underlying = new InMemoryConversationStore();
        var wrapper = WithProvenance(underlying, ThreadId, "thread-parent");

        await wrapper.SaveMetadataAsync(
            "thread-root",
            new ThreadMetadata { ThreadId = "thread-root", LastUpdated = 1 });

        var saved = await underlying.LoadMetadataAsync("thread-root");
        (saved!.Properties?.ContainsKey(SubAgentProvenance.ParentThreadIdKey) ?? false)
            .Should().BeFalse();
    }
}
