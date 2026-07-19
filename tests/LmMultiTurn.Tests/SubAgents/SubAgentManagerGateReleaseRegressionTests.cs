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

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Regression tests for the concurrency-gate release invariants around
/// <see cref="SubAgentManager.SpawnAsync"/>, <c>RestartRunAsync</c>, and
/// <see cref="SubAgentManager"/>'s per-sub-agent monitor loop:
/// <list type="bullet">
/// <item><description>F1/F2: a spawn/restart whose own <c>SendAsync</c> fails AFTER the monitor
/// has already started must release the concurrency slot exactly once - not zero times (a
/// stuck slot) and not twice (a corrupted semaphore count) - even when an earlier epoch's
/// monitor is still "in flight" (blocked in its subscription, as a real agent's stays after a
/// single run completes) at the moment of the failure. See <see cref="GateReleaseGuard"/>.</description></item>
/// <item><description>F3: a completed sub-agent's concurrency slot must be released BEFORE its
/// (possibly slow/backpressured) parent relay, so a blocked relay never holds up a fresh
/// spawn.</description></item>
/// <item><description>F5: if the monitor's subscription fails outright with a non-cancellation
/// exception, the sub-agent's completion latch must be faulted (not left to hang
/// forever).</description></item>
/// </list>
/// These scenarios are not organically reachable through the real <c>MultiTurnAgentLoop</c>
/// pipeline (every turn-execution exception it raises is already converted to a normal
/// <c>RunCompletedMessage(IsError: true)</c>), so they use
/// <see cref="SubAgentManager.TestAgentFactoryOverride"/> to substitute a
/// <see cref="FakeMultiTurnAgent"/> while still exercising the real Spawn/Restart/Monitor
/// plumbing (real gate acquisition, real monitor task).
/// </summary>
public class SubAgentManagerGateReleaseRegressionTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        // Default parent mock: accept any SendAsync call immediately (individual tests
        // override this when they need to control relay timing, e.g. the F3 test).
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
    public async Task SpawnAsync_SendAsyncThrowsAfterMonitorStarts_ReleasesGateExactlyOnceAndRollsBackRegistration()
    {
        // F1: SpawnAsync starts the monitor (which subscribes) BEFORE awaiting SendAsync, so a
        // SendAsync failure happens with the monitor already owning (and about to release) the
        // gate. The fix must release exactly once - verified indirectly, since a genuine
        // double-release would either throw SemaphoreFullException or silently let more than
        // MaxConcurrentSubAgents run concurrently - by filling capacity afterward.
        const int maxConcurrent = 2;
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["throws-on-send"] = DummyTemplate("throws-on-send"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);
        _manager.TestAgentFactoryOverride = (_, template) => new FakeMultiTurnAgent
        {
            SendImpl = template.Name == "throws-on-send"
                ? _ => ValueTask.FromException<SendReceipt>(new InvalidOperationException("send failed"))
                : null,
        };

        var act = () => _manager.SpawnAsync(
            "throws-on-send", "task", name: "failed-spawn", runInBackground: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("send failed");

        // The failed spawn's partial registration must be rolled back: its name no longer
        // resolves to anything.
        var sendToRolledBack = () => _manager.SendMessageAsync("failed-spawn", "x");
        await sendToRolledBack.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unknown sub-agent*failed-spawn*");

        // Fill capacity with agents that never complete (holding their slots forever) to prove
        // exactly maxConcurrent slots are free - not fewer (stuck slot) and not more
        // (corrupted/over-released count would let this loop run past capacity too).
        for (var i = 0; i < maxConcurrent; i++)
        {
            var json = await _manager.SpawnAsync("normal", $"filler-{i}", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
        }

        var overCapacity = () => _manager.SpawnAsync("normal", "one-too-many", runInBackground: true);
        await overCapacity.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Max concurrent sub-agents*");
    }

    [Fact]
    public async Task RestartRunAsync_SendAsyncThrowsAfterMonitorStarts_ReleasesGateExactlyOnceEvenWhilePriorEpochMonitorStillInFlight()
    {
        // F2: reproduces the exact race the GateReleaseGuard redesign fixes. Epoch 1 completes
        // (releasing its slot) but - like a real agent - its monitor's subscription stays open
        // and blocked afterward, not torn down. SendMessageAsync then restarts the agent
        // (epoch 2); RestartRunAsync's own SendAsync fails after epoch 2's monitor has already
        // started, while epoch 1's monitor is still in flight, blocked, waiting to be
        // cancelled+awaited. A shared/reset-in-place guard would let epoch 1's late release
        // (fired only once RestartRunAsync cancels+awaits it) spuriously consume epoch 2's
        // slot; independent per-epoch guards cannot be confused this way.
        const int maxConcurrent = 2;
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["restartable"] = DummyTemplate("restartable"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);

        var fake = new FakeMultiTurnAgent
        {
            SendImpl = callIndex => callIndex == 1
                ? new ValueTask<SendReceipt>(new SendReceipt("r1", null, DateTimeOffset.UtcNow))
                : ValueTask.FromException<SendReceipt>(new InvalidOperationException("restart send failed")),
            SubscribeImpl = (callIndex, ct) => callIndex == 1
                // Epoch 1: completes immediately, then - like a real agent - keeps its
                // subscription open/blocked until cancelled.
                ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                // Epoch 2: never gets a chance to complete (SendAsync throws first); just
                // waits to be cancelled during the restart-failure cleanup.
                : FakeMultiTurnAgent.WaitForeverStream(ct),
        };

        _manager.TestAgentFactoryOverride = (_, template) => template.Name == "restartable"
            ? fake
            : new FakeMultiTurnAgent();

        var spawnJson = await _manager.SpawnAsync(
            "restartable", "initial task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait for epoch 1 to settle "completed" - its gateGuard has already released its slot
        // by this point (F3 order: release happens before HandleRunCompletionAsync), while its
        // monitor is still blocked in CompleteOnceThenWaitForeverStream's tail wait.
        await WaitForConditionAsync(
            () =>
            {
                try { return _manager!.Peek(agentId).Contains("\"completed\""); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));

        // Act: SendMessageAsync on the completed agent goes through RestartRunAsync, whose own
        // SendAsync (the fake's 2nd call) throws after epoch 2's monitor has already started.
        var act = () => _manager.SendMessageAsync(agentId, "continue", runInBackground: true);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("restart send failed");

        // Assert: the gate is back to exactly full capacity - one slot from epoch 1's
        // completion, one from epoch 2's restart-failure cleanup - not corrupted upward by a
        // spurious extra release from epoch 1's monitor exiting (during RestartRunAsync's
        // await) after epoch 2 already started.
        for (var i = 0; i < maxConcurrent; i++)
        {
            var json = await _manager.SpawnAsync("normal", $"filler-{i}", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
        }

        var overCapacity = () => _manager.SpawnAsync("normal", "one-too-many", runInBackground: true);
        await overCapacity.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Max concurrent sub-agents*");
    }

    [Fact]
    public async Task MonitorSubAgentAsync_ReleasesGateBeforeParentRelay_SoBackpressuredRelayDoesNotBlockNewSpawns()
    {
        // F3: a completed sub-agent's parent relay (SendToParentAsync) is deliberately blocked;
        // a fresh spawn must still be able to acquire the (only) concurrency slot, proving the
        // slot is released BEFORE - not after - the relay.
        const int maxConcurrent = 1;
        var relayEntered = new TaskCompletionSource<bool>();
        var relayRelease = new TaskCompletionSource<bool>();

        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns<List<IMessage>, string?, string?, CancellationToken>(async (_, _, _, ct) =>
            {
                _ = relayEntered.TrySetResult(true);
                await relayRelease.Task.WaitAsync(ct);
                return new SendReceipt("relayed", null, DateTimeOffset.UtcNow);
            });

        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["completing"] = DummyTemplate("completing"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);
        _manager.TestAgentFactoryOverride = (_, template) => template.Name == "completing"
            ? new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, ct) => FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct),
            }
            : new FakeMultiTurnAgent();

        await _manager.SpawnAsync("completing", "task", runInBackground: true);

        // Wait until the completed sub-agent's parent relay is in flight (blocked).
        (await relayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        // Act: a fresh spawn must succeed even while the first agent's relay is still blocked.
        var secondSpawnJson = await _manager
            .SpawnAsync("normal", "second task", runInBackground: true)
            .WaitAsync(TimeSpan.FromSeconds(10));

        using var doc = JsonDocument.Parse(secondSpawnJson);
        doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");

        relayRelease.SetResult(true);
    }

    [Fact]
    public async Task MonitorSubAgentAsync_SubscribeThrowsNonCancellationException_FaultsCompletionLatch()
    {
        // F5: if SubscribeAsync fails outright with a non-cancellation exception, the monitor's
        // generic terminal catch must fault state.Completion, or ObserveCompletionAsync hangs
        // forever. Guarded by a timeout so a regression here hangs this one test, not the suite.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["broken"] = DummyTemplate("broken"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 5, templates);
        var thrown = new InvalidOperationException("subscribe blew up");
        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent
        {
            SubscribeImpl = (_, _) => FakeMultiTurnAgent.ThrowingStream(thrown),
        };

        var spawnJson = await _manager.SpawnAsync("broken", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        var act = () => _manager!.ObserveCompletionAsync(agentId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("subscribe blew up");
    }

    [Fact]
    public async Task HandleRunCompletion_PendingMessageCompletion_DefersOwnedProviderDisposalUntilTerminalCompletion()
    {
        // The HasPendingMessages branch (SubAgentManager.HandleRunCompletionAsync) must NOT treat a
        // pending (non-terminal) completion as terminal: it must leave the completion latch unresolved
        // and the owned provider undisposed, and only the following terminal completion disposes the
        // provider — exactly once. Without direct coverage this branch could regress while the rest of
        // the suite stays green.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var disposeCount = 0;
        var provider = new Mock<IStreamingAgent>();
        provider.As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync())
            .Returns(() =>
            {
                _ = Interlocked.Increment(ref disposeCount);
                return ValueTask.CompletedTask;
            });

        var pendingEmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent
        {
            SubscribeImpl = (_, ct) => FakeMultiTurnAgent.PendingThenTerminalStream(
                "run-1", pendingEmitted, releaseTerminal.Task, ct),
        };
        _manager.TestOwnedProviderOverride = (_, _) => provider.Object;

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // After the pending (non-terminal) completion: the owned provider must remain undisposed and
        // the sub-agent must still read as running.
        (await pendingEmitted.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await Task.Delay(150); // give HandleRunCompletionAsync time to (wrongly) dispose if it regressed
        Volatile.Read(ref disposeCount).Should()
            .Be(0, "a HasPendingMessages completion must not dispose the owned provider");
        _manager.Peek(agentId).Should().Contain("\"running\"");

        // The terminal completion disposes the owned provider exactly once.
        releaseTerminal.SetResult(true);
        await WaitForConditionAsync(() => Volatile.Read(ref disposeCount) == 1, TimeSpan.FromSeconds(10));
        Volatile.Read(ref disposeCount).Should().Be(1, "the terminal completion disposes the owned provider once");
    }

    [Fact]
    public async Task RestartRunAsync_AfterFailedTerminalDisposal_RebuildsFreshProviderInsteadOfReusingPoisoned()
    {
        // Blocker A: if a terminal owned-provider disposal THROWS, the provider may be partially torn
        // down. A later continuation must NOT reuse it — the restart path must rebuild a fresh provider.
        // A failed disposal resets the dispose guard (HasDisposedOwnedProviderAgent == false), so without
        // the poison flag the rebuild branch would be skipped and the partially-disposed provider reused.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        // Agent #1 completes once (triggering terminal disposal), then keeps its subscription open; the
        // restarted agent #2 just waits so the resumed run stays alive for assertions.
        var agentCallCount = 0;
        _manager.TestAgentFactoryOverride = (_, _) =>
        {
            var idx = Interlocked.Increment(ref agentCallCount);
            return new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, ct) => idx == 1
                    ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                    : FakeMultiTurnAgent.WaitForeverStream(ct),
            };
        };

        // Provider #1's terminal disposal throws the FIRST time (poison), then SUCCEEDS on the restart
        // retry; provider #2 is the fresh rebuild. Failing-once-then-succeeding lets us assert the retry
        // disposal path actually runs (a second attempt) rather than just that a replacement was created.
        var providerCallCount = 0;
        var poisonedDisposeAttempts = 0;
        _manager.TestOwnedProviderOverride = (_, _) =>
        {
            var idx = Interlocked.Increment(ref providerCallCount);
            if (idx == 1)
            {
                var poisoned = new Mock<IStreamingAgent>();
                poisoned.As<IAsyncDisposable>()
                    .Setup(d => d.DisposeAsync())
                    .Returns(() =>
                    {
                        var attempt = Interlocked.Increment(ref poisonedDisposeAttempts);
                        return attempt == 1
                            ? ValueTask.FromException(new InvalidOperationException("dispose failed"))
                            : ValueTask.CompletedTask;
                    });
                return poisoned.Object;
            }

            return new Mock<IStreamingAgent>().Object;
        };

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait until the first run has completed (its terminal disposal ran and threw).
        await WaitForConditionAsync(
            () =>
            {
                try { return _manager!.Peek(agentId).Contains("\"completed\""); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));
        Volatile.Read(ref poisonedDisposeAttempts).Should().Be(1,
            "the terminal disposal must have attempted (and failed) to dispose provider #1 exactly once");
        providerCallCount.Should().Be(1, "only the initial provider exists before the continuation");

        // Act: a continuation restarts the finished run. Because provider #1's terminal disposal FAILED,
        // the restart must rebuild a fresh provider (call #2), never reuse the poisoned instance.
        _ = await _manager.SendMessageAsync(agentId, "continue", runInBackground: true);

        providerCallCount.Should().Be(2, "the poisoned provider must be replaced with a fresh one on restart");
        Volatile.Read(ref poisonedDisposeAttempts).Should().Be(2,
            "the restart must RETRY disposing the poisoned provider before swapping in the replacement");
        _manager.Peek(agentId).Should().Contain("\"running\"", "the resumed run is live on the fresh provider");
    }

    [Fact]
    public async Task SendMessageAsync_InjectCancelledByTerminalDisposal_RedeliversPromptToRestartedRun()
    {
        // Blocker (round 4): the inject send links the caller token with the run's lifecycle token, and
        // terminal disposal cancels that token. This must NOT surface as a caller cancellation that drops
        // the user's message — the continuation must re-enter the decision loop and deliver the prompt to
        // the restarted run. Exercised through the REAL SubAgentManager.SendMessageAsync boundary (not the
        // SubAgentState primitive directly), so it fails if the manager stops using the linked token.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var sentSink = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var injectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCallCount = 0;

        _manager.TestAgentFactoryOverride = (_, _) =>
        {
            var which = Interlocked.Increment(ref agentCallCount);
            if (which != 1)
            {
                // Agent #2 (the restart) stays running and uses the default succeeding send.
                return new FakeMultiTurnAgent
                {
                    SentSink = sentSink,
                    SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct),
                };
            }

            // Agent #1: spawn send (call 1) succeeds; the inject (call >= 2) signals it is in flight then
            // BLOCKS on its token — the manager's linked lifecycle token — until terminal disposal cancels
            // it. Its run completes terminally only AFTER the inject is in flight, so the lease is held when
            // terminal disposal runs and the lifecycle-cancel path is exercised end to end.
            return new FakeMultiTurnAgent
            {
                SentSink = sentSink,
                SendWithTokenImpl = async (idx, sendCt) =>
                {
                    if (idx >= 2)
                    {
                        _ = injectStarted.TrySetResult(true);
                        await Task.Delay(Timeout.InfiniteTimeSpan, sendCt);
                    }

                    return new SendReceipt(Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow);
                },
                SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitThenCompleteStream(injectStarted.Task, "run-1", ct),
            };
        };

        // Owned provider disposes cleanly at terminal completion, so HasDisposedOwnedProviderAgent drives
        // the restart rebuild.
        _manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Act: a continuation whose caller token is non-cancelable. Its inject send blocks, terminal
        // completion lands and lifecycle-cancels it, and the manager must restart and deliver the prompt
        // — returning normally rather than throwing OperationCanceledException.
        var resultJson = await _manager
            .SendMessageAsync(agentId, "resumed-prompt", runInBackground: true)
            .WaitAsync(TimeSpan.FromSeconds(15));

        using var resultDoc = JsonDocument.Parse(resultJson);
        resultDoc.RootElement.GetProperty("status").GetString().Should().Be("resumed",
            "the lifecycle-cancelled inject must be re-driven through the restart path, not surfaced as cancellation");
        agentCallCount.Should().Be(2, "the finished run must be restarted on a fresh agent");
        sentSink.Should().Contain("resumed-prompt",
            "the user's prompt must reach the restarted run rather than being dropped on lifecycle cancellation");
    }

    [Fact]
    public async Task SendMessageAsync_InjectSendThrowsInternalCancellation_PropagatesWithoutRetry()
    {
        // Round-5 blocker: the inject catch must treat ONLY lifecycle-token cancellation as "retry via
        // restart". An internal OperationCanceledException from Agent.SendAsync (e.g. its own timeout) with
        // NEITHER the caller token NOR the linked lifecycle token cancelled must PROPAGATE, not be retried —
        // otherwise the manager risks duplicate delivery or an unbounded retry loop.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var sendCount = 0;
        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent
        {
            // Spawn send (call 1) succeeds; the inject (call 2) throws an INTERNAL cancellation unrelated
            // to either supplied token.
            SendImpl = _ =>
            {
                var n = Interlocked.Increment(ref sendCount);
                return n == 1
                    ? new ValueTask<SendReceipt>(new SendReceipt("r1", null, DateTimeOffset.UtcNow))
                    : ValueTask.FromException<SendReceipt>(new OperationCanceledException("internal send timeout"));
            },
            SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct),
        };

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // The internal OCE must surface to the caller, not be swallowed-and-retried.
        var act = () => _manager!.SendMessageAsync(agentId, "resumed-prompt", runInBackground: true);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Exactly the spawn send + the single failed inject attempt — no restart / re-send.
        Volatile.Read(ref sendCount).Should().Be(2, "an internal SendAsync cancellation must not be retried");
    }

    [Fact]
    public async Task RestartRunAsync_RestartedMonitorFaultsBeforeArmRunning_DoesNotResurrectRunning()
    {
        // Round-6 blocker: a restarted run's monitor can fault BEFORE RestartRunAsync's TryArmRunning
        // executes. The monitor's fault path must record a GENERATION-AWARE terminal Error (not a raw
        // Status write), so TryArmRunning observes this generation's terminal and refuses to overwrite
        // Error with Running — which would advertise a dead run. Synchronized so it deterministically hits
        // the fault-before-arm ordering.
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["owned"] = DummyTemplate("owned"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var restartSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCallCount = 0;
        _manager.TestAgentFactoryOverride = (_, _) =>
        {
            var which = Interlocked.Increment(ref agentCallCount);
            if (which == 1)
            {
                // Agent #1 completes once (becomes restartable), then keeps its subscription open.
                return new FakeMultiTurnAgent
                {
                    SubscribeImpl = (_, ct) => FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct),
                };
            }

            // Agent #2 (the restart): its monitor faults immediately; its restart SendAsync blocks on the
            // gate so the test can confirm the fault was recorded (status Error) BEFORE TryArmRunning runs.
            return new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, _) =>
                    FakeMultiTurnAgent.ThrowingStream(new InvalidOperationException("restarted monitor blew up")),
                SendWithTokenImpl = async (_, sendCt) =>
                {
                    await restartSendGate.Task.WaitAsync(sendCt);
                    return new SendReceipt("restart-send", null, DateTimeOffset.UtcNow);
                },
            };
        };

        // Owned provider disposes cleanly at agent #1's terminal, so the restart rebuilds (agent #2).
        _manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        await WaitForConditionAsync(
            () => { try { return _manager!.Peek(agentId).Contains("\"completed\""); } catch { return false; } },
            TimeSpan.FromSeconds(10));

        // Begin the restart on a background task; its restart SendAsync blocks on the gate.
        var restartTask = Task.Run(() => _manager!.SendMessageAsync(agentId, "resumed-prompt", runInBackground: true));

        // The restarted monitor faults and records the generation-aware terminal Error.
        await WaitForConditionAsync(
            () => { try { return _manager!.Peek(agentId).Contains("\"error\""); } catch { return false; } },
            TimeSpan.FromSeconds(10));

        // Now let the restart SendAsync return so TryArmRunning(runGeneration) executes AFTER the fault.
        restartSendGate.SetResult(true);
        _ = await restartTask.WaitAsync(TimeSpan.FromSeconds(10));

        // TryArmRunning must NOT resurrect the faulted run: status stays Error, never Running.
        _manager.Peek(agentId).Should().Contain("\"error\"",
            "a monitor fault recorded against the run generation must block TryArmRunning from restoring Running");
        _manager.Peek(agentId).Should().NotContain("\"running\"");
    }

    [Fact]
    public async Task MonitorSubAgentAsync_PendingMessageCompletion_HoldsConcurrencyPermitUntilTerminal()
    {
        // Blocker D: with limit 1, a nonterminal (HasPendingMessages) completion must NOT release the
        // concurrency permit — the same sub-agent keeps processing queued work. Releasing early would let
        // a second sub-agent start, exceeding MaxConcurrentSubAgents. The permit is freed only on the
        // TERMINAL completion.
        const int maxConcurrent = 1;
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["pending"] = DummyTemplate("pending"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);

        var pendingEmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _manager.TestAgentFactoryOverride = (_, template) => template.Name == "pending"
            ? new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, ct) => FakeMultiTurnAgent.PendingThenTerminalStream(
                    "run-1", pendingEmitted, releaseTerminal.Task, ct),
            }
            : new FakeMultiTurnAgent();

        var spawnJson = await _manager.SpawnAsync("pending", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var pendingAgentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // After the pending completion is processed, the permit must still be held.
        (await pendingEmitted.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        await Task.Delay(150); // give the monitor time to (wrongly) release the permit if it regressed

        var whileBusy = () => _manager!.SpawnAsync("normal", "should-not-start", runInBackground: true);
        await whileBusy.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Max concurrent sub-agents*",
                "a pending (nonterminal) completion must not free the slot while the sub-agent is still active");

        // The terminal completion frees the slot: wait for the busy agent to settle terminal, then a
        // single fresh spawn must succeed on the now-released slot.
        releaseTerminal.SetResult(true);
        await WaitForConditionAsync(
            () =>
            {
                try { return _manager!.Peek(pendingAgentId).Contains("\"completed\""); }
                catch { return false; }
            },
            TimeSpan.FromSeconds(10));

        var afterTerminalJson = await _manager
            .SpawnAsync("normal", "after-terminal", runInBackground: true)
            .WaitAsync(TimeSpan.FromSeconds(10));
        using var afterDoc = JsonDocument.Parse(afterTerminalJson);
        afterDoc.RootElement.GetProperty("status").GetString().Should().Be("spawned",
            "the terminal completion released the slot, so a new sub-agent can start");
    }

    #region Helpers

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// A minimal template whose <see cref="SubAgentTemplate.AgentFactory"/> is never actually
    /// invoked (bypassed by <see cref="SubAgentManager.TestAgentFactoryOverride"/>); its
    /// <see cref="SubAgentTemplate.Name"/> lets a shared override delegate distinguish which
    /// template a given spawn used.
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

    private SubAgentManager CreateManagerWithTemplates(
        int maxConcurrent,
        IReadOnlyDictionary<string, SubAgentTemplate> templates)
    {
        var options = new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = maxConcurrent,
        };

        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
    }

    #endregion
}

