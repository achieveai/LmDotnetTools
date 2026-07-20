using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Unit coverage for the presentation-only observation seam added for WI #194 (Task 9):
/// <see cref="SubAgentManager.SubscribeToAgentAcrossRestartsAsync"/> plus the
/// <c>SubAgentState.SignalAgentReplaced</c>/<c>AgentReplacedTask</c> pair it relies on. These prove a
/// focused observer keeps receiving frames when a FINISHED owned-provider child is relayed a follow-up
/// (which disposes the old loop and swaps in a fresh one), and that manager teardown ends the observer
/// cleanly. No change to spawn/send/monitor/restart execution is exercised — only the read seam.
/// </summary>
public class SubAgentManagerSubscribeAcrossRestartsTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public SubAgentManagerSubscribeAcrossRestartsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await _manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeAcrossRestarts_SpansOwnedProviderRestart_YieldsSecondRunMessages()
    {
        // A finished owned-provider child, when relayed a follow-up, is restarted with a FRESH loop
        // instance (the old one is disposed). An observer bound to the old instance must not end when
        // that instance's stream closes on dispose — it must follow the swap and stream the SECOND run.
        const string firstText = "first-run-answer";
        const string secondText = "second-run-answer";

        var createdAgents = new List<ObservableFakeAgent>();
        var agentCallCount = 0;

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        });

        manager.TestAgentFactoryOverride = (agentId, _) =>
        {
            var idx = Interlocked.Increment(ref agentCallCount);
            var agent = new ObservableFakeAgent
            {
                ThreadId = $"subagent-{agentId}",
                // Run 1 emits its text + a terminal completion; run 2 (the restart) emits a DISTINCT
                // text + completion. Each instance blocks its subscriptions open after its messages
                // until the instance is disposed (mirrors MultiTurnAgentBase completing subscriber
                // channels on DisposeAsync).
                RunMessages = idx == 1
                    ?
                    [
                        new TextMessage { Text = firstText, Role = Role.Assistant },
                        new RunCompletedMessage { CompletedRunId = "run-1" },
                    ]
                    :
                    [
                        new TextMessage { Text = secondText, Role = Role.Assistant },
                        new RunCompletedMessage { CompletedRunId = "run-2" },
                    ],
            };
            lock (createdAgents)
            {
                createdAgents.Add(agent);
            }

            return agent;
        };

        // Owned provider so completion disposes it, forcing the follow-up to take the restart path.
        manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        // Deterministically wait for the first run to reach terminal completion (owned provider disposed).
        await WaitForConditionAsync(
            () =>
            {
                try { return manager.Peek(agentId).Contains("\"completed\"", StringComparison.Ordinal); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var collected = new List<IMessage>();
        var sawFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start the restart-spanning observer BEFORE the follow-up. It subscribes to the finished
        // instance (getting run 1), stays attached, and must transparently pick up the replacement.
        var observerTask = Task.Run(async () =>
        {
            await foreach (var msg in manager.SubscribeToAgentAcrossRestartsAsync(agentId, observeCts.Token))
            {
                lock (collected)
                {
                    collected.Add(msg);
                }

                if (msg is TextMessage tm && tm.Text == firstText)
                {
                    _ = sawFirst.TrySetResult();
                }

                if (msg is TextMessage tm2 && tm2.Text == secondText)
                {
                    _ = sawSecond.TrySetResult();
                    return; // seen the restarted turn spanning the swap — done.
                }
            }
        }, observeCts.Token);

        // Gate the follow-up on the observer having attached to and drained run 1, so the swap is
        // genuinely observed mid-subscription (not a post-hoc re-resolve).
        await sawFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: relay a follow-up to the finished child -> owned-provider restart -> fresh instance.
        _ = await manager.SendMessageAsync(agentId, "continue", runInBackground: true);

        lock (createdAgents)
        {
            createdAgents.Should().HaveCount(2, "the restart must have created a replacement instance");
        }

        // The observer, without ending between runs, must yield the SECOND run's text (it spanned the
        // owned-provider instance swap).
        await sawSecond.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await observerTask.WaitAsync(TimeSpan.FromSeconds(10));

        List<IMessage> snapshot;
        lock (collected)
        {
            snapshot = [.. collected];
        }

        foreach (var m in snapshot)
        {
            _output.WriteLine($"observed {m.GetType().Name} {(m as TextMessage)?.Text}");
        }

        snapshot.OfType<TextMessage>().Select(t => t.Text).Should().Contain(firstText);
        snapshot.OfType<TextMessage>().Select(t => t.Text).Should().Contain(
            secondText,
            "the observer must follow the child across the owned-provider restart swap and stream run 2");

        // The run-1 text must precede the run-2 text in a single uninterrupted enumeration (no early end).
        var firstIndex = snapshot.FindIndex(m => m is TextMessage t && t.Text == firstText);
        var secondIndex = snapshot.FindIndex(m => m is TextMessage t && t.Text == secondText);
        secondIndex.Should().BeGreaterThan(
            firstIndex, "the second run's text arrives after the first on the same continuous stream");
    }

    [Fact]
    public async Task SubscribeAcrossRestarts_WhenSubscriberDroppedWithoutRestart_EndsPromptly()
    {
        // Backpressure DROP path: a slow subscriber can be removed by
        // MultiTurnAgentBase.PublishToSubscriber while the instance stays alive — no restart, so no
        // replacement signal ever fires AND no restart-in-progress flag is set. DecideAfterStreamEnd must
        // therefore return EndStream and the observer must end PROMPTLY (deterministically, no timeout) so
        // the focus socket closes and the client can reconnect + replay.
        const string attachedSentinel = "attached-sentinel";
        ObservableFakeAgent? liveAgent = null;

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        });

        manager.TestAgentFactoryOverride = (agentId, _) =>
        {
            liveAgent = new ObservableFakeAgent
            {
                ThreadId = $"subagent-{agentId}",
                RunMessages = [new TextMessage { Text = attachedSentinel, Role = Role.Assistant }],
            };
            return liveAgent;
        };

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var observerTask = Task.Run(async () =>
        {
            await foreach (var msg in manager.SubscribeToAgentAcrossRestartsAsync(agentId, observeCts.Token))
            {
                if (msg is TextMessage tm && tm.Text == attachedSentinel)
                {
                    _ = attached.TrySetResult();
                }
            }
        }, observeCts.Token);

        await attached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: drop the subscriber WITHOUT a restart/teardown (no replacement signal fires, no restart flag).
        liveAgent!.SimulateSubscriberDrop();

        // The observer must end promptly via the deterministic EndStream decision (not hang), and NOT
        // because its own token cancelled.
        var completed = await Task.WhenAny(observerTask, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().BeSameAs(
            observerTask, "a dropped subscriber with no replacement signal and no restart in flight must end");
        await observerTask;
        observeCts.IsCancellationRequested.Should().BeFalse();

        // The child instance is still alive/registered — the drop did not tear it down.
        manager.TryGetAgent(agentId, out var still).Should().BeTrue();
        still.Should().BeSameAs(liveAgent);
    }

    [Fact]
    public async Task SubscribeAcrossRestarts_WhenManagerDisposes_EndsCleanly()
    {
        // Teardown path: DisposeAsync signals every state's replacement awaitable with null, so an
        // observer blocked on a still-running child unblocks and the enumeration ends (rather than
        // hanging forever waiting for a swap that will never come).
        const string attachedSentinel = "attached-sentinel";

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        });

        manager.TestAgentFactoryOverride = (agentId, _) => new ObservableFakeAgent
        {
            ThreadId = $"subagent-{agentId}",
            // One non-terminal sentinel, then the subscription blocks open until teardown. The sentinel
            // lets the observer prove (deterministically, no sleep) it has captured the replacement
            // awaitable and is actively subscribed before we dispose.
            RunMessages = [new TextMessage { Text = attachedSentinel, Role = Role.Assistant }],
        };

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var observerTask = Task.Run(async () =>
        {
            await foreach (var msg in manager.SubscribeToAgentAcrossRestartsAsync(agentId, observeCts.Token))
            {
                if (msg is TextMessage tm && tm.Text == attachedSentinel)
                {
                    // The observer has captured the replacement awaitable and is subscribed to the
                    // running child — safe to tear down and assert the teardown ends the stream.
                    _ = attached.TrySetResult();
                }
            }
        }, observeCts.Token);

        await attached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: tearing down the manager must end the observer without cancelling the observer's own token.
        await manager.DisposeAsync();
        _manager = null; // already disposed; avoid a double dispose in DisposeAsync().

        var completed = await Task.WhenAny(observerTask, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().BeSameAs(
            observerTask, "manager teardown signals a null replacement so the observer ends cleanly");

        // Surface any fault and confirm the observer was not force-cancelled (it ended via the null signal).
        await observerTask;
        observeCts.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task SnapshotForObservation_PairsCapturedAgentWithItsOwnReplacementSignal()
    {
        // The atomic read seam the observer uses: SnapshotForObservation returns the live Agent AND the
        // awaitable that fires when THAT agent is replaced, captured together. Prove the pairing is by
        // epoch — the signal captured alongside a0 resolves to a0's replacement (a1), never to a0 — and
        // that each swap installs a fresh signal for the next epoch.
        var a0 = ObservableAgent();
        var a1 = ObservableAgent();
        var a2 = ObservableAgent();
        var state = NewObservationState(a0);

        var s0 = state.SnapshotForObservation();
        s0.Agent.Should().BeSameAs(a0);
        s0.AgentReplaced.IsCompleted.Should().BeFalse("no swap has happened yet");

        // Restart swap 1: a0 -> a1, atomically (Agent-set + signal under one lock).
        state.SwapLiveAgentAndSignalReplaced(a1);

        s0.AgentReplaced.IsCompletedSuccessfully.Should().BeTrue();
        (await s0.AgentReplaced).Should().BeSameAs(
            a1, "the signal captured with a0 resolves to a0's replacement, never to a0 itself");

        var s1 = state.SnapshotForObservation();
        s1.Agent.Should().BeSameAs(a1);
        s1.AgentReplaced.Should().NotBeSameAs(s0.AgentReplaced, "a fresh signal is installed per epoch");
        s1.AgentReplaced.IsCompleted.Should().BeFalse("the a1 epoch has not been replaced yet");

        // Restart swap 2: a1 -> a2.
        state.SwapLiveAgentAndSignalReplaced(a2);
        (await s1.AgentReplaced).Should().BeSameAs(a2);
    }

    [Fact]
    public async Task SnapshotForObservation_UnderConcurrentSwaps_NeverPairsAgentWithASignalThatResolvedToIt()
    {
        // Drive many restart swaps concurrently with many snapshots. A consistent pair (agentN, signalN)
        // ALWAYS resolves to agent(N+1) != agentN, so the captured signal can never resolve to the SAME
        // agent it was captured with. A torn two-read pair — (newAgent, oldSignal) where oldSignal already
        // fired with newAgent — WOULD. The dedicated lock makes the snapshot and the swap mutually atomic,
        // so no torn pair can ever be observed.
        const int iterations = 5000;
        var agents = new ObservableFakeAgent[iterations + 1];
        for (var i = 0; i <= iterations; i++)
        {
            agents[i] = ObservableAgent();
        }

        var state = NewObservationState(agents[0]);
        using var gate = new Barrier(2);
        var tornPairs = 0;

        var swapper = Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var i = 1; i <= iterations; i++)
            {
                state.SwapLiveAgentAndSignalReplaced(agents[i]);
            }
        });

        var observer = Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var i = 0; i < iterations; i++)
            {
                var (agent, replaced) = state.SnapshotForObservation();
                if (replaced.IsCompletedSuccessfully && ReferenceEquals(replaced.Result, agent))
                {
                    _ = Interlocked.Increment(ref tornPairs);
                }
            }
        });

        await Task.WhenAll(swapper, observer);

        tornPairs.Should().Be(
            0, "an atomic snapshot never pairs an agent with a signal that already resolved to that same agent");
    }

    [Fact]
    public void DecideAfterStreamEnd_WhenNoRestartAndReplacementPending_ReturnsEndStream()
    {
        // A backpressure DROP: the subscribed instance's stream ended, no restart is in flight, and no
        // replacement was signalled. The decision must be EndStream — deterministically, with no timeout.
        var state = NewObservationState(ObservableAgent());
        var (_, replaced) = state.SnapshotForObservation();
        replaced.IsCompleted.Should().BeFalse();

        state.DecideAfterStreamEnd(replaced).Should().Be(ObservationContinuation.EndStream);
    }

    [Fact]
    public void DecideAfterStreamEnd_WhenRestartInProgress_ReturnsAwaitReplacement()
    {
        // A restart is in flight (SignalRestartStarting set BEFORE the dispose that ends the observer's
        // stream) but the swap has NOT delivered the replacement yet, so `replaced` is still pending. The
        // decision must be AwaitReplacement so the observer waits for the imminent signal with no timeout,
        // regardless of how long the dispose + cleanup takes.
        var state = NewObservationState(ObservableAgent());
        var (_, replaced) = state.SnapshotForObservation();

        state.SignalRestartStarting();
        replaced.IsCompleted.Should().BeFalse("the swap has not delivered the replacement yet");

        state.DecideAfterStreamEnd(replaced).Should().Be(ObservationContinuation.AwaitReplacement);
    }

    [Fact]
    public async Task DecideAfterStreamEnd_WhenReplacementAlreadySignalled_ReturnsAwaitReplacement()
    {
        // The replacement (or teardown null) already fired before the observer decided: await it to pick
        // up the new instance (or end on null).
        var state = NewObservationState(ObservableAgent());
        var (_, replaced) = state.SnapshotForObservation();

        var a1 = ObservableAgent();
        state.SwapLiveAgentAndSignalReplaced(a1);
        replaced.IsCompleted.Should().BeTrue();

        state.DecideAfterStreamEnd(replaced).Should().Be(ObservationContinuation.AwaitReplacement);
        (await replaced).Should().BeSameAs(a1);
    }

    [Fact]
    public void DecideAfterStreamEnd_AfterSwapDeliversReplacement_ClearsRestartFlagForNextEpoch()
    {
        // The restart-in-progress flag must fall exactly when the replacement is delivered, so the NEXT
        // epoch's stream end (with no new restart) is correctly treated as a drop (EndStream), not a
        // lingering "await forever".
        var state = NewObservationState(ObservableAgent());
        state.SignalRestartStarting();
        state.SwapLiveAgentAndSignalReplaced(ObservableAgent());

        var (_, nextReplaced) = state.SnapshotForObservation();
        nextReplaced.IsCompleted.Should().BeFalse("a fresh signal is installed for the new epoch");

        state.DecideAfterStreamEnd(nextReplaced).Should().Be(
            ObservationContinuation.EndStream, "the swap cleared the restart flag");
    }

    [Fact]
    public void DecideAfterStreamEnd_AfterTeardownSignal_ClearsRestartFlag()
    {
        // Teardown (SignalAgentReplaced(null)) must also clear the restart flag, so a stale flag can never
        // strand a later epoch's observer awaiting a signal that will never come.
        var state = NewObservationState(ObservableAgent());
        state.SignalRestartStarting();
        state.SignalAgentReplaced(null);

        var (_, nextReplaced) = state.SnapshotForObservation();
        state.DecideAfterStreamEnd(nextReplaced).Should().Be(
            ObservationContinuation.EndStream, "teardown cleared the restart flag");
    }

    [Fact]
    public async Task SubscribeAcrossRestarts_SlowOwnedProviderRestart_DoesNotTerminateAndYieldsSecondRun()
    {
        // Round-3 BLOCKER #3: a legitimate owned-provider restart whose old-instance dispose + cleanup is
        // SLOW must NOT terminate a valid focus stream. The old timing heuristic (2s grace) would have
        // expired and wrongly ended the stream. With the explicit restart-intent signal the observer
        // deterministically waits for the definitive replacement signal — however long the dispose takes —
        // with NO wall-clock timeout. Proven with a TCS gate holding the dispose OPEN between the point it
        // ends the observer's old stream and the swap that delivers the replacement.
        const string firstText = "first-run-answer";
        const string secondText = "second-run-answer";

        var createdAgents = new List<ObservableFakeAgent>();
        var agentCallCount = 0;

        // Gate the FIRST instance's dispose: it ends the observer's run-1 stream, then blocks here (before
        // the swap) until the test releases it.
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var manager = CreateManager(new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        });

        manager.TestAgentFactoryOverride = (agentId, _) =>
        {
            var idx = Interlocked.Increment(ref agentCallCount);
            var agent = new ObservableFakeAgent
            {
                ThreadId = $"subagent-{agentId}",
                RunMessages = idx == 1
                    ?
                    [
                        new TextMessage { Text = firstText, Role = Role.Assistant },
                        new RunCompletedMessage { CompletedRunId = "run-1" },
                    ]
                    :
                    [
                        new TextMessage { Text = secondText, Role = Role.Assistant },
                        new RunCompletedMessage { CompletedRunId = "run-2" },
                    ],
                // Only the first instance's dispose is gated (it is the one the restart disposes).
                DisposeGate = idx == 1 ? disposeGate : null,
            };
            lock (createdAgents)
            {
                createdAgents.Add(agent);
            }

            return agent;
        };

        manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        await WaitForConditionAsync(
            () =>
            {
                try { return manager.Peek(agentId).Contains("\"completed\"", StringComparison.Ordinal); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));

        using var observeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sawFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var observerTask = Task.Run(async () =>
        {
            await foreach (var msg in manager.SubscribeToAgentAcrossRestartsAsync(agentId, observeCts.Token))
            {
                if (msg is TextMessage tm && tm.Text == firstText)
                {
                    _ = sawFirst.TrySetResult();
                }

                if (msg is TextMessage tm2 && tm2.Text == secondText)
                {
                    _ = sawSecond.TrySetResult();
                    return;
                }
            }
        }, observeCts.Token);

        await sawFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: relay a follow-up -> owned-provider restart. SendMessageAsync awaits RestartRunAsync, which
        // BLOCKS on the gated dispose, so run it off the test thread and drive the gate here.
        var sendTask = Task.Run(() => manager.SendMessageAsync(agentId, "continue", runInBackground: true));

        ObservableFakeAgent firstAgent;
        lock (createdAgents)
        {
            firstAgent = createdAgents[0];
        }

        // The restart has built the replacement, set the restart-in-progress flag, and entered the gated
        // dispose that ended the observer's run-1 stream — but the swap (which completes `replaced`) has
        // NOT happened yet.
        await firstAgent.DisposeEnteredTask.WaitAsync(TimeSpan.FromSeconds(10));

        // The observer's run-1 stream has ended and no replacement has arrived, yet — because a restart is
        // in flight — it must be AWAITING the replacement, not ended. The old 2s grace would eventually
        // have ended it here; the explicit signal never does.
        observerTask.IsCompleted.Should().BeFalse(
            "a restart in flight must keep the observer awaiting the replacement, not terminate the stream");
        sawSecond.Task.IsCompleted.Should().BeFalse("the swap has not delivered the second run yet");

        // Release the gated dispose -> restart proceeds to the swap -> `replaced` completes -> the observer
        // re-subscribes to the replacement and streams run 2.
        disposeGate.SetResult();

        await sawSecond.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await observerTask.WaitAsync(TimeSpan.FromSeconds(10));
        await sendTask.WaitAsync(TimeSpan.FromSeconds(10));

        lock (createdAgents)
        {
            createdAgents.Should().HaveCount(2, "the restart created exactly one replacement instance");
        }

        observeCts.IsCancellationRequested.Should().BeFalse(
            "the observer followed the slow restart via the signal, not via cancellation");
    }

    #region Helpers

    private static ObservableFakeAgent ObservableAgent() => new() { RunMessages = [] };

    private static SubAgentState NewObservationState(IMultiTurnAgent agent) =>
        new()
        {
            AgentId = "agent-obs",
            TemplateName = "tmpl",
            Task = "observe",
            Agent = agent,
            Template = new SubAgentTemplate
            {
                Name = "tmpl",
                SystemPrompt = "You are a test agent.",
                AgentFactory = () => new Mock<IStreamingAgent>().Object,
            },
        };

    private SubAgentManager CreateManager(
        IReadOnlyDictionary<string, SubAgentTemplate> templates,
        int maxConcurrent = 5)
    {
        var options = new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = maxConcurrent,
        };

        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
        _manager = manager;
        return manager;
    }

    /// <summary>
    /// A template whose real <see cref="SubAgentTemplate.AgentFactory"/> is never invoked (bypassed
    /// by <see cref="SubAgentManager.TestAgentFactoryOverride"/>).
    /// </summary>
    private static SubAgentTemplate DummyTemplate(string name)
    {
        return new SubAgentTemplate
        {
            Name = name,
            SystemPrompt = "You are a test agent.",
            AgentFactory = () => throw new NotSupportedException(
                "Bypassed by TestAgentFactoryOverride; should never be invoked."),
        };
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    #endregion
}

