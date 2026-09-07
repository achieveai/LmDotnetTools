using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
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
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
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
        _manager.TestAgentFactoryOverride = (_, template) =>
            new FakeMultiTurnAgent
            {
                SendImpl =
                    template.Name == "throws-on-send"
                        ? _ => ValueTask.FromException<SendReceipt>(new InvalidOperationException("send failed"))
                        : null,
            };

        var act = () => _manager.SpawnAsync("throws-on-send", "task", name: "failed-spawn", runInBackground: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("send failed");

        // The failed spawn's partial registration must be rolled back: its name no longer
        // resolves to anything.
        var sendToRolledBack = () => _manager.SendMessageAsync("failed-spawn", "x");
        await sendToRolledBack.Should().ThrowAsync<ArgumentException>().WithMessage("*Unknown sub-agent*failed-spawn*");

        // Fill capacity with agents that never complete (holding their slots forever) to prove
        // exactly maxConcurrent slots are free - not fewer (stuck slot) and not more
        // (corrupted/over-released count would let this loop run past capacity too).
        for (var i = 0; i < maxConcurrent; i++)
        {
            var json = await _manager.SpawnAsync("normal", $"filler-{i}", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
        }

        // With the pool now exactly full, one more spawn is DEFER-QUEUED (status="queued") rather than
        // run inline. This is the current over-capacity probe: an over-released gate would instead leave
        // a free slot and return "spawned" here, so "queued" still proves the count is exactly full.
        var overCapacityJson = await _manager.SpawnAsync("normal", "one-too-many", runInBackground: true);
        using var overCapacityDoc = JsonDocument.Parse(overCapacityJson);
        overCapacityDoc.RootElement.GetProperty("status").GetString().Should().Be("queued");
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
            SendImpl = callIndex =>
                callIndex == 1
                    ? new ValueTask<SendReceipt>(new SendReceipt("r1", null, DateTimeOffset.UtcNow))
                    : ValueTask.FromException<SendReceipt>(new InvalidOperationException("restart send failed")),
            SubscribeImpl = (callIndex, ct) =>
                callIndex == 1
                    // Epoch 1: completes immediately, then - like a real agent - keeps its
                    // subscription open/blocked until cancelled.
                    ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                    // Epoch 2: never gets a chance to complete (SendAsync throws first); just
                    // waits to be cancelled during the restart-failure cleanup.
                    : FakeMultiTurnAgent.WaitForeverStream(ct),
        };

        _manager.TestAgentFactoryOverride = (_, template) =>
            template.Name == "restartable" ? fake : new FakeMultiTurnAgent();

        var spawnJson = await _manager.SpawnAsync("restartable", "initial task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Wait for epoch 1 to settle "completed" - its gateGuard has already released its slot
        // by this point (F3 order: release happens before HandleRunCompletionAsync), while its
        // monitor is still blocked in CompleteOnceThenWaitForeverStream's tail wait.
        await Wait.UntilAsync(
            () =>
            {
                // TryPeek returns false only for the not-yet-registered case; any real fault
                // (a disposed manager, an NRE while serializing status) now propagates and surfaces
                // on timeout instead of being swallowed into an indistinguishable "not yet" (#403).
                return _manager!.TryPeek(agentId, out var status)
                    && status.Contains("\"completed\"", StringComparison.Ordinal);
            },
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );

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

        // Pool exactly full -> one more spawn defer-queues ("queued"); a corrupted upward count would
        // leave a slot free and return "spawned" instead.
        var overCapacityJson = await _manager.SpawnAsync("normal", "one-too-many", runInBackground: true);
        using var overCapacityDoc = JsonDocument.Parse(overCapacityJson);
        overCapacityDoc.RootElement.GetProperty("status").GetString().Should().Be("queued");
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
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<List<IMessage>, string?, string?, CancellationToken>(
                async (_, _, _, ct) =>
                {
                    _ = relayEntered.TrySetResult(true);
                    await relayRelease.Task.WaitAsync(ct);
                    return new SendReceipt("relayed", null, DateTimeOffset.UtcNow);
                }
            );

        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["completing"] = DummyTemplate("completing"),
            ["normal"] = DummyTemplate("normal"),
        };

        _manager = CreateManagerWithTemplates(maxConcurrent, templates);
        _manager.TestAgentFactoryOverride = (_, template) =>
            template.Name == "completing"
                ? new FakeMultiTurnAgent
                {
                    SubscribeImpl = (_, ct) => FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct),
                }
                : new FakeMultiTurnAgent();

        await _manager.SpawnAsync("completing", "task", runInBackground: true);

        // Wait until the completed sub-agent's parent relay is in flight (blocked).
        (await relayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .BeTrue();

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
        var templates = new Dictionary<string, SubAgentTemplate> { ["broken"] = DummyTemplate("broken") };

        _manager = CreateManagerWithTemplates(maxConcurrent: 5, templates);
        var thrown = new InvalidOperationException("subscribe blew up");
        _manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent { SubscribeImpl = (_, _) => FakeMultiTurnAgent.ThrowingStream(thrown) };

        var spawnJson = await _manager.SpawnAsync("broken", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        var act = () =>
            _manager!.ObserveCompletionAsync(agentId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("subscribe blew up");
    }

    [Fact]
    public async Task HandleRunCompletion_KeepsOwnedProviderAliveAcrossCompletions_DisposingItOnceAtShutdown()
    {
        // Owned-provider lifetime is the LOOP's, not an individual run's. Two claims, one arrangement:
        //   1. A pending (HasPendingMessages) completion is not terminal at all: the latch stays
        //      unresolved, the status stays Running, the provider is untouched. (Unchanged invariant.)
        //   2. The following TERMINAL completion settles the sub-agent but STILL must not dispose the
        //      owned provider — the loop stays reusable, so a continuation injects into (or restarts on)
        //      the same live pipeline. (Contract change: this test previously asserted the terminal
        //      completion disposed the provider, back when completion owned provider lifetime.)
        // The provider is torn down exactly once, at manager shutdown.
        var templates = new Dictionary<string, SubAgentTemplate> { ["owned"] = DummyTemplate("owned") };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var disposeCount = 0;
        var provider = new Mock<IStreamingAgent>();
        provider
            .As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync())
            .Returns(() =>
            {
                _ = Interlocked.Increment(ref disposeCount);
                return ValueTask.CompletedTask;
            });

        var pendingEmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, ct) =>
                    FakeMultiTurnAgent.PendingThenTerminalStream("run-1", pendingEmitted, releaseTerminal.Task, ct),
            };
        _manager.TestOwnedProviderOverride = (_, _) => provider.Object;

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // After the pending (non-terminal) completion: the owned provider must remain undisposed and
        // the sub-agent must still read as running.
        (await pendingEmitted.Task.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .BeTrue();
        await Task.Delay(150); // give HandleRunCompletionAsync time to (wrongly) dispose if it regressed
        Volatile
            .Read(ref disposeCount)
            .Should()
            .Be(0, "a HasPendingMessages completion must not dispose the owned provider");
        _manager.Peek(agentId).Should().Contain("\"running\"");

        // The TERMINAL completion settles the sub-agent — waiting on the published status is what makes
        // the assertion below non-vacuous, since it proves the terminal branch actually ran — and must
        // still leave the owned provider alive for the reusable loop.
        releaseTerminal.SetResult(true);
        await Wait.UntilAsync(
            () =>
                _manager!.TryPeek(agentId, out var status)
                && JsonDocument.Parse(status).RootElement.GetProperty("status").GetString() == "completed",
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );
        Volatile
            .Read(ref disposeCount)
            .Should()
            .Be(
                0,
                "provider ownership follows the loop, not the run: a terminal completion leaves the loop "
                    + "reusable, so disposing its provider here would break the very next continuation"
            );

        // Shutdown is the one thing that ends the loop, so it is the one thing that disposes the
        // provider — exactly once, not once per completed run.
        await _manager.DisposeAsync();
        Volatile.Read(ref disposeCount).Should().Be(1, "manager shutdown disposes the owned provider exactly once");
    }

    [Fact]
    public async Task RestartRunAsync_AfterAFailedRestartLeftAnUndisposedProvider_RetriesDisposalAndRebuildsInsteadOfLeakingIt()
    {
        // Blocker A, re-aimed at the loop-lifetime provider policy. A normal completion no longer
        // disposes the owned provider, so the "poisoned at terminal disposal" trigger this test used to
        // arrange no longer exists. The hazard it guarded does: the RESTART-FAILURE cleanup
        // (RestartRunAsync's catch) disposes the loop, marks it disposed, and disposes the owned
        // provider — and when THAT disposal throws, the dispose guard resets to Idle, leaving a DISPOSED
        // LOOP owning a LIVE provider. The next continuation must rebuild the pipeline (the loop is
        // dead) AND retry disposing the still-live provider before overwriting its slot; skipping the
        // retry silently leaks it, because the handle is gone once the slot is replaced.
        //
        // Also pins the contract change itself: an ordinary completion must NOT rebuild anything.
        var templates = new Dictionary<string, SubAgentTemplate> { ["owned"] = DummyTemplate("owned") };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        // Agent #1 completes once (so the follow-up is a restart, not an inject), keeps its subscription
        // open on later epochs, and fails the RESTART's own send (call >= 2) so RestartRunAsync enters
        // its failure cleanup. Agent #2 is the rebuild and just waits, so the resumed run stays alive.
        var agentCallCount = 0;
        _manager.TestAgentFactoryOverride = (_, _) =>
        {
            var idx = Interlocked.Increment(ref agentCallCount);
            return idx == 1
                ? new FakeMultiTurnAgent
                {
                    SubscribeImpl = (callIndex, ct) =>
                        callIndex == 1
                            ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                            : FakeMultiTurnAgent.WaitForeverStream(ct),
                    SendImpl = callIndex =>
                        callIndex == 1
                            ? new ValueTask<SendReceipt>(new SendReceipt("r1", null, DateTimeOffset.UtcNow))
                            : ValueTask.FromException<SendReceipt>(
                                new InvalidOperationException("restart send failed")
                            ),
                }
                : new FakeMultiTurnAgent { SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct) };
        };

        // Provider #1's disposal throws the FIRST time (the restart-failure cleanup swallows it, leaving
        // the provider live behind a disposed loop) and SUCCEEDS on the rebuild's retry; provider #2 is
        // the fresh replacement. Failing-once-then-succeeding is what lets the test assert the retry
        // disposal actually RAN (a second attempt) rather than merely that a replacement was created.
        var providerCallCount = 0;
        var poisonedDisposeAttempts = 0;
        _manager.TestOwnedProviderOverride = (_, _) =>
        {
            var idx = Interlocked.Increment(ref providerCallCount);
            if (idx == 1)
            {
                var poisoned = new Mock<IStreamingAgent>();
                poisoned
                    .As<IAsyncDisposable>()
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

        await Wait.UntilAsync(
            () =>
            {
                // TryPeek returns false only for the not-yet-registered case; any real fault
                // (a disposed manager, an NRE while serializing status) now propagates and surfaces
                // on timeout instead of being swallowed into an indistinguishable "not yet" (#403).
                return _manager!.TryPeek(agentId, out var status)
                    && status.Contains("\"completed\"", StringComparison.Ordinal);
            },
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );
        Volatile
            .Read(ref poisonedDisposeAttempts)
            .Should()
            .Be(0, "a completed run leaves its loop reusable, so completion must not touch the owned provider");

        // First continuation: no rebuild trigger exists yet, so the restart REUSES the live loop — and
        // that reused loop's restart send fails, driving the cleanup that disposes the loop and (fails
        // to) dispose the provider.
        var failingRestart = () => _manager!.SendMessageAsync(agentId, "continue", runInBackground: true);
        await failingRestart.Should().ThrowAsync<InvalidOperationException>().WithMessage("restart send failed");

        agentCallCount
            .Should()
            .Be(1, "a completion alone must not rebuild the pipeline — the restart reuses the live loop");
        providerCallCount.Should().Be(1, "and therefore must not build a second provider either");
        Volatile
            .Read(ref poisonedDisposeAttempts)
            .Should()
            .Be(1, "the restart-failure cleanup must have attempted (and failed) to dispose the owned provider once");

        // Second continuation: the loop is disposed, so this one MUST rebuild — and must retry the
        // still-pending provider disposal before overwriting the slot.
        _ = await _manager.SendMessageAsync(agentId, "continue-again", runInBackground: true);

        agentCallCount.Should().Be(2, "a restart onto a disposed loop must rebuild it, never send into it");
        providerCallCount.Should().Be(2, "the rebuilt loop gets a fresh provider");
        Volatile
            .Read(ref poisonedDisposeAttempts)
            .Should()
            .Be(
                2,
                "the rebuild must RETRY disposing the still-live provider of the disposed loop before "
                    + "overwriting the slot, or that handle is leaked forever"
            );
        _manager.Peek(agentId).Should().Contain("\"running\"", "the resumed run is live on the fresh pipeline");
    }

    [Fact]
    public async Task SendMessageAsync_InjectCancelledByTerminalDisposal_RedeliversPromptToRestartedRun()
    {
        // Blocker (round 4): the inject send links the caller token with the run's lifecycle token, and
        // terminal disposal cancels that token. This must NOT surface as a caller cancellation that drops
        // the user's message — the continuation must re-enter the decision loop and deliver the prompt to
        // the restarted run. Exercised through the REAL SubAgentManager.SendMessageAsync boundary (not the
        // SubAgentState primitive directly), so it fails if the manager stops using the linked token.
        var templates = new Dictionary<string, SubAgentTemplate> { ["owned"] = DummyTemplate("owned") };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var sentSink = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var injectStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCallCount = 0;

        _manager.TestAgentFactoryOverride = (_, _) =>
        {
            _ = Interlocked.Increment(ref agentCallCount);

            // Send call 1 (spawn) succeeds. Call 2 (the inject) signals it is in flight then BLOCKS on
            // its token — the manager's linked lifecycle token — until terminal disposal cancels it.
            // Call 3 is the REDELIVERY the restart performs, and must succeed. Subscribe call 1 completes
            // terminally only AFTER the inject is in flight (so the lease is held when the terminal lands
            // and the lifecycle-cancel path runs end to end); the restarted epoch's subscription just
            // stays open so the resumed run remains live.
            return new FakeMultiTurnAgent
            {
                SentSink = sentSink,
                SendWithTokenImpl = async (idx, sendCt) =>
                {
                    if (idx == 2)
                    {
                        _ = injectStarted.TrySetResult(true);
                        await Task.Delay(Timeout.InfiniteTimeSpan, sendCt);
                    }

                    return new SendReceipt(Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow);
                },
                SubscribeImpl = (callIndex, ct) =>
                    callIndex == 1
                        ? FakeMultiTurnAgent.WaitThenCompleteStream(injectStarted.Task, "run-1", ct)
                        : FakeMultiTurnAgent.WaitForeverStream(ct),
            };
        };

        // An OWNED provider is what arms the lifecycle cancel at terminal completion
        // (SubAgentState.BeginTerminalDisposalAsync cancels the lifecycle CTS only when the sub-agent
        // owns its provider and a send lease is outstanding). It is no longer disposed there, so the
        // restart below deliberately reuses this same live pipeline.
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
        resultDoc
            .RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be(
                "resumed",
                "the lifecycle-cancelled inject must be re-driven through the restart path, not surfaced as cancellation"
            );
        agentCallCount
            .Should()
            .Be(
                1,
                "the terminal completion no longer disposes the owned provider, so the restart reuses the "
                    + "live loop instead of rebuilding it"
            );
        sentSink
            .Count(text => string.Equals(text, "resumed-prompt", StringComparison.Ordinal))
            .Should()
            .Be(
                2,
                "exactly the cancelled inject attempt plus ONE redelivery through the restarted run: fewer "
                    + "means the user's prompt was dropped on lifecycle cancellation, more means it was "
                    + "delivered twice"
            );
    }

    [Fact]
    public async Task SendMessageAsync_InjectSendThrowsInternalCancellation_PropagatesWithoutRetry()
    {
        // Round-5 blocker: the inject catch must treat ONLY lifecycle-token cancellation as "retry via
        // restart". An internal OperationCanceledException from Agent.SendAsync (e.g. its own timeout) with
        // NEITHER the caller token NOR the linked lifecycle token cancelled must PROPAGATE, not be retried —
        // otherwise the manager risks duplicate delivery or an unbounded retry loop.
        var templates = new Dictionary<string, SubAgentTemplate> { ["owned"] = DummyTemplate("owned") };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var sendCount = 0;
        _manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent
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
        var templates = new Dictionary<string, SubAgentTemplate> { ["owned"] = DummyTemplate("owned") };

        _manager = CreateManagerWithTemplates(maxConcurrent: 2, templates);

        var restartSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agentCallCount = 0;
        _manager.TestAgentFactoryOverride = (_, _) =>
        {
            _ = Interlocked.Increment(ref agentCallCount);

            // Epoch 1 completes once (making the sub-agent restartable) and keeps its subscription open.
            // The RESTARTED epoch's monitor faults immediately, while the restart's own SendAsync blocks
            // on the gate so the test can confirm the fault was recorded (status Error) BEFORE
            // TryArmRunning runs. Same loop instance across both epochs: a completion no longer disposes
            // the owned provider, so the restart reuses the live pipeline rather than rebuilding it.
            return new FakeMultiTurnAgent
            {
                SubscribeImpl = (callIndex, ct) =>
                    callIndex == 1
                        ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                        : FakeMultiTurnAgent.ThrowingStream(new InvalidOperationException("restarted monitor blew up")),
                SendWithTokenImpl = async (callIndex, sendCt) =>
                {
                    if (callIndex >= 2)
                    {
                        await restartSendGate.Task.WaitAsync(sendCt);
                    }

                    return new SendReceipt($"send-{callIndex}", null, DateTimeOffset.UtcNow);
                },
            };
        };

        // An owned provider is present (so the terminal transition takes the owned-provider path), but
        // under loop-lifetime ownership it is no longer disposed at completion and therefore no longer
        // forces a rebuild.
        _manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;

        var spawnJson = await _manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Parsed and compared on the "status" property rather than a raw substring match (#359):
        // Peek's JSON is not attacker-controlled here, but a substring check on "\"completed\""
        // would equally match a status of e.g. "not_completed" or a value embedded elsewhere in the
        // payload (recent_turns text, model id) -- the exact field is what the assertion means.
        await Wait.UntilAsync(
            () =>
            {
                // TryPeek returns false only for the not-yet-registered case; a status whose JSON shape
                // regressed (a missing "status" property, a non-JSON payload) now throws out of the
                // condition and surfaces on timeout rather than being swallowed into "not yet" (#403).
                return _manager!.TryPeek(agentId, out var status)
                    && JsonDocument.Parse(status).RootElement.GetProperty("status").GetString() == "completed";
            },
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );

        // Begin the restart on a background task; its restart SendAsync blocks on the gate.
        var restartTask = Task.Run(() => _manager!.SendMessageAsync(agentId, "resumed-prompt", runInBackground: true));

        // The restarted monitor faults and records the generation-aware terminal Error.
        await Wait.UntilAsync(
            () =>
            {
                // See the #403 note above: a real JSON-shape fault propagates instead of masking.
                return _manager!.TryPeek(agentId, out var status)
                    && JsonDocument.Parse(status).RootElement.GetProperty("status").GetString() == "error";
            },
            "the restarted sub-agent reported error",
            TimeSpan.FromSeconds(10)
        );

        // Now let the restart SendAsync return so TryArmRunning(runGeneration) executes AFTER the fault.
        restartSendGate.SetResult(true);
        _ = await restartTask.WaitAsync(TimeSpan.FromSeconds(10));

        // TryArmRunning must NOT resurrect the faulted run: status stays exactly "error", never "running".
        using var finalDoc = JsonDocument.Parse(_manager.Peek(agentId));
        finalDoc
            .RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be(
                "error",
                "a monitor fault recorded against the run generation must block TryArmRunning from restoring Running"
            );
        agentCallCount
            .Should()
            .Be(1, "the restart reused the live loop, so the faulting monitor is the SAME agent's second epoch");
    }

    [Fact]
    public async Task ForegroundCancellationAfterPermitAcquisition_StopsBeforeRegistration()
    {
        var manager = CreateManagerWithTemplates(
            1,
            new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") }
        );
        _manager = manager;
        var constructed = new FakeMultiTurnAgent();
        manager.TestAgentFactoryOverride = (_, _) => constructed;
        var reachedRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TestBeforeAgentRegistrationAsync = async () =>
        {
            reachedRegistration.SetResult();
            await releaseRegistration.Task;
        };
        using var cts = new CancellationTokenSource();

        var spawn = manager.SpawnAsync("worker", "task", ct: cts.Token);
        await reachedRegistration.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        releaseRegistration.SetResult();

        var act = async () => await spawn;
        await act.Should().ThrowAsync<OperationCanceledException>();
        manager.ListAgents().Should().BeEmpty();
        constructed.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeRacingInlineSpawn_RejectsRegistrationAndDisposesConstructedAgent()
    {
        var manager = CreateManagerWithTemplates(
            1,
            new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") }
        );
        _manager = manager;
        var constructed = new FakeMultiTurnAgent();
        manager.TestAgentFactoryOverride = (_, _) => constructed;
        var reachedRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRegistration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TestBeforeAgentRegistrationAsync = async () =>
        {
            reachedRegistration.SetResult();
            await releaseRegistration.Task;
        };

        var spawn = manager.SpawnAsync("worker", "task", runInBackground: true);
        await reachedRegistration.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var dispose = manager.DisposeAsync().AsTask();
        releaseRegistration.SetResult();

        var act = async () => await spawn;
        await act.Should().ThrowAsync<ObjectDisposedException>();
        await dispose;
        manager.ListAgents().Should().BeEmpty();
        constructed.DisposeCount.Should().Be(1);
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

        _manager.TestAgentFactoryOverride = (_, template) =>
            template.Name == "pending"
                ? new FakeMultiTurnAgent
                {
                    SubscribeImpl = (_, ct) =>
                        FakeMultiTurnAgent.PendingThenTerminalStream("run-1", pendingEmitted, releaseTerminal.Task, ct),
                }
                : new FakeMultiTurnAgent();

        var spawnJson = await _manager.SpawnAsync("pending", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var pendingAgentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // After the pending completion is processed, the permit must still be held.
        (await pendingEmitted.Task.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should()
            .BeTrue();
        await Task.Delay(150); // give the monitor time to (wrongly) release the permit if it regressed

        // While the pending agent is still active (holding the only permit), a second spawn is
        // DEFER-QUEUED rather than started. This is the probe that the pending (nonterminal) completion
        // did NOT free the slot: had it wrongly released, Wait(0) would succeed and return "spawned".
        var queuedJson = await _manager!.SpawnAsync("normal", "queued-while-busy", runInBackground: true);
        using var queuedDoc = JsonDocument.Parse(queuedJson);
        queuedDoc
            .RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be(
                "queued",
                "a pending (nonterminal) completion must not free the slot while the sub-agent is still active"
            );
        var queuedAgentId = queuedDoc.RootElement.GetProperty("agent_id").GetString()!;

        // The terminal completion frees the slot; the background pump then starts the queued spawn on the
        // now-released permit. Wait for the pending agent to settle terminal, then assert the queued agent
        // is started by the pump (it becomes Running — the default fake holds its slot open).
        releaseTerminal.SetResult(true);
        await Wait.UntilAsync(
            () =>
            {
                // See the #403 note above: a genuine fault propagates instead of masking as "not yet".
                return _manager!.TryPeek(pendingAgentId, out var status)
                    && status.Contains("\"completed\"", StringComparison.Ordinal);
            },
            "the pending-message sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );

        await Wait.UntilAsync(
            () =>
            {
                // See the #403 note above: a genuine fault propagates instead of masking as "not yet".
                return _manager!.TryPeek(queuedAgentId, out var status)
                    && status.Contains("\"running\"", StringComparison.Ordinal);
            },
            "the queued sub-agent reported running",
            TimeSpan.FromSeconds(10)
        );

        using var queuedPeek = JsonDocument.Parse(_manager.Peek(queuedAgentId));
        queuedPeek
            .RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be("running", "the terminal completion released the slot, so the pump started the queued sub-agent");
    }

    [Fact]
    public async Task DisposeAsync_BoundsAWedgedRunTask_RatherThanHangingForever()
    {
        // #373: production DisposeAsync used to await each sub-agent's RunTask/MonitorTask with no
        // ceiling. Cts.CancelAsync() and StopAsync(5s) precede that await, but neither GUARANTEES the
        // task actually observes cancellation -- a RunTask that ignores its token (simulated here via
        // RunImpl) would otherwise hang DisposeAsync, and every real host shutting down behind it,
        // forever. The test-only ceiling override keeps this fast without weakening what it proves:
        // the production ceiling is the same code path, just a smaller number.
        var manager = CreateManagerWithTemplates(
            1,
            new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") }
        );
        manager.TestPerAgentBackgroundTaskDisposeCeiling = TimeSpan.FromMilliseconds(200);
        manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent { RunImpl = _ => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None) };
        _manager = manager;

        _ = await manager.SpawnAsync("worker", "task", runInBackground: true);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        elapsed.Stop();

        elapsed
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(2),
                "a RunTask that never observes cancellation must be abandoned at the per-agent ceiling, not awaited forever"
            );
    }

    [Fact]
    public async Task DisposeAsync_BoundsACleanupStuckOnATokenIgnoringRunTask_RatherThanHangingForever()
    {
        // #373 review follow-up (PR #396): the original #373 fix bounded DisposeAsync's own per-agent
        // teardown loop, but CleanupFailedSpawnAsync -- reached whenever a spawn's own SendAsync fails
        // AFTER its RunTask/MonitorTask already started -- still awaited those same two tasks
        // UNBOUNDED. That is reachable from DisposeAsync itself: disposal cancels the spawn pump's own
        // token FIRST (before awaiting the pump task), and a BACKGROUND queued spawn's SendAsync is
        // called with exactly that pump token. If cancelling it unblocks SendAsync into this failure
        // path while the spawn's RunTask separately ignores ITS OWN token (simulated here via
        // RunImpl), the unbounded await inside CleanupFailedSpawnAsync used to hang forever -- which
        // hangs the pump loop awaiting it, which hangs DisposeAsync's own await of the pump behind
        // that. Three unreturning awaits stacked on one wedged background task.
        var manager = CreateManagerWithTemplates(
            1,
            new Dictionary<string, SubAgentTemplate>
            {
                ["filler"] = DummyTemplate("filler"),
                ["wedged"] = DummyTemplate("wedged"),
            }
        );
        manager.TestPerAgentBackgroundTaskDisposeCeiling = TimeSpan.FromMilliseconds(200);

        var releaseFiller = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.TestAgentFactoryOverride = (spawnedAgentId, template) =>
            template.Name == "filler"
                ? new FakeMultiTurnAgent
                {
                    // Holds the sole permit until the test says otherwise, so "wedged" below is
                    // deterministically defer-queued rather than racing the inline fast path.
                    SubscribeImpl = (_, ct) =>
                        FakeMultiTurnAgent.WaitThenCompleteStream(releaseFiller.Task, "filler-run", ct),
                }
                : new FakeMultiTurnAgent
                {
                    // Called with the PUMP's own token (queued.RunInBackground => pumpCt), not any
                    // caller token -- this is what makes DisposeAsync's own pump-cancel the thing that
                    // unblocks it, exactly as the deadlock scenario requires.
                    SendWithTokenImpl = async (callIndex, sendCt) =>
                    {
                        sendEntered.TrySetResult(true);
                        await Task.Delay(Timeout.InfiniteTimeSpan, sendCt);
                        return new SendReceipt($"unreachable-{callIndex}", null, DateTimeOffset.UtcNow);
                    },
                    // Ignores its OWN per-agent token entirely -- CleanupFailedSpawnAsync's
                    // state.Cts.CancelAsync() can never make this RunTask return on its own.
                    RunImpl = _ => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None),
                };
        _manager = manager;

        _ = await manager.SpawnAsync("filler", "task", runInBackground: true);

        var wedgedJson = await manager.SpawnAsync("wedged", "task", runInBackground: true);
        using var wedgedDoc = JsonDocument.Parse(wedgedJson);
        wedgedDoc
            .RootElement.GetProperty("status")
            .GetString()
            .Should()
            .Be(
                "queued",
                "the filler must still hold the only permit when 'wedged' is requested, or the pump's own "
                    + "token never becomes 'wedged's SendAsync token"
            );

        // Frees the permit: the pump dequeues "wedged" and starts it via StartWithHeldPermitAsync,
        // which reaches SendAsync (blocked on the pump's token) with RunTask/MonitorTask already live.
        releaseFiller.SetResult(true);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        elapsed.Stop();

        elapsed
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(2),
                "cancelling the pump must unblock SendAsync into CleanupFailedSpawnAsync, whose own "
                    + "RunTask await must itself be bounded by the same per-agent ceiling -- otherwise "
                    + "disposal hangs on 'await _pumpTask' forever, behind a spawn that never even "
                    + "finished registering"
            );
    }

    [Fact]
    public async Task DisposeAsync_ActuallyAwaitsARunTaskThatFinishesShortlyAfterCancellation_NotJustAbandonsIt()
    {
        // #396 review addendum: mutating the PRODUCTION PerAgentBackgroundTaskDisposeCeiling constant
        // (SubAgentManager.cs) to TimeSpan.Zero still passed the entire LmMultiTurn.Tests suite
        // (1252/1252) before this test existed. Every other ceiling test
        // (DisposeAsync_BoundsAWedgedRunTask..., DisposeAsync_BoundsACleanupStuckOn...) sets
        // TestPerAgentBackgroundTaskDisposeCeiling to a short override AND uses a RunTask that NEVER
        // completes, so they only prove a bounding mechanism exists -- none of them can tell "abandoned
        // instantly" apart from "waited the real ceiling, then abandoned", because both look identical
        // when the task never finishes either way, and none of them exercise the production constant's
        // actual value since they all override it. This test deliberately does NOT set the test
        // override, so it exercises the real production ceiling directly. Its RunTask DOES finish
        // shortly after its token is cancelled -- well within the real 10s ceiling -- and asserts a side
        // effect of that completion is observable once DisposeAsync returns, so the ceiling's ACTUAL
        // VALUE (not just the bounding code's existence) is what a regression here would break.
        var manager = CreateManagerWithTemplates(
            1,
            new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") }
        );

        var completedAfterCancel = false;
        manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent
            {
                RunImpl = async ct =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Real cleanup work that takes a moment after cancellation is observed (flushing a
                        // buffer, closing a connection) rather than returning the instant the token fires --
                        // still comfortably inside the real 10s production ceiling.
                        await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
                        completedAfterCancel = true;
                        throw;
                    }
                },
            };
        _manager = manager;

        _ = await manager.SpawnAsync("worker", "task", runInBackground: true);

        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        completedAfterCancel
            .Should()
            .BeTrue(
                "a RunTask that finishes shortly after cancellation -- well within the per-agent ceiling -- "
                    + "must actually be awaited to completion during disposal, not abandoned the instant "
                    + "cancellation is requested"
            );
    }

    [Fact]
    public async Task RestartRun_BoundsAFinishedRunsTokenIgnoringRunTask_RatherThanHangingForever()
    {
        // #404: RestartRunAsync cancels the finished run's CTS and then awaits its old
        // RunTask/MonitorTask BEFORE rebuilding the pipeline. That await used to be unbounded, unlike
        // every sibling teardown path (DisposeAsync, CleanupFailedSpawnAsync) that #373/#396 bounded via
        // AwaitBoundedTaskAsync. A RunTask that ignores its token (simulated via RunImpl) would wedge the
        // restart -- and SendMessageAsync awaiting it -- forever. The short ceiling keeps this fast
        // without weakening what it proves: production is the same code path, larger number.
        var manager = CreateManagerWithTemplates(
            1,
            new Dictionary<string, SubAgentTemplate> { ["owned"] = DummyTemplate("owned") }
        );
        manager.TestPerAgentBackgroundTaskDisposeCeiling = TimeSpan.FromMilliseconds(200);
        manager.TestAgentFactoryOverride = (agentId, _) =>
            new FakeMultiTurnAgent
            {
                ThreadId = $"subagent-{agentId}",
                // Reaches terminal completion so the follow-up is a RESTART (not an inject into a live run).
                SubscribeImpl = (_, ct) => FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct),
                // But the run itself ignores cancellation: this is the exact task RestartRunAsync's
                // pre-rebuild await must be bounded against.
                RunImpl = _ => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None),
            };
        // Owned provider so terminal completion disposes it, forcing the follow-up down the rebuild path.
        manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;
        _manager = manager;

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        await Wait.UntilAsync(
            () =>
                manager.TryPeek(agentId, out var status) && status.Contains("\"completed\"", StringComparison.Ordinal),
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await manager.SendMessageAsync(agentId, "continue", runInBackground: true).WaitAsync(TimeSpan.FromSeconds(10));
        elapsed.Stop();

        // 5s, not 2s: the ceiling under test is 200ms, so this still cleanly separates "bounded at
        // the ceiling" from both "hard-coded the 10s production ceiling" and "unbounded" (the outer
        // WaitAsync(10s) catches forever) - while giving a loaded CI runner real scheduling headroom;
        // 2s flaked under concurrent-lane load on work that is not itself timing-bound.
        elapsed
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(5),
                "the restart must abandon the finished run's token-ignoring RunTask at the per-agent ceiling, "
                    + "not await it forever"
            );
    }

    [Fact]
    public async Task RestartRun_BoundsAFailureCleanupStuckOnATokenIgnoringRunTask_RatherThanHangingForever()
    {
        // #404 (second unbounded await): when a restart's own SendAsync throws AFTER the replacement
        // run/monitor have already started, RestartRunAsync's catch cancels and awaits those new tasks
        // to avoid leaking them. That cleanup await used to be unbounded too, so a replacement RunTask
        // that ignores its token wedged the failing restart (and the exception it is trying to surface)
        // forever. Bounding it lets the InvalidOperationException propagate at the ceiling instead.
        var manager = CreateManagerWithTemplates(
            1,
            new Dictionary<string, SubAgentTemplate> { ["owned"] = DummyTemplate("owned") }
        );
        manager.TestPerAgentBackgroundTaskDisposeCeiling = TimeSpan.FromMilliseconds(200);

        var runStarts = 0;
        manager.TestAgentFactoryOverride = (agentId, _) =>
            new FakeMultiTurnAgent
            {
                ThreadId = $"subagent-{agentId}",
                // Epoch 1 finishes so the follow-up restarts it; the replacement epoch's subscription
                // just stays open. Same loop instance across both epochs: a completion no longer
                // disposes the owned provider, so the restart reuses the live pipeline.
                SubscribeImpl = (callIndex, ct) =>
                    callIndex == 1
                        ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                        : FakeMultiTurnAgent.WaitForeverStream(ct),
                // The spawn send succeeds; the RESTART's send throws AFTER the replacement run/monitor
                // have already started, driving RestartRunAsync into its failure-cleanup catch.
                SendImpl = callIndex =>
                    callIndex == 1
                        ? new ValueTask<SendReceipt>(new SendReceipt("r1", null, DateTimeOffset.UtcNow))
                        : ValueTask.FromException<SendReceipt>(new InvalidOperationException("restart send failed")),
                // Epoch 1's run honours cancellation so the restart's PRE-rebuild await returns cleanly
                // and the test actually reaches the failure-cleanup path; epoch 2's run ignores its own
                // token entirely -- that is the task the cleanup await must bound.
                RunImpl = ct =>
                    Interlocked.Increment(ref runStarts) == 1
                        ? Task.Delay(Timeout.InfiniteTimeSpan, ct)
                        : Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None),
            };
        manager.TestOwnedProviderOverride = (_, _) => new Mock<IStreamingAgent>().Object;
        _manager = manager;

        var spawnJson = await manager.SpawnAsync("owned", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        await Wait.UntilAsync(
            () =>
                manager.TryPeek(agentId, out var status) && status.Contains("\"completed\"", StringComparison.Ordinal),
            "the sub-agent reported completed",
            TimeSpan.FromSeconds(10)
        );

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var act = () =>
            manager.SendMessageAsync(agentId, "continue", runInBackground: true).WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("restart send failed");
        elapsed.Stop();

        // Same widening as the sibling test above: 200ms ceiling + loaded-runner headroom, still far
        // below both the 10s production ceiling and the outer WaitAsync bound.
        elapsed
            .Elapsed.Should()
            .BeLessThan(
                TimeSpan.FromSeconds(5),
                "the restart-failure cleanup must abandon the replacement run's token-ignoring RunTask at the "
                    + "per-agent ceiling so the failure surfaces, not hang on it forever"
            );
    }

    #region Helpers


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
            AgentFactory = () =>
                throw new NotSupportedException("Bypassed by TestAgentFactoryOverride; should never be invoked."),
        };
    }

    private SubAgentManager CreateManagerWithTemplates(
        int maxConcurrent,
        IReadOnlyDictionary<string, SubAgentTemplate> templates
    )
    {
        var options = new SubAgentOptions { Templates = templates, MaxConcurrentSubAgents = maxConcurrent };

        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates)
        );
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
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

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

    /// <summary>
    /// Overrides <see cref="RunAsync"/>'s body entirely (#373), so a test can make RunTask ignore its
    /// token and hang forever -- proving <see cref="SubAgentManager.DisposeAsync"/>'s per-agent
    /// teardown ceiling actually bounds a wedged background task rather than hanging behind it. Null
    /// (default) keeps the normal cancellable "run forever until cancelled" behavior below.
    /// </summary>
    public Func<CancellationToken, Task>? RunImpl { get; set; }

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    )
    {
        SentSink?.Enqueue(messages.OfType<TextMessage>().Select(m => m.Text).FirstOrDefault() ?? string.Empty);
        var callIndex = Interlocked.Increment(ref _sendCallCount);
        if (SendWithTokenImpl != null)
        {
            return SendWithTokenImpl(callIndex, ct);
        }

        return SendImpl != null
            ? SendImpl(callIndex)
            : new ValueTask<SendReceipt>(new SendReceipt(Guid.NewGuid().ToString("N"), inputId, DateTimeOffset.UtcNow));
    }

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    )
    {
        throw new NotSupportedException("Not used by SubAgentManager or these tests.");
    }

    public IAsyncEnumerable<IMessage> ExecuteRunAsync(UserInput userInput, CancellationToken ct = default)
    {
        throw new NotSupportedException("Not used by SubAgentManager or these tests.");
    }

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default)
    {
        var callIndex = Interlocked.Increment(ref _subscribeCallCount);
        return SubscribeImpl != null ? SubscribeImpl(callIndex, ct) : WaitForeverStream(ct);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        try
        {
            if (RunImpl != null)
            {
                await RunImpl(ct);
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
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

    public ValueTask DisposeAsync()
    {
        _ = Interlocked.Increment(ref _disposeCount);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stream that never produces a message and only ends when cancelled - mirrors a real
    /// sub-agent's subscription staying open/blocked after its background loop has nothing left
    /// to do (the loop and subscriber channels stay alive across individual run completions;
    /// only explicit cancellation/disposal closes them).
    /// </summary>
    internal static async IAsyncEnumerable<IMessage> WaitForeverStream([EnumeratorCancellation] CancellationToken ct)
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
        [EnumeratorCancellation] CancellationToken ct
    )
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
        [EnumeratorCancellation] CancellationToken ct
    )
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
        [EnumeratorCancellation] CancellationToken ct
    )
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