/// <summary>
/// Minimal <see cref="IMultiTurnAgent"/> test double for exercising
/// <see cref="SubAgentManager"/>'s real Spawn/Restart/Monitor plumbing (real gate acquisition,
/// real monitor task) with fully controllable Send/Subscribe behavior per call - needed for
/// scenarios a real <c>MultiTurnAgentLoop</c> cannot organically reproduce (a background
/// <c>SendAsync</c> failing after the monitor already started; a monitor's
/// <c>SubscribeAsync</c> failing outright with a non-cancellation exception).
/// </summary>
internal sealed class FakeMultiTurnAgent : IMultiTurnAgent
{
    private int _sendCallCount;
    private int _subscribeCallCount;

    public string? CurrentRunId => null;

    public string ThreadId { get; init; } = "fake-thread";

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Invoked for each <see cref="SendAsync"/> call with a 1-based call index, so a test can
    /// make e.g. only a restart's send (call #2) fail. Null (default) =&gt; every call
    /// succeeds with a fresh receipt.
    /// </summary>
    public Func<int, ValueTask<SendReceipt>>? SendImpl { get; set; }

    /// <summary>
    /// Token-aware variant of <see cref="SendImpl"/> (takes precedence when set): receives the
    /// send's <see cref="CancellationToken"/> so a test can block on it and prove terminal
    /// disposal cancels a wedged inject send through the manager's linked lifecycle token.
    /// </summary>
    public Func<int, CancellationToken, ValueTask<SendReceipt>>? SendWithTokenImpl { get; set; }

