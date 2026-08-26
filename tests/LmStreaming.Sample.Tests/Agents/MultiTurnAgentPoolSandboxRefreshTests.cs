using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolSandboxRefreshTests
{
    private static readonly AgentProfile Mode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId)!;

    [Fact]
    public async Task EnsureCurrentAgentAsync_RebuildsIdleAgentWhenSandboxSessionWasReplaced()
    {
        var credential = new SandboxCredential("owner", "key");
        var sessionId = "sess-1";
        var created = new List<(FakeMultiTurnAgent Agent, MultiTurnAgentPool.AgentCreationContext Context)>();
        var sink = new RecordingBindingSink();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                var agent = new FakeMultiTurnAgent(context.ThreadId);
                created.Add((agent, context));
                return new MultiTurnAgentPool.AgentCreationResult(agent)
                {
                    StagedBinding = new SandboxEstablishedBinding(
                        new WorkspaceRef("workspace-1"),
                        credential,
                        credential,
                        sessionId),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: sink,
            liveSessionResolver: (_, _) =>
                Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace"))
        );

        var original = pool.GetOrCreateAgent(
            "thread-1",
            Mode,
            requestedProviderId: "provider-1",
            requestResponseDumpFileName: "dump-1",
            requestedWorkspaceId: "workspace-1",
            callerCredential: credential);
        sessionId = "sess-2";

        var result = await pool.EnsureCurrentAgentAsync("thread-1", credential);

        result.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.Replaced);
        result.Agent.Should().NotBeSameAs(original);
        created.Should().HaveCount(2);
        created[1].Context.Mode.Should().BeSameAs(Mode);
        created[1].Context.ProviderId.Should().Be("provider-1");
        created[1].Context.WorkspaceId.Should().Be("workspace-1");
        created[1].Context.DumpFile.Should().Be("dump-1");
        created[1].Context.CallerCredential.Should().Be(credential);
        sink.Published.Should().HaveCount(2);
        sink.Published[^1].Binding.SessionId.Should().Be("sess-2");
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_CanReportRefreshBeforeReplacingOpenSocketAgent()
    {
        var credential = new SandboxCredential("owner", "key");
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = CreatePool(
            credential,
            () => "sess-1",
            (_, _) => Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace")),
            created);

        var original = pool.GetOrCreateAgent(
            "thread-socket",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: credential);

        var result = await pool.EnsureCurrentAgentAsync("thread-socket", credential, replace: false);

        result.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.RefreshRequired);
        result.Agent.Should().BeSameAs(original);
        created.Should().ContainSingle("the old socket must close before its captured agent is replaced");
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_DoesNotInterruptActiveRun_AndRefreshesWhenIdle()
    {
        var credential = new SandboxCredential("owner", "key");
        var sessionId = "sess-1";
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = CreatePool(
            credential,
            () => sessionId,
            (_, _) => Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace")),
            created);

        var original = (FakeMultiTurnAgent)pool.GetOrCreateAgent(
            "thread-active",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: credential);
        original.CurrentRunId = "run-1";
        original.IsRunning = true;
        sessionId = "sess-2";

        var whileActive = await pool.EnsureCurrentAgentAsync("thread-active", credential);

        whileActive.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred);
        whileActive.Agent.Should().BeSameAs(original);
        created.Should().ContainSingle();

        original.CurrentRunId = null;
        original.IsRunning = false;
        var onceIdle = await pool.EnsureCurrentAgentAsync("thread-active", credential);

        onceIdle.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.Replaced);
        onceIdle.Agent.Should().NotBeSameAs(original);
        created.Should().HaveCount(2);
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_LeavesOriginalAgentCommittedWhenRebuildFails()
    {
        var credential = new SandboxCredential("owner", "key");
        var calls = 0;
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                {
                    throw new InvalidOperationException("replacement failed");
                }

                var agent = new FakeMultiTurnAgent(context.ThreadId);
                created.Add(agent);
                return new MultiTurnAgentPool.AgentCreationResult(agent)
                {
                    StagedBinding = new SandboxEstablishedBinding(
                        new WorkspaceRef("workspace-1"),
                        credential,
                        credential,
                        "sess-1"),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: new RecordingBindingSink(),
            liveSessionResolver: (_, _) =>
                Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace")));

        var original = pool.GetOrCreateAgent(
            "thread-failed-refresh",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: credential);

        Func<Task> act = () => pool.EnsureCurrentAgentAsync("thread-failed-refresh", credential);

        await act.Should().ThrowAsync<InvalidOperationException>();
        pool.TryGet("thread-failed-refresh", out var stillCurrent).Should().BeTrue();
        stillCurrent.Should().BeSameAs(original);
        created.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_DoesNotResolveNonSandboxAgent()
    {
        var resolverCalls = 0;
        await using var pool = new MultiTurnAgentPool(
            context => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId)),
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            liveSessionResolver: (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace"));
            });

        var original = pool.GetOrCreateAgent("thread-plain", SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var result = await pool.EnsureCurrentAgentAsync("thread-plain");

        result.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.Current);
        result.Agent.Should().BeSameAs(original);
        resolverCalls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_RejectsForeignCallerBeforeResolvingSession()
    {
        var owner = new SandboxCredential("owner", "owner-key");
        var foreign = new SandboxCredential("foreign", "foreign-key");
        var resolverCalls = 0;
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = CreatePool(
            owner,
            () => "sess-1",
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace"));
            },
            created);

        _ = pool.GetOrCreateAgent(
            "thread-foreign",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: owner);

        Func<Task> act = () => pool.EnsureCurrentAgentAsync("thread-foreign", foreign);

        await act.Should().ThrowAsync<SandboxCredentialConflictException>();
        resolverCalls.Should().Be(0);
        created.Should().ContainSingle();
    }

    /// <summary>
    /// #398: the sandbox-session refresh rebuilds the entry and must carry the FROZEN principal onto
    /// the replacement, exactly as it already carries the frozen credential. Asserted from the far
    /// side of the refresh - a pre-refresh conflict proves nothing here, because the entry the guard
    /// reads after a refresh is a different object from the one creation froze.
    /// </summary>
    [Fact]
    public async Task EnsureCurrentAgentAsync_KeepsTheFrozenPrincipalOnTheReplacementEntry()
    {
        var owner = new SandboxCredential("owner", "key");
        var sessionId = "sess-1";
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                var agent = new FakeMultiTurnAgent(context.ThreadId);
                created.Add(agent);
                return new MultiTurnAgentPool.AgentCreationResult(agent)
                {
                    StagedBinding = new SandboxEstablishedBinding(
                        new WorkspaceRef("workspace-1"),
                        owner,
                        owner,
                        sessionId),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: new RecordingBindingSink(),
            liveSessionResolver: (_, _) =>
                Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace"))
        );

        _ = pool.GetOrCreateAgent(
            "thread-frozen",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: owner,
            ownerUserId: "dir-a:alice");
        sessionId = "sess-2";

        var refreshed = await pool.EnsureCurrentAgentAsync("thread-frozen", owner, ownerUserId: "dir-a:alice");

        refreshed.Status.Should().Be(
            MultiTurnAgentPool.AgentRefreshStatus.Replaced,
            "the assertions below are about the REPLACEMENT entry, not the original");
        created.Should().HaveCount(2);

        Func<Task> bobsTurn = () => pool.EnsureCurrentAgentAsync(
            "thread-frozen",
            owner,
            ownerUserId: "dir-b:bob");

        var conflict = await bobsTurn.Should().ThrowAsync<PrincipalConflictException>();
        conflict.Which.ExistingUserId.Should().Be("dir-a:alice");
        conflict.Which.RequestedUserId.Should().Be("dir-b:bob");

        // Companion assertion: fixing the principal must not quietly drop the credential the same
        // call already carried correctly.
        Func<Task> foreignCaller = () => pool.EnsureCurrentAgentAsync(
            "thread-frozen",
            new SandboxCredential("intruder", "key"),
            ownerUserId: "dir-a:alice");

        _ = await foreignCaller.Should().ThrowAsync<SandboxCredentialConflictException>();
    }

    /// <summary>
    /// #398, the other half: the refresh carries the frozen principal FORWARD and never adopts the
    /// refreshing caller's. An unowned entry stays unowned, so triggering a sandbox-session refresh
    /// is not a way to claim a thread nobody has claimed.
    /// </summary>
    [Fact]
    public async Task EnsureCurrentAgentAsync_DoesNotLetTheRefreshingCallerClaimAnUnownedEntry()
    {
        var owner = new SandboxCredential("owner", "key");
        var sessionId = "sess-1";
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                var agent = new FakeMultiTurnAgent(context.ThreadId);
                created.Add(agent);
                return new MultiTurnAgentPool.AgentCreationResult(agent)
                {
                    StagedBinding = new SandboxEstablishedBinding(
                        new WorkspaceRef("workspace-1"),
                        owner,
                        owner,
                        sessionId),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: new RecordingBindingSink(),
            liveSessionResolver: (_, _) =>
                Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace"))
        );

        _ = pool.GetOrCreateAgent(
            "thread-unowned",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: owner,
            ownerUserId: null);
        sessionId = "sess-2";

        var refreshed = await pool.EnsureCurrentAgentAsync(
            "thread-unowned",
            owner,
            ownerUserId: "dir-b:bob");

        refreshed.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.Replaced);

        Func<Task> carolsTurn = () => pool.EnsureCurrentAgentAsync(
            "thread-unowned",
            owner,
            ownerUserId: "dir-c:carol");

        await carolsTurn.Should().NotThrowAsync(
            "the refresh must carry the entry's own (absent) principal forward, not adopt Bob's");
    }

    private static MultiTurnAgentPool CreatePool(
        SandboxCredential credential,
        Func<string> sessionId,
        Func<SandboxEstablishedBinding, CancellationToken, Task<SandboxSession>> resolver,
        List<FakeMultiTurnAgent> created)
    {
        return new MultiTurnAgentPool(
            context =>
            {
                var agent = new FakeMultiTurnAgent(context.ThreadId);
                created.Add(agent);
                return new MultiTurnAgentPool.AgentCreationResult(agent)
                {
                    StagedBinding = new SandboxEstablishedBinding(
                        new WorkspaceRef("workspace-1"),
                        credential,
                        credential,
                        sessionId()),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: new RecordingBindingSink(),
            liveSessionResolver: resolver);
    }

    private sealed class RecordingBindingSink : ISandboxBindingSink
    {
        public List<(string ThreadId, SandboxEstablishedBinding Binding)> Published { get; } = [];

        public void PublishEstablishedBinding(string threadId, SandboxEstablishedBinding binding) =>
            Published.Add((threadId, binding));

        public void ClearEstablishedBinding(string threadId) { }
    }
}