/// <summary>
/// Minimal <see cref="IMultiTurnAgent"/> test double whose subscriptions faithfully END when the
/// instance is disposed — mirroring <c>MultiTurnAgentBase.DisposeAsync</c> completing subscriber
/// channels. Each <see cref="SubscribeAsync"/> call yields the configured <see cref="RunMessages"/> then
/// blocks the subscription open until this instance is disposed (or the subscriber cancels), so a test
/// can drive an owned-provider restart's instance swap and prove an observer follows it across the swap.
/// </summary>
internal sealed class ObservableFakeAgent : IMultiTurnAgent
{
    private readonly TaskCompletionSource _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Ends an active subscription WITHOUT disposing the instance — simulates
    // MultiTurnAgentBase.PublishToSubscriber dropping a slow subscriber under backpressure (the
    // instance stays alive/registered and no replacement is signalled).
    private readonly TaskCompletionSource _dropped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completes the instant DisposeAsync is entered and has already ended open subscriptions (set
    // _disposed) — before it awaits DisposeGate. Lets a test know the observer's OLD stream has been
    // closed (so the restart-in-progress flag is already set and the observer is about to decide) before
    // it releases the gate, WITHOUT any wall-clock delay.
    private readonly TaskCompletionSource _disposeEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required IReadOnlyList<IMessage> RunMessages { get; init; }

    public string ThreadId { get; init; } = "fake-thread";

    /// <summary>
    /// Optional gate: when set, <see cref="DisposeAsync"/> ends open subscriptions (mirroring a restart
    /// disposing the previous loop) and THEN awaits this gate before returning. Lets a test hold an
    /// owned-provider restart between the dispose that ends the observer's old stream and the swap that
    /// delivers the replacement — proving a SLOW restart never terminates the stream, deterministically
    /// and without wall-clock delay.
    /// </summary>
    public TaskCompletionSource? DisposeGate { get; init; }

    /// <summary>Completes once <see cref="DisposeAsync"/> has ended open subscriptions and is about to
    /// await <see cref="DisposeGate"/>.</summary>
    public Task DisposeEnteredTask => _disposeEntered.Task;

    /// <summary>Simulate a backpressure drop: ends open subscriptions while the instance stays alive.</summary>
    public void SimulateSubscriberDrop() => _dropped.TrySetResult();

    public string? CurrentRunId => null;

    public bool IsRunning { get; private set; }

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        return new ValueTask<SendReceipt>(
            new SendReceipt(Guid.NewGuid().ToString("N"), inputId, DateTimeOffset.UtcNow));
    }

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by these tests.");
    }

    public IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by these tests.");
    }

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in RunMessages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }

        // Keep the subscription open until this instance is disposed (a restart disposes the previous
        // loop), the subscriber is dropped under backpressure, or the subscriber cancels — then end,
        // just as a real agent's channel closes on dispose/drop.
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = ct.Register(() => cancelled.TrySetResult());
        _ = await Task.WhenAny(_disposed.Task, _dropped.Task, cancelled.Task);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public Task StopAsync(TimeSpan? timeout = null)
    {
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _ = _disposed.TrySetResult();
        _ = _disposeEntered.TrySetResult();
        if (DisposeGate is not null)
        {
            await DisposeGate.Task;
        }
    }
}