    /// <summary>
    /// Optional sink recording the first user text of every <see cref="SendAsync"/> call — shared
    /// across agent instances (e.g. a restart's replacement agent) so a test can assert a prompt
    /// reached the restarted run.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<string>? SentSink { get; init; }

    /// <summary>
    /// Invoked for each <see cref="SubscribeAsync"/> call with a 1-based call index, so a test
    /// can give a later restart's monitor different behavior than the original epoch's. Null
    /// (default) =&gt; <see cref="WaitForeverStream"/>.
    /// </summary>
    public Func<int, CancellationToken, IAsyncEnumerable<IMessage>>? SubscribeImpl { get; set; }

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        SentSink?.Enqueue(messages.OfType<TextMessage>().Select(m => m.Text).FirstOrDefault() ?? string.Empty);
        var callIndex = Interlocked.Increment(ref _sendCallCount);
        if (SendWithTokenImpl != null)
        {
            return SendWithTokenImpl(callIndex, ct);
        }

        return SendImpl != null
            ? SendImpl(callIndex)
            : new ValueTask<SendReceipt>(
                new SendReceipt(Guid.NewGuid().ToString("N"), inputId, DateTimeOffset.UtcNow));
    }

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by SubAgentManager or these tests.");
    }

    public IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by SubAgentManager or these tests.");
    }

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default)
    {
        var callIndex = Interlocked.Increment(ref _subscribeCallCount);
        return SubscribeImpl != null
            ? SubscribeImpl(callIndex, ct)
            : WaitForeverStream(ct);
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

    public Task<RunCancellationResult> CancelCurrentRunAsync(string expectedRunId, CancellationToken ct = default)
    {
        return Task.FromResult(RunCancellationResult.NoActiveRun);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stream that never produces a message and only ends when cancelled - mirrors a real
    /// sub-agent's subscription staying open/blocked after its background loop has nothing left
    /// to do (the loop and subscriber channels stay alive across individual run completions;
    /// only explicit cancellation/disposal closes them).
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> WaitForeverStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    /// <summary>
    /// Stream that waits for <paramref name="gate"/> (e.g. "an inject send is now in flight"), then yields
    /// a single TERMINAL <see cref="RunCompletedMessage"/> and keeps the subscription open. Lets a test
    /// drive a terminal completion to land WHILE an admitted inject send is blocked, so the manager's
    /// lifecycle-token cancellation + continuation-restart path is exercised end to end.
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> WaitThenCompleteStream(
        Task gate,
        string completedRunId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        yield return new RunCompletedMessage { CompletedRunId = completedRunId };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>
    /// Stream that yields one <see cref="RunCompletedMessage"/> immediately, then keeps the
    /// subscription open/blocked exactly like a real agent's would after a single run finishes
    /// - needed to faithfully reproduce the "old monitor still in flight when a restart
    /// happens" timing the <see cref="GateReleaseGuard"/> fix targets.
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> CompleteOnceThenWaitForeverStream(
        string completedRunId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new RunCompletedMessage { CompletedRunId = completedRunId };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>
    /// Stream that yields a NON-terminal <see cref="RunCompletedMessage"/> (HasPendingMessages =
    /// true), signals <paramref name="pendingEmitted"/>, waits for <paramref name="releaseTerminal"/>,
    /// then yields the terminal completion and keeps the subscription open — lets a test verify that a
    /// pending completion neither resolves the latch nor disposes the owned provider, and that the
    /// following terminal completion disposes exactly once.
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> PendingThenTerminalStream(
        string completedRunId,
        TaskCompletionSource<bool> pendingEmitted,
        Task releaseTerminal,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new RunCompletedMessage { CompletedRunId = completedRunId, HasPendingMessages = true };
        _ = pendingEmitted.TrySetResult(true);
        await releaseTerminal.WaitAsync(ct);
        yield return new RunCompletedMessage { CompletedRunId = completedRunId };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>
    /// Stream that throws <paramref name="exception"/> the moment it's subscribed to, before
    /// producing any message - simulates a monitor's <c>SubscribeAsync</c> failing outright,
    /// the scenario the monitor's generic terminal catch must fault the completion latch for
    /// (F5).
    /// </summary>
    internal static IAsyncEnumerable<IMessage> ThrowingStream(Exception exception)
    {
        throw exception;
    }
}
