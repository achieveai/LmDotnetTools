using System.Collections.Concurrent;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Lifecycle-teardown regressions for <see cref="SubAgentManager"/>, covering the three defects that
/// share one root cause — a teardown path that hands something back (a permit, a live reference, control
/// of the calling thread) before the child it belongs to has actually finished being torn down:
/// <list type="bullet">
/// <item><description><c>PR451-002</c>: a monitor that fails outright released the sub-agent's
/// concurrency permit while that sub-agent's run task and owned provider were still live, so the pool
/// could hand the slot to a NEW sub-agent while the faulted one still held real resources.</description></item>
/// <item><description><c>PR451-003</c>: a restart whose cleanup disposed the live loop left
/// <c>state.Agent</c> pointing at that disposed instance. For a BORROWED provider the restart path's
/// rebuild branch does not fire on its own, so the next continuation drove a torn-down
/// loop.</description></item>
/// <item><description><c>PR451-004</c>/<c>PR451-005</c>: restart, failed-spawn cleanup, and manager
/// shutdown awaited a child's <c>RunTask</c>/<c>MonitorTask</c> (and its loop's disposal) with no
/// ceiling, so one task that ignores its cancellation token wedged the caller — up to and including
/// hanging the whole manager's <c>DisposeAsync</c> — forever.</description></item>
/// </list>
/// Every barrier here is deterministic: a <see cref="TaskCompletionSource{TResult}"/> the test completes,
/// or an ordering assertion over a queue the production paths append to. No test asserts on elapsed time
/// beyond a generous outer ceiling used purely to turn "hangs forever" into a failure rather than a
/// hung run.
/// </summary>
public class SubAgentManagerTeardownBoundaryTests : IAsyncLifetime
{
    /// <summary>
    /// Ceiling handed to the manager under test. Short enough that a bounded path finishes well inside
    /// <see cref="OuterCeiling"/>, long enough that a cooperative task is never abandoned spuriously.
    /// </summary>
    private static readonly TimeSpan ShortTeardownCeiling = TimeSpan.FromMilliseconds(250);

    /// <summary>Outer bound that converts "this path hangs forever" into a test failure.</summary>
    private static readonly TimeSpan OuterCeiling = TimeSpan.FromSeconds(10);

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly CapturingLogger _logger = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
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
            await _manager.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // PR451-002 — the permit is the LAST thing a faulted monitor gives back.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MonitorFault_CancelsAndDisposesTheChildBeforeItsPermitCanStartAnother()
    {
        // The monitor's subscription fails outright. Before the fix the monitor recorded the fault and
        // let its `finally` hand the permit straight back: the faulted sub-agent's RunAsync was never
        // cancelled and its OWNED provider was never disposed, so the pool could start a second
        // sub-agent while the first one's run and provider were still alive — MaxConcurrentSubAgents
        // enforced on paper only.
        //
        // The probe is an ORDERING one, not a timing one: with a single permit, the next sub-agent can
        // only be constructed once the permit is back, so "the faulted child's run ended and its
        // provider was disposed BEFORE the next sub-agent started" is exactly the teardown-before-release
        // invariant. Before the fix the first two entries never appear at all (nothing ever cancels that
        // run), so the ordering assertion fails on absence rather than on a race.
        const int maxConcurrent = 1;
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["faulting"] = DummyTemplate("faulting"),
            ["next"] = DummyTemplate("next"),
        };

        _manager = CreateManager(maxConcurrent, templates);

