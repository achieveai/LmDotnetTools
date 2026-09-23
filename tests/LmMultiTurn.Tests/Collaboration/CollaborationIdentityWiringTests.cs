using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// The round trip that makes the feature real: one process writes its roster down, the next one reads
/// it and turns it into refusals.
/// </summary>
public class CollaborationIdentityWiringTests
{
    private const string RootId = "conv-a";

    private static AgentCollaborationSetup CreateRoot(string collaborationId = RootId) =>
        AgentCollaborationSetup.CreateRoot(
            new AgentCollaborationOptions(),
            collaborationId,
            agentId: "agent-root",
            name: "root"
        );

    [Fact]
    public void CreateRoot_WithNoNameSupplied_NamesTheRootMainAgent()
    {
        // Mutation that must go red: restoring the old "root" default.
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        setup.Name.Should().Be("MainAgent");
    }

    [Fact]
    public void CreateRoot_WithABlankName_FallsBackToMainAgent()
    {
        // A host that reads an empty config value used to get an ArgumentException here.
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions(), name: "   ");

        setup.Name.Should().Be("MainAgent");
    }

    [Fact]
    public void CreateRoot_WithAConfiguredName_UsesIt()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions(), name: "orchestrator");

        setup.Name.Should().Be("orchestrator");
    }

    [Fact]
    public void ASetupWhoseContextBelongsToAnotherCollaboration_IsRejected()
    {
        // The bundle carries the shared directory and ledger. Accepting a foreign context here would
        // file this agent's rows under a collaboration it is not part of, so the guard is load-bearing.
        var a = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions(), collaborationId: "collab-a");
        var b = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions(), collaborationId: "collab-b");

        var act = () => new AgentCollaborationSetup(a.Bundle, b.Context, "intruder");

        act.Should().Throw<ArgumentException>().WithMessage("*different collaboration*");
    }

    [Fact]
    public void ARootSetup_ExposesItsBundlesOptionsAndCanStillDelegate()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        setup.Options.Should().BeSameAs(setup.Bundle.Options);
        setup.CanDelegate.Should().BeTrue();
    }

    [Fact]
    public void AChildAtTheDelegationLimit_CanNoLongerDelegate()
    {
        var root = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions { MaxDelegationDepth = 1 });
        var childContext = root.Context.CreateChild("agent-1", AgentKind.SubAgent, "worker", "Does work.");

        root.ForChild(childContext, "worker").CanDelegate.Should().BeFalse();
    }

    /// <summary>Registers the root the way <c>MultiTurnAgentLoop</c>'s constructor does.</summary>
    private static AgentCollaborationSetup RegisterRoot(AgentCollaborationSetup setup)
    {
        setup.Directory.TryRegister(setup.Context, setup.Name, AgentCollaborationStatuses.Running);
        return setup;
    }

    private static Task SeedConversationAsync(IConversationStore store, string threadId) =>
        store.UpdateMetadataAsync(
            threadId,
            existing =>
                existing
                ?? new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = 0,
                    TenantId = "tenant-1",
                }
        );

    private static AgentCollaborationContext Spawn(AgentCollaborationSetup setup, string agentId, string name)
    {
        var child = setup.Context.CreateChild(agentId, AgentKind.SubAgent, "reviewer", "reviews diffs");
        setup.Directory.TryRegister(child, name, AgentCollaborationStatuses.Running).Succeeded.Should().BeTrue();
        return child;
    }

    [Fact]
    public async Task ASessionsAgentsAreResolvableAsNotLiveInTheNextSession()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootId);

        // Session one: spawn an agent, then shut down.
        var first = RegisterRoot(CreateRoot());
        await using (await CollaborationIdentityWiring.AttachAsync(first, store))
        {
            Spawn(first, "agent-1", "reviewer");
        }

        // Session two: a brand new bundle over the same conversation, as a process restart produces.
        var second = RegisterRoot(CreateRoot());
        await using var _ = await CollaborationIdentityWiring.AttachAsync(second, store);

        second.Directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);
        second.Directory.Resolve("agent-1").FailureCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);
        second.Directory.Resolve("root").Succeeded.Should().BeTrue("the live root must survive its own persisted row");
    }

    [Fact]
    public async Task ThirdSessionDoesNotInheritTheSecondSessionsGhosts()
    {
        // The property that stops the document growing forever: once a restart has reported an agent
        // gone, the re-capture drops it, so the NEXT restart reports nothing rather than the same
        // casualty a second time.
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootId);

        var first = RegisterRoot(CreateRoot());
        await using (await CollaborationIdentityWiring.AttachAsync(first, store))
        {
            Spawn(first, "agent-1", "reviewer");
        }

        var second = RegisterRoot(CreateRoot());
        await using (await CollaborationIdentityWiring.AttachAsync(second, store)) { }

        var persisted = await ConversationAgentBindingProjection.LoadAsync(store, RootId);
        persisted!.Agents.Select(a => a.AgentId).Should().Equal("agent-root");

        var third = RegisterRoot(CreateRoot());
        await using var _ = await CollaborationIdentityWiring.AttachAsync(third, store);
        third.Directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
    }

    [Fact]
    public async Task AReplacedSessionFlushingAfterItsReplacementAttached_CannotOverwriteTheNewRoster()
    {
        // The swap order in MultiTurnAgentPool.SwapAgentUnderLockAsync: the replacement loop is built
        // (and so attaches, reconciles, and writes) BEFORE the old entry is disposed, and disposing the
        // old entry is what flushes ITS roster — last. Monotonic-in-time alone lets that last write win,
        // because it IS the latest capture; it just describes a session that has already been replaced.
        // The next recreation then reads the old roster back and tombstones the same agents a second
        // time, which is the "not live 26" trace. Ordered by session, not by clock: a document written
        // by a later session is never overwritten by an earlier one.
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootId);

        var old = RegisterRoot(CreateRoot());
        var oldHandle = await CollaborationIdentityWiring.AttachAsync(old, store);
        Spawn(old, "agent-1", "reviewer");
        // The capture is written on the writers own drain task, so wait for the old sessions roster
        // to reach the store before the replacement reads it. Disposing the old handle is what usually
        // guarantees this, and not disposing it yet is the whole point of the test.
        await Wait.UntilAsync(
            async () =>
                (await ConversationAgentBindingProjection.LoadAsync(store, RootId))?.Agents.Any(a =>
                    a.AgentId == "agent-1"
                ) == true,
            "the old sessions roster reached the store"
        );

        // The replacement attaches while the old session is still alive, and its roster is on disk
        // before the old session is torn down.
        var replacement = RegisterRoot(CreateRoot());
        await (await CollaborationIdentityWiring.AttachAsync(replacement, store)).DisposeAsync();
        replacement.Directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.TargetNotLive);

        // Now the old entry is disposed: its manager retires what it owned, and its handle flushes.
        _ = old.Bundle.RetireAgent("agent-1", AgentCollaborationStatuses.Stopped);
        await oldHandle.DisposeAsync();

        var persisted = await ConversationAgentBindingProjection.LoadAsync(store, RootId);
        persisted!
            .Agents.Select(a => a.AgentId)
            .Should()
            .Equal(
                ["agent-root"],
                "the replacement's roster is the current one, whatever the old session wrote after it"
            );

        var next = RegisterRoot(CreateRoot());
        await using var nextHandle = await CollaborationIdentityWiring.AttachAsync(next, store);
        next.Directory.Resolve("reviewer")
            .FailureCode.Should()
            .Be(AgentDirectoryFailureCodes.NotFound, "an agent reported gone once must not be reported gone again");
    }

    [Fact]
    public async Task DisposeWaitsForTheOutstandingWriteAndMakesItDurable()
    {
        // The write is held open inside the store rather than left to race the assertion. Asserting only
        // on the persisted content after Dispose returns is a detector the fast path usually beats: an
        // unflushed write still lands, just late, so removing the flush reddens such a test only
        // sometimes. Parking the write makes the claim itself — Dispose does not return while a capture
        // is outstanding — the thing under test, and it can only be observed one way.
        var inner = new InMemoryConversationStore();
        await SeedConversationAsync(inner, RootId);
        var store = new GatedMetadataStore(inner);
        var setup = RegisterRoot(CreateRoot());

        var handle = await CollaborationIdentityWiring.AttachAsync(setup, store);
        Spawn(setup, "agent-1", "reviewer");

        var dispose = handle.DisposeAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        dispose
            .IsCompleted.Should()
            .BeFalse("the flush must await the parked write, so Dispose cannot return before it is released");

        store.ReleaseWrites();
        await dispose;

        // And what it waited for is the LATEST roster: the capture runs inside the write, so the spawn
        // that happened after the attach-time schedule is in the document the flush made durable.
        var persisted = await ConversationAgentBindingProjection.LoadAsync(inner, RootId);
        persisted!.Agents.Select(a => a.AgentId).Should().Equal("agent-1", "agent-root");
    }

    [Fact]
    public async Task DisposeStopsCapturing()
    {
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootId);
        var setup = RegisterRoot(CreateRoot());

        await (await CollaborationIdentityWiring.AttachAsync(setup, store)).DisposeAsync();
        Spawn(setup, "agent-1", "reviewer");

        var persisted = await ConversationAgentBindingProjection.LoadAsync(store, RootId);
        persisted!.Agents.Select(a => a.AgentId).Should().Equal("agent-root");
    }

    [Fact]
    public async Task AttachingToAConversationWithNoMetadataRowPersistsNothingAndStillReconciles()
    {
        // A conversation whose row does not exist yet is the ordinary first-turn case. The projection
        // refuses to mint one — an unstamped row is unreadable by everyone — so attach must be a no-op
        // on the write side without failing the startup that called it.
        var store = new InMemoryConversationStore();
        var setup = RegisterRoot(CreateRoot());

        await using var handle = await CollaborationIdentityWiring.AttachAsync(setup, store);

        (await store.LoadMetadataAsync(RootId)).Should().BeNull();
    }

    [Fact]
    public async Task TwoRootsInOneStoreKeepTheirOwnAgentOne()
    {
        // Since #705 both conversations really do mint an `agent-1`. Each session two must resolve its
        // own: the store is shared, and only the collaboration id separates the two documents.
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, "conv-a");
        await SeedConversationAsync(store, "conv-b");

        var firstA = RegisterRoot(CreateRoot("conv-a"));
        await using (await CollaborationIdentityWiring.AttachAsync(firstA, store))
        {
            Spawn(firstA, "agent-1", "reviewer");
        }

        var liveB = RegisterRoot(CreateRoot("conv-b"));
        Spawn(liveB, "agent-1", "writer");
        await using var _ = await CollaborationIdentityWiring.AttachAsync(liveB, store);

        liveB.Directory.Resolve("agent-1").Succeeded.Should().BeTrue("conv-b's agent-1 is live and is not conv-a's");
        liveB.Directory.Resolve("writer").Succeeded.Should().BeTrue();
        liveB.Directory.Resolve("reviewer").FailureCode.Should().Be(AgentDirectoryFailureCodes.NotFound);
    }

    /// <summary>
    /// Parks every metadata WRITE until the test releases it; reads and everything else pass straight
    /// through. Seed through the inner store, not through this one, or the seed parks too.
    /// </summary>
    private sealed class GatedMetadataStore(IConversationStore inner) : IConversationStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseWrites() => _gate.TrySetResult();

        public async Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        )
        {
            await _gate.Task;
            await inner.UpdateMetadataAsync(threadId, update, ct);
        }

        public async Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
        {
            await _gate.Task;
            await inner.SaveMetadataAsync(threadId, metadata, ct);
        }

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            inner.LoadMetadataAsync(threadId, ct);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => inner.ListThreadsAsync(limit, offset, options, ct);
    }
}
