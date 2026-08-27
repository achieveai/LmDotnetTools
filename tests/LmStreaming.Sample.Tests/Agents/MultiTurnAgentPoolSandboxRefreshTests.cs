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
                        sessionId
                    ),
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
            callerCredential: credential
        );
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
            created
        );

        var original = pool.GetOrCreateAgent(
            "thread-socket",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: credential
        );

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
            created
        );

        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-active",
                Mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: "workspace-1",
                callerCredential: credential
            );
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
    public async Task EnsureCurrentAgentAsync_DoesNotDiscardInputAcknowledgedBeforeARunIdExists()
    {
        // The race this pins down, in the order the two threads actually run it:
        //   1. a REST/WS send reaches the pooled agent and is ACKNOWLEDGED — the caller now holds a
        //      receipt (and, with a run ledger, a durable accepted-input row);
        //   2. the agent's run loop has not woken yet, so CurrentRunId is still null;
        //   3. a second connection arrives, the sandbox session has been replaced, and the refresh
        //      path samples the entry. Every signal it reads says "idle".
        // Replacing the entry there disposes the agent and its input channel, and the acknowledged
        // input is gone with no error on any path. The fixture stops the world at step 2, which is
        // what makes this deterministic rather than a timing race.
        var credential = new SandboxCredential("owner", "key");
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = CreatePool(
            credential,
            () => "sess-1",
            (_, _) => Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace")),
            created
        );

        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-accepted-input",
                Mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: "workspace-1",
                callerCredential: credential
            );

        var receipt = await original.SendAsync(
            [new TextMessage { Role = Role.User, Text = "please review this" }],
            inputId: "input-1"
        );

        // Precondition, asserted rather than assumed: we really are inside the window. If the
        // fixture ever stopped modelling it, the act below would prove nothing.
        original.CurrentRunId.Should().BeNull("the run loop has not assigned a run to the input yet");
        original.HasUnassignedInput.Should().BeTrue("the input was acknowledged but no run owns it");

        var result = await pool.EnsureCurrentAgentAsync("thread-accepted-input", credential);

        // Disposal first, because it is the loss itself: the pool disposes a replaced entry, and
        // disposing the agent completes its input channel. Reading the status first would report the
        // symptom before the damage.
        original
            .Disposed.Should()
            .BeFalse(
                "disposing the agent completes its input channel and drops an input this host already acknowledged"
            );
        original
            .UnassignedReceiptIds.Should()
            .Contain(receipt.ReceiptId, "the acknowledged input must still be there for the run loop to pick up");
        result
            .Status.Should()
            .Be(
                MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred,
                "an entry holding acknowledged input is busy, even though no run id exists yet"
            );
        result.Agent.Should().BeSameAs(original);
        created.Should().ContainSingle("no replacement may be built while acknowledged input is unassigned");
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_RefreshesOnceTheAcknowledgedInputHasBeenRunAndCompleted()
    {
        // The negative control for the test above. Without it, "defer" could be coming from
        // anything at all — including a fix that simply never refreshes a sandbox-backed agent.
        // Same pool, same stale session, same agent: the ONLY thing that changes is that the
        // acknowledged input gets taken up by a run and that run finishes.
        var credential = new SandboxCredential("owner", "key");
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = CreatePool(
            credential,
            () => "sess-1",
            (_, _) => Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace")),
            created
        );

        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-input-consumed",
                Mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: "workspace-1",
                callerCredential: credential
            );

        _ = await original.SendAsync(
            [new TextMessage { Role = Role.User, Text = "please review this" }],
            inputId: "input-1"
        );

        var deferred = await pool.EnsureCurrentAgentAsync("thread-input-consumed", credential);
        deferred.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred);

        // The loop drains the input, names a run for it, and the run finishes.
        original.AssignRun("run-1");
        original.HasUnassignedInput.Should().BeFalse("the run now owns the input");
        original.CurrentRunId = null;

        var refreshed = await pool.EnsureCurrentAgentAsync("thread-input-consumed", credential);

        refreshed
            .Status.Should()
            .Be(
                MultiTurnAgentPool.AgentRefreshStatus.Replaced,
                "the deferral must be released once nothing acknowledged is at risk"
            );
        refreshed.Agent.Should().NotBeSameAs(original);
        created.Should().HaveCount(2);
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_ReportsDeferredNotRequired_WhenAcknowledgedInputIsUnassigned()
    {
        // The probe the WebSocket message path uses (replace: false). RefreshRequired makes that
        // caller CLOSE the socket; RefreshDeferred leaves it open. Telling it "required" here would
        // tear the connection down on top of an input the loop is still about to run, so the
        // busy check has to sit ahead of the replace:false early return, not behind it.
        var credential = new SandboxCredential("owner", "key");
        var created = new List<FakeMultiTurnAgent>();

        await using var pool = CreatePool(
            credential,
            () => "sess-1",
            (_, _) => Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace")),
            created
        );

        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-probe-unassigned",
                Mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: "workspace-1",
                callerCredential: credential
            );

        _ = await original.SendAsync(
            [new TextMessage { Role = Role.User, Text = "please review this" }],
            inputId: "input-1"
        );

        var result = await pool.EnsureCurrentAgentAsync("thread-probe-unassigned", credential, replace: false);

        result.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred);
        result.Agent.Should().BeSameAs(original);
        original.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureCurrentAgentAsync_DiscardsTheReplacement_WhenInputArrivesWhileItIsBeingBuilt()
    {
        // Checking "is this entry busy?" only BEFORE building the replacement leaves the build
        // itself open, and the build is the slow part — a workspace agent starts a sandbox session
        // over the network. Callers hold their agent reference outside the pool's per-thread lock,
        // so a send lands on the outgoing entry while the incoming one is still being constructed.
        // The factory below IS that interleave, executed deterministically: it sends into the
        // original agent at the exact moment the replacement is under construction.
        var credential = new SandboxCredential("owner", "key");
        var created = new List<FakeMultiTurnAgent>();
        FakeMultiTurnAgent? original = null;

        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                // On the SECOND creation — the replacement — a concurrent caller's send reaches the
                // agent that is about to be discarded.
                if (original is { } incumbent)
                {
                    _ = incumbent
                        .SendAsync(
                            [new TextMessage { Role = Role.User, Text = "arrived mid-rebuild" }],
                            inputId: "input-mid-rebuild"
                        )
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }

                var agent = new FakeMultiTurnAgent(context.ThreadId);
                created.Add(agent);
                return new MultiTurnAgentPool.AgentCreationResult(agent)
                {
                    StagedBinding = new SandboxEstablishedBinding(
                        new WorkspaceRef("workspace-1"),
                        credential,
                        credential,
                        "sess-1"
                    ),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: new RecordingBindingSink(),
            liveSessionResolver: (_, _) =>
                Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace"))
        );

        original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-mid-rebuild",
                Mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: "workspace-1",
                callerCredential: credential
            );

        original
            .HasUnassignedInput.Should()
            .BeFalse("the window is not open yet — the send happens during the rebuild");

        var result = await pool.EnsureCurrentAgentAsync("thread-mid-rebuild", credential);

        original
            .Disposed.Should()
            .BeFalse(
                "the input acknowledged mid-rebuild lives in this agent's channel; the replacement is the disposable one"
            );
        result.Agent.Should().BeSameAs(original, "the pool must keep the entry that holds the acknowledged input");
        result.Status.Should().Be(MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred);
        pool.TryGet("thread-mid-rebuild", out var pooled).Should().BeTrue();
        pooled.Should().BeSameAs(original, "the discarded replacement must never have been committed");
        created.Should().HaveCount(2, "a replacement really was built — and then thrown away, not kept");
        created[1].Disposed.Should().BeTrue("the abandoned replacement must be torn down, not leaked");
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
                        "sess-1"
                    ),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: new RecordingBindingSink(),
            liveSessionResolver: (_, _) =>
                Task.FromResult(new SandboxSession("workspace-1", "sess-2", "workspace", "/workspace"))
        );

        var original = pool.GetOrCreateAgent(
            "thread-failed-refresh",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: credential
        );

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
            }
        );

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
            created
        );

        _ = pool.GetOrCreateAgent(
            "thread-foreign",
            Mode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            requestedWorkspaceId: "workspace-1",
            callerCredential: owner
        );

        Func<Task> act = () => pool.EnsureCurrentAgentAsync("thread-foreign", foreign);

        await act.Should().ThrowAsync<SandboxCredentialConflictException>();
        resolverCalls.Should().Be(0);
        created.Should().ContainSingle();
    }

    private static MultiTurnAgentPool CreatePool(
        SandboxCredential credential,
        Func<string> sessionId,
        Func<SandboxEstablishedBinding, CancellationToken, Task<SandboxSession>> resolver,
        List<FakeMultiTurnAgent> created
    )
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
                        sessionId()
                    ),
                };
            },
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: new RecordingBindingSink(),
            liveSessionResolver: resolver
        );
    }

    private sealed class RecordingBindingSink : ISandboxBindingSink
    {
        public List<(string ThreadId, SandboxEstablishedBinding Binding)> Published { get; } = [];

        public void PublishEstablishedBinding(string threadId, SandboxEstablishedBinding binding) =>
            Published.Add((threadId, binding));

        public void ClearEstablishedBinding(string threadId) { }
    }
}
