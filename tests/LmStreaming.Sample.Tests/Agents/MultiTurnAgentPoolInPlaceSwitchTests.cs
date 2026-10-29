using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

/// <summary>
/// The in-place half of a mode/provider switch: when the factory hands the SAME agent back, the pool
/// reconfigures the live entry instead of building a replacement and evicting the old one.
/// </summary>
/// <remarks>
/// <para>
/// The pool's rule is reference equality of the returned agent — not a type test. That is deliberate:
/// whether a live loop CAN be reconfigured is the factory's question (it owns the provider arms, the
/// tool wiring and the busy refusal), and the pool only has to recognise the answer. So the fakes here
/// are ordinary <see cref="FakeMultiTurnAgent"/>s and the "in place" factory is one line:
/// <c>ctx.Existing!.Agent</c>.
/// </para>
/// <para>
/// What each test pins is a thing the entry must KEEP (the run task, the accepted-input ledger, the
/// agent itself) or must UPDATE (mode, provider, resources, the persisted binding), because the whole
/// value of the branch is in that split.
/// </para>
/// </remarks>
[Collection("EnvironmentVariables")]
public class MultiTurnAgentPoolInPlaceSwitchTests
{
    private static AgentProfile DefaultMode => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    [Fact]
    public async Task AModeSwitchTheFactoryServesInPlace_KeepsTheAgentAndItsRunTask()
    {
        var store = new InMemoryConversationStore();
        var contexts = new List<MultiTurnAgentPool.AgentCreationContext>();
        await using var pool = CreatePool(store, contexts, InPlaceFactory);

        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-inplace-mode",
                DefaultMode,
                requestedProviderId: "test",
                requestResponseDumpFileName: null
            );
        (await WaitForPersistedPropertyAsync(store, "thread-inplace-mode", "mode")).Should().Be(DefaultMode.Id);
        var runTaskBefore = pool.GetRunTaskForTest("thread-inplace-mode");

        var notifications = new List<string>();
        pool.ThreadRemoved += id => notifications.Add(id);

        // Typed, not inferred: SystemChatModes hands out ChatMode and the pool takes AgentProfile, so an
        // inferred local would be converted afresh at every use and no reference comparison against the
        // entry's mode could ever hold.
        AgentProfile newMode = SystemChatModes.All.First(m => m.Id != DefaultMode.Id);
        var result = await pool.SwitchModeAsync("thread-inplace-mode", newMode);