        var order = new ConcurrentQueue<string>();
        var nextStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var faultingAgent = new FakeMultiTurnAgent
        {
            SubscribeImpl = (_, _) =>
                FakeMultiTurnAgent.ThrowingStream(new InvalidOperationException("subscribe failed")),
            RunImpl = async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    order.Enqueue("faulted-child-run-ended");
                }
            },
        };

        var provider = new Mock<IStreamingAgent>();
        provider
            .As<IAsyncDisposable>()
            .Setup(d => d.DisposeAsync())
            .Returns(() =>
            {
                order.Enqueue("faulted-child-provider-disposed");
                return ValueTask.CompletedTask;
            });

        _manager.TestAgentFactoryOverride = (_, template) =>
        {
            if (template.Name == "faulting")
            {
                return faultingAgent;
            }

            order.Enqueue("next-sub-agent-started");
            nextStarted.TrySetResult(true);
            return new FakeMultiTurnAgent();
        };
        _manager.TestOwnedProviderOverride = (_, template) => template.Name == "faulting" ? provider.Object : null;

        _ = await _manager.SpawnAsync("faulting", "task", runInBackground: true);

        // The second spawn competes for the single permit the faulted child holds. Whether it runs
        // inline (the permit is already back) or defer-queues (it is not yet) is irrelevant to the
        // invariant under test — either way its agent is constructed only under a held permit.
        _ = await _manager.SpawnAsync("next", "task", runInBackground: true);

        _ = await nextStarted.Task.WaitAsync(OuterCeiling);

        order
            .Should()
            .ContainInOrder("faulted-child-run-ended", "faulted-child-provider-disposed", "next-sub-agent-started");
    }

    // ---------------------------------------------------------------------------------------------
    // PR451-003 — a failed restart must never leave a disposed loop as the live reference.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailedRestart_NextContinuationRebuildsInsteadOfDrivingTheDisposedLoop()
    {
        // The restart failure path ALWAYS disposes state.Agent. With a BORROWED provider neither
        // HasDisposedOwnedProviderAgent nor OwnedProviderTerminalDisposeFailed is set, so the restart's
        // rebuild branch was skipped on the next continuation and the manager called RunAsync/SendAsync
        // straight through the loop it had just torn down.
        var templates = new Dictionary<string, SubAgentTemplate> { ["borrowed"] = DummyTemplate("borrowed") };

        _manager = CreateManager(maxConcurrent: 2, templates);

        var created = new List<FakeMultiTurnAgent>();
        var instances = 0;
        _manager.TestAgentFactoryOverride = (_, _) =>
        {
            var instance = Interlocked.Increment(ref instances);
            var agent =
                instance == 1
                    ? new FakeMultiTurnAgent
                    {
                        // Send #1 is the spawn's task; send #2 is the FIRST restart's prompt and fails.
                        // A later send on this same (disposed) instance would succeed — deliberately, so a
                        // regression fails the "a fresh loop was built" assertion rather than throwing.
                        SendImpl = idx =>
                            idx == 2
                                ? ValueTask.FromException<SendReceipt>(
                                    new InvalidOperationException("restart send failed")
                                )
                                : new ValueTask<SendReceipt>(new SendReceipt("r", null, DateTimeOffset.UtcNow)),
                        SubscribeImpl = (idx, ct) =>
                            idx == 1
                                ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                                : FakeMultiTurnAgent.WaitForeverStream(ct),
                    }
                    : new FakeMultiTurnAgent();

            lock (created)
            {
                created.Add(agent);
            }

            return agent;
        };
        // No TestOwnedProviderOverride: the provider is BORROWED. That is the case the rebuild branch
        // does not cover on its own, and therefore the one this finding is about.

        var spawnJson = await _manager.SpawnAsync("borrowed", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    return _manager!.Peek(agentId).Contains("\"completed\"");
                }
                catch
                {
                    return false;
                }
            },
            OuterCeiling
        );

        var failingRestart = () => _manager.SendMessageAsync(agentId, "first", runInBackground: true);
        _ = await failingRestart.Should().ThrowAsync<InvalidOperationException>().WithMessage("restart send failed");

        FakeMultiTurnAgent original;
        lock (created)
        {
            _ = created.Should().HaveCount(1);
            original = created[0];
        }

        original.DisposeCount.Should().BeGreaterThan(0, "the restart failure path disposes the live loop instance");

        // The next continuation must not run against that instance.
        var resumed = await _manager.SendMessageAsync(agentId, "second", runInBackground: true).WaitAsync(OuterCeiling);
        resumed.Should().Contain("resumed");

        lock (created)
        {
            _ = created
                .Should()
                .HaveCount(
                    2,
                    "a failed restart disposed the live loop, so the next continuation must rebuild the "
                        + "pipeline instead of reusing it"
                );
        }

        original
            .InvocationsAfterDispose.Should()
            .Be(0, "a disposed loop must never be sent to, subscribed to, or run again");
    }

    // ---------------------------------------------------------------------------------------------
    // PR451-004 / PR451-005 — every teardown await is bounded by one configurable ceiling.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Restart_NonCooperativeOldRunTask_ExitsWithinTheConfiguredCeiling()
    {
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") };

            _manager = CreateManager(maxConcurrent: 2, templates);
            _manager.TestAgentFactoryOverride = (_, _) =>
                new FakeMultiTurnAgent
                {
                    // Ignores its token entirely: cancelling the run never ends this task.
                    RunImpl = _ => block.Task,
                    SubscribeImpl = (idx, ct) =>
                        idx == 1
                            ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                            : FakeMultiTurnAgent.WaitForeverStream(ct),
                };

            var agentId = await SpawnAndWaitForCompletionAsync("worker");

            var resumed = await _manager
                .SendMessageAsync(agentId, "continue", runInBackground: true)
                .WaitAsync(OuterCeiling);

            resumed.Should().Contain("resumed");
            _logger
                .Warnings.Should()
                .Contain(
                    w => w.Contains("RunTask") && w.Contains("abandoning"),
                    "an abandoned wait must be reported, not silently skipped"
                );
        }
        finally
        {
            _ = block.TrySetResult(true);
        }
    }

    [Fact]
    public async Task Restart_NonCooperativeOldMonitorTask_ExitsWithinTheConfiguredCeiling()
    {
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") };

            _manager = CreateManager(maxConcurrent: 2, templates);
            _manager.TestAgentFactoryOverride = (_, _) =>
                new FakeMultiTurnAgent
                {
                    // The RUN task is cooperative here, isolating the monitor as the wedged one.
                    SubscribeImpl = (idx, ct) =>
                        idx == 1
                            ? FakeMultiTurnAgent.CompleteOnceThenBlockIgnoringCancellationStream(
                                "run-1",
                                block.Task,
                                ct
                            )
                            : FakeMultiTurnAgent.WaitForeverStream(ct),
                };

            var agentId = await SpawnAndWaitForCompletionAsync("worker");

            var resumed = await _manager
                .SendMessageAsync(agentId, "continue", runInBackground: true)
                .WaitAsync(OuterCeiling);

            resumed.Should().Contain("resumed");
            _logger.Warnings.Should().Contain(w => w.Contains("MonitorTask") && w.Contains("abandoning"));
        }
        finally
        {
            _ = block.TrySetResult(true);
        }
    }

    [Fact]
    public async Task DisposeAsync_NonCooperativeRunAndMonitor_ExitsWithinTheConfiguredCeiling()
    {
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var templates = new Dictionary<string, SubAgentTemplate> { ["wedged"] = DummyTemplate("wedged") };

            // Local manager: this test disposes it itself, so it must not also be disposed by the
            // fixture (DisposeAsync is idempotent, but keeping ownership local keeps the probe honest).
            var manager = CreateManager(maxConcurrent: 2, templates);
            manager.TestAgentFactoryOverride = (_, _) =>
                new FakeMultiTurnAgent
                {
                    RunImpl = _ => block.Task,
                    SubscribeImpl = (_, ct) => FakeMultiTurnAgent.BlockIgnoringCancellationStream(block.Task, ct),
                };

            _ = await manager.SpawnAsync("wedged", "task", runInBackground: true);

            await manager.DisposeAsync().AsTask().WaitAsync(OuterCeiling);

            _logger.Warnings.Should().Contain(w => w.Contains("RunTask") && w.Contains("abandoning"));
            _logger.Warnings.Should().Contain(w => w.Contains("MonitorTask") && w.Contains("abandoning"));
        }
        finally
        {
            _ = block.TrySetResult(true);
        }
    }

    [Fact]
    public async Task FailedSpawnCleanup_NonCooperativeRunTask_ExitsWithinCeilingAndReturnsThePermit()
    {
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            const int maxConcurrent = 1;
            var templates = new Dictionary<string, SubAgentTemplate>
            {
                ["throws-on-send"] = DummyTemplate("throws-on-send"),
                ["normal"] = DummyTemplate("normal"),
            };

            _manager = CreateManager(maxConcurrent, templates);
            _manager.TestAgentFactoryOverride = (_, template) =>
                template.Name == "throws-on-send"
                    ? new FakeMultiTurnAgent
                    {
                        SendImpl = _ =>
                            ValueTask.FromException<SendReceipt>(new InvalidOperationException("send failed")),
                        RunImpl = _ => block.Task,
                    }
                    : new FakeMultiTurnAgent();

            var spawn = async () =>
                await _manager.SpawnAsync("throws-on-send", "task", runInBackground: true).WaitAsync(OuterCeiling);

            // A TimeoutException here (rather than the spawn's own failure) is the unbounded-wait defect.
            _ = await spawn.Should().ThrowAsync<InvalidOperationException>().WithMessage("send failed");

            // The rollback still gave the permit back, so the next spawn runs INLINE rather than queuing.
            var json = await _manager.SpawnAsync("normal", "after", runInBackground: true);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("status")
                .GetString()
                .Should()
                .Be("spawned", "a bounded rollback must still release the failed spawn's permit");
        }
        finally
        {
            _ = block.TrySetResult(true);
        }
    }

    [Fact]
    public async Task QueuedSpawnThatFailsToStart_DoesNotWedgeThePumpOnANonCooperativeRunTask()
    {
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            const int maxConcurrent = 1;
            var templates = new Dictionary<string, SubAgentTemplate>
            {
                ["holder"] = DummyTemplate("holder"),
                ["throws-on-send"] = DummyTemplate("throws-on-send"),
                ["normal"] = DummyTemplate("normal"),
            };

            _manager = CreateManager(maxConcurrent, templates);
            _manager.TestAgentFactoryOverride = (_, template) =>
                template.Name switch
                {
                    "holder" => new FakeMultiTurnAgent
                    {
                        SubscribeImpl = (_, ct) =>
                            FakeMultiTurnAgent.WaitThenCompleteStream(releaseHolder.Task, "holder-run", ct),
                    },
                    "throws-on-send" => new FakeMultiTurnAgent
                    {
                        SendImpl = _ =>
                            ValueTask.FromException<SendReceipt>(new InvalidOperationException("send failed")),
                        RunImpl = _ => block.Task,
                    },
                    _ => new FakeMultiTurnAgent(),
                };

            // Occupy the single permit, then queue the poisoned spawn ahead of a healthy one.
            var holderJson = await _manager.SpawnAsync("holder", "task", runInBackground: true);
            using (var holderDoc = JsonDocument.Parse(holderJson))
            {
                holderDoc.RootElement.GetProperty("status").GetString().Should().Be("spawned");
            }

            var poisonedJson = await _manager.SpawnAsync("throws-on-send", "task", runInBackground: true);
            using (var poisonedDoc = JsonDocument.Parse(poisonedJson))
            {
                poisonedDoc.RootElement.GetProperty("status").GetString().Should().Be("queued");
            }

            var healthyJson = await _manager.SpawnAsync("normal", "task", runInBackground: true);
            using var healthyDoc = JsonDocument.Parse(healthyJson);
            healthyDoc.RootElement.GetProperty("status").GetString().Should().Be("queued");
            var healthyId = healthyDoc.RootElement.GetProperty("agent_id").GetString()!;

            // Free the permit. The pump dequeues the poisoned spawn first; its rollback must be bounded
            // so the healthy spawn behind it still starts.
            releaseHolder.SetResult(true);

            await WaitForConditionAsync(
                () => _manager!.TryPeek(healthyId, out var status) && status.Contains("\"running\""),
                OuterCeiling
            );

            _ = _manager.TryPeek(healthyId, out var finalStatus).Should().BeTrue();
            finalStatus
                .Should()
                .Contain("\"running\"", "a bounded failed-spawn rollback must not wedge the defer-queue pump");
        }
        finally
        {
            _ = releaseHolder.TrySetResult(true);
            _ = block.TrySetResult(true);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // PR451-002 (review follow-up) — the faulted-monitor teardown must OWN the transition, and every
    // owned-provider disposal must be bounded with its outcome modelled.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task MonitorFault_DrainsAnAdmittedSendBeforeDisposingTheProviderItRunsThrough()
    {
        // A monitor fault is the one terminal path that never goes through BeginTerminalDisposalAsync,
        // the handshake that makes a GRACEFUL terminal safe against a concurrent SendMessageAsync. So a
        // send admitted as Inject microseconds before the fault is still inside the provider when the
        // fault teardown decides to dispose it. Before the fix that teardown disposed immediately: the
        // provider was torn down underneath a live write.
        //
        // Fully deterministic, with no test-side sleep: the inject send parks on ITS OWN cancellation
        // token, and the only thing that fires that token is the teardown's own lease-drain. So the
        // ordering below is driven entirely by production control flow.
        var order = new ConcurrentQueue<string>();
        var faultGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var injectAdmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var injectCallerCts = new CancellationTokenSource();

        var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") };

        // A ceiling comfortably longer than this test's control flow, so what serialises the two paths
        // is the CLAIM, not a wait giving up.
        _manager = CreateManager(maxConcurrent: 2, templates, TimeSpan.FromSeconds(5));

        var provider = new GatedProvider(Task.CompletedTask, order, "provider-1");

        _manager.TestAgentFactoryOverride = (_, _) =>
            new FakeMultiTurnAgent
            {
                SubscribeImpl = (_, ct) =>
                    FakeMultiTurnAgent.WaitThenThrowStream(
                        faultGate.Task,
                        new InvalidOperationException("subscribe failed"),
                        ct
                    ),
                SendWithTokenImpl = async (idx, ct) =>
                {
                    if (idx == 1)
                    {
                        // The spawn's own task prompt.
                        return new SendReceipt("r", null, DateTimeOffset.UtcNow);
                    }

                    order.Enqueue("inject-send-started");
                    injectAdmitted.TrySetResult(true);

                    // Park until the manager's linked lifecycle token fires. Production fires it from
                    // the teardown's lease drain precisely so a wedged send cannot stall the disposal.
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected.
                    }

                    order.Enqueue("inject-send-finished");
                    return new SendReceipt("r", null, DateTimeOffset.UtcNow);
                },
            };
        _manager.TestOwnedProviderOverride = (_, _) => provider;

        var spawnJson = await _manager.SpawnAsync("worker", "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        // Admit an inject; it parks inside the provider.
        var injectTask = _manager.SendMessageAsync(agentId, "inject", runInBackground: true, injectCallerCts.Token);
        _ = await injectAdmitted.Task.WaitAsync(OuterCeiling);

        // Now fault the monitor, with that send still in flight.
        faultGate.TrySetResult(true);

        try
        {
            _ = await injectTask.WaitAsync(OuterCeiling);
        }
        catch (Exception)
        {
            // The inject's own outcome is not what this test is about — a lifecycle-cancelled inject
            // legitimately re-enters the continuation loop or surfaces the run's error. The ORDER is.
        }
        finally
        {
            injectCallerCts.Cancel();
        }

        await WaitForConditionAsync(() => order.Contains("provider-1-dispose-entered"), OuterCeiling);

        var seen = order.ToArray();
        var disposeEntered = Array.IndexOf(seen, "provider-1-dispose-entered");
        var sendFinished = Array.IndexOf(seen, "inject-send-finished");

        disposeEntered.Should().BeGreaterThan(-1, "the faulted run's owned provider must still be disposed");
        sendFinished
            .Should()
            .BeGreaterThan(-1, "the teardown's lease drain must cancel the wedged inject so it can finish");
        disposeEntered
            .Should()
            .BeGreaterThan(
                sendFinished,
                "the provider must not be disposed while an admitted send is still writing through it"
            );
    }

    [Fact]
    public async Task MonitorFault_ProviderDisposeOutruns_TheCeiling_NextContinuationDoesNotReuseIt()
    {
        // Bounding a disposal is only half the fix. When the wait is abandoned the disposal is still IN
        // FLIGHT — the state's guard is neither Idle nor Disposed — so a naive rebuild check
        // ("HasDisposedOwnedProviderAgent") reads false and the next epoch happily reuses a provider that
        // is halfway through being torn down. That is strictly worse than the unbounded hang it replaced.
        //
        // The discriminator is a provider whose DisposeAsync never returns within the ceiling: the next
        // continuation must build a FRESH provider, and must NOT retry disposing the in-flight one.
        var blockForever = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") };
            _manager = CreateManager(maxConcurrent: 2, templates);

            var providers = new List<GatedProvider>();
            _manager.TestAgentFactoryOverride = (_, _) =>
                new FakeMultiTurnAgent
                {
                    SubscribeImpl = (idx, ct) =>
                        idx == 1
                            ? FakeMultiTurnAgent.ThrowingStream(new InvalidOperationException("subscribe failed"))
                            : FakeMultiTurnAgent.WaitForeverStream(ct),
                };
            _manager.TestOwnedProviderOverride = (_, _) =>
            {
                // Only the FIRST provider is non-cooperative; the replacement disposes normally.
                var gate = providers.Count == 0 ? blockForever.Task : Task.CompletedTask;
                var created = new GatedProvider(gate, label: $"provider-{providers.Count + 1}");
                providers.Add(created);
                return created;
            };

            var spawnJson = await _manager.SpawnAsync("worker", "task", runInBackground: true);
            using var spawnDoc = JsonDocument.Parse(spawnJson);
            var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

            // The faulted teardown starts disposing provider #1 and abandons the wait at the ceiling.
            await WaitForConditionAsync(
                () => _logger.Warnings.Any(w => w.Contains("Owned-provider disposal") && w.Contains("abandoning")),
                OuterCeiling
            );

            var resumed = await _manager
                .SendMessageAsync(agentId, "continue", runInBackground: true)
                .WaitAsync(OuterCeiling);

            resumed.Should().Contain("resumed");
            providers
                .Should()
                .HaveCount(
                    2,
                    "a provider whose disposal never completed is in an UNKNOWN state and must never be "
                        + "reused; the continuation has to build a fresh one"
                );
            providers[0]
                .DisposeCalls.Should()
                .Be(
                    1,
                    "an abandoned disposal is still running, so it must NOT be retried — retry semantics "
                        + "are for a disposal that actually finished by throwing"
                );
        }
        finally
        {
            _ = blockForever.TrySetResult(true);
        }
    }

    [Fact]
    public async Task DisposeAsync_NonCooperativeProviderDispose_ExitsWithinTheConfiguredCeiling()
    {
        // Shutdown is the path where an unbounded provider disposal is worst: it is the difference
        // between a host that stops and a host that has to be killed.
        var blockForever = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") };
            var manager = CreateManager(maxConcurrent: 2, templates);
            var provider = new GatedProvider(blockForever.Task, label: "provider-1");

            manager.TestAgentFactoryOverride = (_, _) =>
                new FakeMultiTurnAgent { SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct) };
            manager.TestOwnedProviderOverride = (_, _) => provider;

            _ = await manager.SpawnAsync("worker", "task", runInBackground: true);

            // A TimeoutException here IS the defect: manager disposal never returning.
            await manager.DisposeAsync().AsTask().WaitAsync(OuterCeiling);

            provider.DisposeCalls.Should().BeGreaterThan(0, "shutdown must still ATTEMPT the disposal");
            _logger
                .Warnings.Should()
                .Contain(
                    w => w.Contains("Owned-provider disposal") && w.Contains("abandoning"),
                    "an abandoned shutdown disposal must be reported, not silently skipped"
                );
        }
        finally
        {
            _ = blockForever.TrySetResult(true);
        }
    }

    [Fact]
    public async Task FailedSpawnCleanup_NonCooperativeProviderDispose_ExitsWithinCeilingAndReturnsThePermit()
    {
        var blockForever = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") };
            _manager = CreateManager(maxConcurrent: 1, templates);

            var first = true;
            _manager.TestAgentFactoryOverride = (_, _) =>
            {
                if (!first)
                {
                    return new FakeMultiTurnAgent
                    {
                        SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct),
                    };
                }

                first = false;
                return new FakeMultiTurnAgent
                {
                    SendImpl = _ => ValueTask.FromException<SendReceipt>(new InvalidOperationException("send failed")),
                    SubscribeImpl = (_, ct) => FakeMultiTurnAgent.WaitForeverStream(ct),
                };
            };

            var failingProvider = new GatedProvider(blockForever.Task, label: "provider-1");
            var issued = 0;
            _manager.TestOwnedProviderOverride = (_, _) =>
                Interlocked.Increment(ref issued) == 1 ? failingProvider : new GatedProvider(Task.CompletedTask);

            // A TimeoutException here would mean the rollback hung on the provider's disposal. Bounded
            // inside the assertion so an unbounded path fails the test rather than hanging the run.
            var spawn = _manager.SpawnAsync("worker", "task", runInBackground: true);
            var failing = async () => await spawn.WaitAsync(OuterCeiling);
            _ = await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("send failed");

            failingProvider.DisposeCalls.Should().BeGreaterThan(0, "the rollback must attempt the disposal");

            // The permit must be back despite the abandoned disposal — the single concurrency slot is
            // free, so the next spawn starts rather than queueing.
            var nextJson = await _manager.SpawnAsync("worker", "task", runInBackground: true).WaitAsync(OuterCeiling);
            using var nextDoc = JsonDocument.Parse(nextJson);
            nextDoc
                .RootElement.GetProperty("status")
                .GetString()
                .Should()
                .Be("spawned", "an abandoned provider disposal must not strand the failed spawn's permit");
        }
        finally
        {
            _ = blockForever.TrySetResult(true);
        }
    }

    [Fact]
    public async Task RestartCleanup_NonCooperativeProviderDispose_ExitsWithinTheConfiguredCeiling()
    {
        var blockForever = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = DummyTemplate("worker") };
            _manager = CreateManager(maxConcurrent: 2, templates);

            // Shared across instances deliberately: the restart REBUILDS the loop, so a per-instance send
            // counter would reset and the replacement's prompt would be its own send #1.
            var totalSends = 0;

            _manager.TestAgentFactoryOverride = (_, _) =>
                new FakeMultiTurnAgent
                {
                    // Send #1 is the spawn's task; send #2 is the restart's prompt, issued through the
                    // REPLACEMENT loop, and fails — driving the restart's failure cleanup, which disposes
                    // the replacement's owned provider.
                    SendImpl = _ =>
                        Interlocked.Increment(ref totalSends) == 2
                            ? ValueTask.FromException<SendReceipt>(new InvalidOperationException("restart send failed"))
                            : new ValueTask<SendReceipt>(new SendReceipt("r", null, DateTimeOffset.UtcNow)),
                    SubscribeImpl = (idx, ct) =>
                        idx == 1
                            ? FakeMultiTurnAgent.CompleteOnceThenWaitForeverStream("run-1", ct)
                            : FakeMultiTurnAgent.WaitForeverStream(ct),
                };

            var issued = 0;
            GatedProvider? replacementProvider = null;
            _manager.TestOwnedProviderOverride = (_, _) =>
            {
                if (Interlocked.Increment(ref issued) == 1)
                {
                    return new GatedProvider(Task.CompletedTask, label: "provider-1");
                }

                replacementProvider = new GatedProvider(blockForever.Task, label: "provider-2");
                return replacementProvider;
            };

            var agentId = await SpawnAndWaitForCompletionAsync("worker");

            // A TimeoutException here would mean the restart's failure cleanup hung on the replacement
            // provider's disposal instead of surfacing the real failure. Bounded inside the assertion so
            // an unbounded path fails the test rather than hanging the run.
            var restart = _manager.SendMessageAsync(agentId, "continue", runInBackground: true);
            var failingRestart = async () => await restart.WaitAsync(OuterCeiling);
            _ = await failingRestart
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("restart send failed");

            // Power check: without this the test could pass while never reaching the blocking disposal at
            // all, making the warning assertion below vacuous.
            replacementProvider
                .Should()
                .NotBeNull("the restart must have rebuilt the pipeline with a fresh owned provider");
            replacementProvider!
                .DisposeCalls.Should()
                .BeGreaterThan(0, "the restart's failure cleanup must ATTEMPT to dispose the rebuilt provider");

            _logger
                .Warnings.Should()
                .Contain(
                    w => w.Contains("Owned-provider disposal") && w.Contains("abandoning"),
                    "an abandoned restart-cleanup disposal must be reported, not silently skipped"
                );
        }
        finally
        {
            _ = blockForever.TrySetResult(true);
        }
    }

    #region Helpers

    private async Task<string> SpawnAndWaitForCompletionAsync(string templateName)
    {
        var spawnJson = await _manager!.SpawnAsync(templateName, "task", runInBackground: true);
        using var spawnDoc = JsonDocument.Parse(spawnJson);
        var agentId = spawnDoc.RootElement.GetProperty("agent_id").GetString()!;

        await WaitForConditionAsync(
            () =>
            {
                try
                {
                    return _manager!.Peek(agentId).Contains("\"completed\"");
                }
                catch
                {
                    return false;
                }
            },
            OuterCeiling
        );

        return agentId;
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

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

    private SubAgentManager CreateManager(int maxConcurrent, IReadOnlyDictionary<string, SubAgentTemplate> templates) =>
        CreateManager(maxConcurrent, templates, ShortTeardownCeiling);

    private SubAgentManager CreateManager(
        int maxConcurrent,
        IReadOnlyDictionary<string, SubAgentTemplate> templates,
        TimeSpan teardownCeiling
    )
    {
        var options = new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = maxConcurrent,
            TeardownObservationTimeout = teardownCeiling,
        };

        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates),
            logger: _logger
        );
    }

    /// <summary>
    /// Owned-provider double whose <see cref="DisposeAsync"/> the test drives: it records that disposal
    /// was ENTERED and then waits on a gate the test controls. Two things need to be separable that a
    /// Moq setup conflates — "the manager decided to dispose me" and "that disposal finished" — because
    /// every claim in these tests is about the gap between them.
    /// </summary>
    private sealed class GatedProvider : IStreamingAgent, IAsyncDisposable
    {
        private readonly Task _releaseDispose;
        private readonly ConcurrentQueue<string>? _order;
        private readonly string _label;
        private int _disposeCalls;

        public GatedProvider(Task releaseDispose, ConcurrentQueue<string>? order = null, string label = "provider")
        {
            _releaseDispose = releaseDispose;
            _order = order;
            _label = label;
        }

        /// <summary>How many times the manager actually STARTED disposing this provider.</summary>
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Not used by SubAgentManager or these tests.");

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Not used by SubAgentManager or these tests.");

        public async ValueTask DisposeAsync()
        {
            _ = Interlocked.Increment(ref _disposeCalls);
            _order?.Enqueue($"{_label}-dispose-entered");
            await _releaseDispose;
            _order?.Enqueue($"{_label}-dispose-finished");
        }
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> that keeps every formatted Warning message, so a test can assert
    /// that an abandoned (ceiling-exceeded) wait was actually REPORTED rather than silently skipped —
    /// the only externally-observable evidence that the bounded path, and not a lucky fast completion,
    /// is what let the caller return.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => [.. _warnings];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel >= LogLevel.Warning)
            {
                _warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    #endregion
}
