using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// Covers WI #195's transactional publication of a conversation's sandbox-established binding
/// (<see cref="SandboxEstablishedBinding"/>) through <see cref="MultiTurnAgentPool"/>: the pool publishes a
/// staged binding ONLY as part of a successful agent-entry commit under the per-thread lock, and clears it
/// on removal (even if agent disposal fails). A non-workspace creation (no staged binding) publishes
/// nothing; a failed construction publishes nothing.
/// </summary>
[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolBindingTests
{
    private static readonly AgentProfile Mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    private static SandboxEstablishedBinding Binding(string appId = "owner") =>
        new(new WorkspaceRef("default"), new SandboxCredential(appId, "key"));

    private sealed class RecordingBindingSink : ISandboxBindingSink
    {
        public List<(string ThreadId, SandboxEstablishedBinding Binding)> Published { get; } = [];
        public List<string> Cleared { get; } = [];

        public void PublishEstablishedBinding(string threadId, SandboxEstablishedBinding binding) =>
            Published.Add((threadId, binding));

        public void ClearEstablishedBinding(string threadId) => Cleared.Add(threadId);
    }

    [Fact]
    public async Task WorkspaceModeCommit_PublishesStagedBinding()
    {
        var sink = new RecordingBindingSink();
        var binding = Binding();
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)) { StagedBinding = binding },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        );

        _ = pool.GetOrCreateAgent("thread-ws", Mode);

        sink.Published.Should().ContainSingle();
        sink.Published[0].ThreadId.Should().Be("thread-ws");
        sink.Published[0].Binding.Should().Be(binding);
        sink.Cleared.Should().BeEmpty();
    }

    [Fact]
    public async Task NonWorkspaceCommit_PublishesNothing()
    {
        var sink = new RecordingBindingSink();
        await using var pool = new MultiTurnAgentPool(
            // No StagedBinding: a non-workspace agent must publish nothing.
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        );

        _ = pool.GetOrCreateAgent("thread-plain", Mode);

        sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedInitialConstruction_PublishesNothing()
    {
        var sink = new RecordingBindingSink();
        await using var pool = new MultiTurnAgentPool(
            MultiTurnAgentPool.AgentCreationResult (MultiTurnAgentPool.AgentCreationContext _) =>
                throw new InvalidOperationException("factory boom"),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        );

        var act = () => pool.GetOrCreateAgent("thread-fail", Mode);

        act.Should().Throw<InvalidOperationException>();
        // The factory threw before the commit line, so nothing was published.
        sink.Published.Should().BeEmpty();
        sink.Cleared.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAgent_ClearsBinding()
    {
        var sink = new RecordingBindingSink();
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)) { StagedBinding = Binding() },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        );

        _ = pool.GetOrCreateAgent("thread-remove", Mode);
        await pool.RemoveAgentAsync("thread-remove");

        sink.Cleared.Should().ContainSingle().Which.Should().Be("thread-remove");
    }

    [Fact]
    public async Task RemoveAgent_ClearsBinding_EvenWhenDisposeThrows()
    {
        var sink = new RecordingBindingSink();
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId) { ThrowOnDispose = true })
            {
                StagedBinding = Binding(),
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        );

        _ = pool.GetOrCreateAgent("thread-dispose-throws", Mode);

        // Disposal throws, but the binding MUST still be cleared (the finally path).
        var act = () => pool.RemoveAgentAsync("thread-dispose-throws").AsTask();
        await act.Should().ThrowAsync<Exception>();
        sink.Cleared.Should().ContainSingle().Which.Should().Be("thread-dispose-throws");
    }

    [Fact]
    public async Task RemoveAgent_ClearsOnlyThatThread_NotOthersSharingASession()
    {
        var sink = new RecordingBindingSink();
        var shared = Binding();
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)) { StagedBinding = shared },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        );

        // Two conversations backed by the SAME (workspaceId, appId) session/binding.
        _ = pool.GetOrCreateAgent("thread-a", Mode);
        _ = pool.GetOrCreateAgent("thread-b", Mode);

        await pool.RemoveAgentAsync("thread-a");

        // Only thread-a's binding is cleared; thread-b keeps its agent (the shared session is untouched).
        sink.Cleared.Should().ContainSingle().Which.Should().Be("thread-a");
        pool.HasAgent("thread-b").Should().BeTrue();
    }

    [Fact]
    public async Task ModeSwitch_StagingNewBinding_Republishes()
    {
        var sink = new RecordingBindingSink();
        var owner = new SandboxCredential("owner", "key");
        var first = new SandboxEstablishedBinding(new WorkspaceRef("default"), owner);
        var second = new SandboxEstablishedBinding(new WorkspaceRef("default"), owner);
        var call = 0;
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                var staged = Interlocked.Increment(ref call) == 1 ? first : second;
                return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)) { StagedBinding = staged };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink
        );

        _ = pool.GetOrCreateAgent(
            "thread-switch",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: null,
            callerCredential: owner
        );
        var newMode = SystemChatModes.All[0];
        _ = await pool.RecreateAgentWithModeAsync("thread-switch", newMode, owner);

        // Both the initial commit and the swap commit published, in order.
        sink.Published.Should().HaveCount(2);
        sink.Published[0].Binding.Should().BeSameAs(first);
        sink.Published[1].Binding.Should().BeSameAs(second);
    }
}