        result.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.ReconfiguredInPlace);
        result.Agent.Should().BeSameAs(original, "the factory handed the live agent back");
        ReferenceEquals(pool.GetOrCreateAgent("thread-inplace-mode", newMode), original)
            .Should()
            .BeTrue("the SAME instance is still the pooled one");

        // The run loop is the thing a recreate kills and this branch exists to keep. Same Task object,
        // so nothing restarted it — an equal-but-new task would mean the agent's loop was torn down.
        // And still RUNNING: keeping the same task while cancelling its token would be the same loss
        // wearing the right reference.
        pool.GetRunTaskForTest("thread-inplace-mode").Should().BeSameAs(runTaskBefore);
        runTaskBefore!.IsCompleted.Should().BeFalse("the kept run loop is still live");

        pool.GetAgentMode("thread-inplace-mode").Should().BeSameAs(newMode, "the entry took the new mode");
        (await WaitForPersistedPropertyAsync(store, "thread-inplace-mode", "mode", newMode.Id))
            .Should()
            .Be(newMode.Id, "an in-place switch persists exactly as a recreate does");
        notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task AProviderSwitchTheFactoryServesInPlace_KeepsTheAgentAndPersistsTheProvider()
    {
        var store = new InMemoryConversationStore();
        var contexts = new List<MultiTurnAgentPool.AgentCreationContext>();
        await using var pool = CreatePool(store, contexts, InPlaceFactory);

        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-inplace-prov",
                DefaultMode,
                requestedProviderId: "test",
                requestResponseDumpFileName: null
            );
        (await WaitForPersistedProviderAsync(store, "thread-inplace-prov")).Should().Be("test");
        var runTaskBefore = pool.GetRunTaskForTest("thread-inplace-prov");

        var result = await pool.SwitchProviderAsync("thread-inplace-prov", "openai", DefaultMode);

        result.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.ReconfiguredInPlace);
        result.Agent.Should().BeSameAs(original);
        pool.GetRunTaskForTest("thread-inplace-prov").Should().BeSameAs(runTaskBefore);
        runTaskBefore!.IsCompleted.Should().BeFalse("the kept run loop is still live");

        // The entry's provider is what the NEXT turn resolves against, so it has to move even though
        // nothing was rebuilt; the persisted copy is what a later refresh restores.
        (await WaitForPersistedProviderAsync(store, "thread-inplace-prov", "openai"))
            .Should()
            .Be("openai");
        pool.GetEffectiveProviderId("thread-inplace-prov", null).Should().Be("openai");

        // ...and the factory was told which provider to reconfigure to, rather than being asked to
        // build one: the second call carries the new id AND the live agent.
        contexts.Should().HaveCount(2);
        contexts[1].ProviderId.Should().Be("openai");
        contexts[1].Existing!.Agent.Should().BeSameAs(original);
        contexts[1].Existing!.ProviderId.Should().Be("test", "the context reports the provider being left");
    }

    [Fact]
    public async Task AFactoryThatBuildsANewAgent_StillRecreates_AndDisposesTheOldOne()
    {
        // The non-vacuity anchor for every in-place assertion above: the branch is chosen by what the
        // factory returns, so a factory that returns something new must still get today's path.
        var store = new InMemoryConversationStore();
        var contexts = new List<MultiTurnAgentPool.AgentCreationContext>();
        await using var pool = CreatePool(store, contexts, ctx => new FakeMultiTurnAgent(ctx.ThreadId));

        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-recreate",
                DefaultMode,
                requestedProviderId: "test",
                requestResponseDumpFileName: null
            );
        var runTaskBefore = pool.GetRunTaskForTest("thread-recreate");

        var result = await pool.SwitchProviderAsync("thread-recreate", "openai", DefaultMode);

        result.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.Recreated);
        result.Agent.Should().NotBeSameAs(original);
        pool.GetRunTaskForTest("thread-recreate").Should().NotBeSameAs(runTaskBefore);

        // Disposal is what makes it a recreate rather than a leak. The fake's run loop parks on the
        // entry's token, so the old entry's task completing IS its teardown having run.
        await Wait.UntilAsync(
            () => runTaskBefore!.IsCompleted,
            "the evicted entry's run task is cancelled and drained by its disposal"
        );

        // The context for the recreate still OFFERED the live agent — the factory simply declined it.
        contexts[1].Existing!.Agent.Should().BeSameAs(original);
    }

    /// <summary>
    /// #792: a recreate disposes the old loop, and with it every sub-agent that loop was running. The
    /// child must say WHY it stopped: its parent was replaced, not the host shut down. Read back from
    /// the child's persisted metadata, through the sample's own provenance store, because that is what
    /// the panel shows once the old loop and its live roster are gone.
    /// </summary>
    [Fact]
    public async Task ARecreate_StopsTheOldLoopsRunningSubAgent_AsParentRecreated()
    {
        const string threadId = "thread-recreate-child";
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MultiTurnAgentLoop? parent = null;
        var store = new InMemoryConversationStore();
        var contexts = new List<MultiTurnAgentPool.AgentCreationContext>();
        await using var pool = CreatePool(
            store,
            contexts,
            ctx =>
                parent is null
                    ? parent = new MultiTurnAgentLoop(
                        BlockingProvider(),
                        new FunctionRegistry(),
                        ctx.ThreadId,
                        subAgentOptions: WorkerTemplate(store, childEntered)
                    )
                    : new FakeMultiTurnAgent(ctx.ThreadId)
        );
        _ = pool.GetOrCreateAgent(
            threadId,
            DefaultMode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null
        );
        _ = await parent!.SubAgentManager!.SpawnAsync("worker", "long task", name: "alpha", runInBackground: true);
        await childEntered.Task.WaitAsync(Wait.DefaultTimeout);

        var result = await pool.SwitchProviderAsync(threadId, "openai", DefaultMode);

        result.Kind.Should().Be(MultiTurnAgentPool.AgentSwitchKind.Recreated, "guard: only a recreate drops children");
        SubAgentSummary? child = null;
        await Wait.UntilAsync(
            async () =>
            {
                child = await ReadChildAsync(store, threadId);
                return child?.Status == "stopped";
            },
            "the old loop's disposal persists its running child as stopped",
            observed: () => $"child status: {child?.Status ?? "<none>"}"
        );
        child!
            .FailureCode.Should()
            .Be(
                SubAgentFailureCodes.ParentRecreated,
                "the child stopped because its parent was replaced, not because the host shut down"
            );
    }

    private static async Task<SubAgentSummary?> ReadChildAsync(IConversationStore store, string parentThreadId)
    {
        foreach (var metadata in await store.ListThreadsAsync(limit: 100))
        {
            if (SubAgentProvenance.TryProject(metadata, parentThreadId) is { } summary)
            {
                return summary;
            }
        }

        return null;
    }

    [Fact]
    public async Task AnInPlaceSwitch_DisposesOnlyTheResourcesTheFactoryDropped()
    {
        // The reuse rule, which is the whole point of handing the previous resources back: a key the
        // new configuration still holds keeps its instance alive; a key it no longer holds is torn
        // down; a key it added is retained for the NEXT switch to be offered.
        var kept = new TrackedResource();
        var dropped = new TrackedResource();
        var added = new TrackedResource();

        var store = new InMemoryConversationStore();
        var contexts = new List<MultiTurnAgentPool.AgentCreationContext>();
        await using var pool = new MultiTurnAgentPool(
            context =>
            {
                if (context.Existing is null)
                {
                    contexts.Add(context);
                    return new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(context.ThreadId))
                    {
                        ReusableResources = new Dictionary<string, IAsyncDisposable>
                        {
                            ["kept"] = kept,
                            ["dropped"] = dropped,
                        },
                    };
                }

                contexts.Add(context);
                return new MultiTurnAgentPool.AgentCreationResult(context.Existing.Agent)
                {
                    // Same INSTANCE under the same key, plus a new one. "dropped" is simply absent.
                    ReusableResources = new Dictionary<string, IAsyncDisposable>
                    {
                        ["kept"] = context.Existing.Resources["kept"],
                        ["added"] = added,
                    },
                };
            },
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]).ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

        _ = pool.GetOrCreateAgent(
            "thread-resources",
            DefaultMode,
            requestedProviderId: "test",
            requestResponseDumpFileName: null
        );
        _ = await pool.RecreateAgentWithProviderAsync("thread-resources", "openai", DefaultMode);

        await Wait.UntilAsync(() => dropped.Disposals == 1, "the resource the new configuration dropped is torn down");
        kept.Disposals.Should().Be(0, "the same instance under the same key is still in use by the live agent");
        added.Disposals.Should().Be(0);

        // Retained, not merely accepted: a third switch is offered both surviving keys, which is what
        // makes reuse work more than once.
        _ = await pool.RecreateAgentWithProviderAsync("thread-resources", "test", DefaultMode);
        contexts[2].Existing!.Resources.Keys.Should().BeEquivalentTo(["kept", "added"]);
    }

    [Fact]
    public async Task AFactoryThatRefusesABusySwitch_LeavesTheEntryExactlyAsItWas()
    {
        // The decided busy behaviour: the factory (not the pool) judges whether the live loop can take
        // the new configuration, and says no by throwing. The pool must not swallow that into a
        // recreate, and must leave the conversation untouched — same agent, same mode, not disposed.
        var store = new InMemoryConversationStore();
        var contexts = new List<MultiTurnAgentPool.AgentCreationContext>();
        await using var pool = CreatePool(
            store,
            contexts,
            ctx =>
                ctx.Existing is null ? new FakeMultiTurnAgent(ctx.ThreadId) : throw new AgentBusyException(ctx.ThreadId)
        );

        // Captured once, and typed: SystemChatModes hands out ChatMode and the pool takes AgentProfile,
        // so every read of DefaultMode is a fresh conversion. Comparing the entry's mode against a
        // second read would compare two equal-but-distinct objects and could never prove the entry was
        // left alone.
        AgentProfile startingMode = DefaultMode;
        var original = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-busy",
                startingMode,
                requestedProviderId: "test",
                requestResponseDumpFileName: null
            );
        original.StartRun("run_streaming");
        var runTaskBefore = pool.GetRunTaskForTest("thread-busy");

        var newMode = SystemChatModes.All.First(m => m.Id != startingMode.Id);
        var act = async () => await pool.RecreateAgentWithModeAsync("thread-busy", newMode);

        (await act.Should().ThrowAsync<AgentBusyException>()).Which.ThreadId.Should().Be("thread-busy");

        ReferenceEquals(pool.GetOrCreateAgent("thread-busy", startingMode), original)
            .Should()
            .BeTrue("a refused switch must not evict the agent that is mid-run");
        pool.GetAgentMode("thread-busy").Should().BeSameAs(startingMode, "nothing was applied");
        pool.GetRunTaskForTest("thread-busy").Should().BeSameAs(runTaskBefore);
        runTaskBefore!.IsCompleted.Should().BeFalse("the live agent was not disposed");
        (await WaitForPersistedPropertyAsync(store, "thread-busy", "mode")).Should().Be(startingMode.Id);
    }

    [Fact]
    public async Task TheSwitchContext_ReportsWhetherTheEntryIsMidRun()
    {
        // The fact the factory's refusal is built on. A run in progress reads busy; an idle entry does
        // not — and neither does an entry holding an input that has been accepted and not yet started,
        // because that turn SURVIVES an in-place switch (see the handoff tests).
        var store = new InMemoryConversationStore();
        var contexts = new List<MultiTurnAgentPool.AgentCreationContext>();
        await using var pool = CreatePool(store, contexts, InPlaceFactory);

        var agent = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                "thread-busyflag",
                DefaultMode,
                requestedProviderId: "test",
                requestResponseDumpFileName: null
            );

        agent.CurrentRunId = null;
        agent.IsRunning = false;
        _ = await pool.RecreateAgentWithProviderAsync("thread-busyflag", "openai", DefaultMode);
        contexts[1].Existing!.IsBusy.Should().BeFalse("an idle entry is not busy");

        agent.StartRun("run_1");
        _ = await pool.RecreateAgentWithProviderAsync("thread-busyflag", "test", DefaultMode);
        contexts[2].Existing!.IsBusy.Should().BeTrue("a run is in progress");

        agent.CompleteRun();
        _ = await pool.RecreateAgentWithProviderAsync("thread-busyflag", "openai", DefaultMode);
        contexts[3].Existing!.IsBusy.Should().BeFalse("the run finished, so the entry is idle again");
    }

    /// <summary>A factory arm that serves the switch by handing the live agent straight back.</summary>
    private static IMultiTurnAgent InPlaceFactory(MultiTurnAgentPool.AgentCreationContext context) =>
        context.Existing?.Agent ?? new FakeMultiTurnAgent(context.ThreadId);

    private static MultiTurnAgentPool CreatePool(
        InMemoryConversationStore store,
        List<MultiTurnAgentPool.AgentCreationContext> contexts,
        Func<MultiTurnAgentPool.AgentCreationContext, IMultiTurnAgent> agentFactory
    ) =>
        new(
            context =>
            {
                contexts.Add(context);
                return new MultiTurnAgentPool.AgentCreationResult(agentFactory(context));
            },
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test", "openai"]).ToReal(),
            store,
            NullLogger<MultiTurnAgentPool>.Instance
        );

    /// <summary>
    /// A <c>worker</c> template whose child signals <paramref name="entered"/> and never answers. Child
    /// transcripts go to <paramref name="store"/> with the sample's provenance stamp, as in the host.
    /// </summary>
    private static SubAgentOptions WorkerTemplate(IConversationStore store, TaskCompletionSource entered) =>
        new()
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    Name = "worker",
                    SystemPrompt = "You are a worker.",
                    AgentFactory = () => BlockingProvider(entered),
                },
            },
            MaxConcurrentSubAgents = 2,
            ProvenanceAwareConversationStoreFactory = (childThreadId, parentThreadId, describeChild) =>
                // The same wiring as Program.cs: the manager also asks for the ROOT's store, with no parent.
                parentThreadId is null
                    ? new NonOwningConversationStore(store)
                    : new NonOwningConversationStore(
                        store,
                        provenanceThreadId: childThreadId,
                        provenance: () => SubAgentProvenance.Build(parentThreadId, describeChild(childThreadId))
                    ),
        };

    private static IStreamingAgent BlockingProvider(TaskCompletionSource? entered = null)
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> _, GenerateReplyOptions? _, CancellationToken ct) =>
                {
                    _ = entered?.TrySetResult();
                    return Task.FromResult(BlockingStream(ct));
                }
            );
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>An owned/reusable resource that counts how many times it was disposed.</summary>
    private sealed class TrackedResource : IAsyncDisposable
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.Increment(ref _disposals);
            return ValueTask.CompletedTask;
        }
    }

    private static Task<string> WaitForPersistedProviderAsync(
        IConversationStore store,
        string threadId,
        string? expected = null
    ) => WaitForPersistedPropertyAsync(store, threadId, MultiTurnAgentPool.ProviderPropertyKey, expected);

    /// <summary>
    /// Reads a fire-and-forget metadata write back.
    /// </summary>
    /// <remarks>
    /// It takes <paramref name="expected"/>, unlike the sibling in <c>MultiTurnAgentPoolTests</c>,
    /// because every switch here OVERWRITES a value that is already persisted: a wait for mere presence
    /// would be satisfied by the pre-switch value and the assertion would then pass on a write that
    /// never happened.
    /// </remarks>
    private static async Task<string> WaitForPersistedPropertyAsync(
        IConversationStore store,
        string threadId,
        string key,
        string? expected = null
    )
    {
        string? value = null;
        await Wait.UntilAsync(
            async () =>
            {
                var metadata = await store.LoadMetadataAsync(threadId);
                value =
                    metadata?.Properties != null && metadata.Properties.TryGetValue(key, out var raw) && raw is string s
                        ? s
                        : null;
                return expected is null ? value is not null : value == expected;
            },
            $"the pool persisted '{key}'{(expected is null ? "" : $" as '{expected}'")} for thread '{threadId}'",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(20)
        );

        return value!;
    }
}
