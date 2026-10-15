using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// Unit tests for the notify-mode wait lifecycle in <see cref="TriggerRuntime"/>: a notify wait
/// stays armed across multiple fires, terminates once <c>maxFires</c> is reached, and delivers a
/// terminal envelope through the notify delegate on cancel. Block mode is covered separately by
/// <see cref="TriggerRuntimeTests"/> and must remain unaffected by this lifecycle.
/// </summary>
public class TriggerRuntimeNotifyTests
{
    private static (
        TriggerRuntime rt,
        ManualTriggerSource src,
        List<(string payload, bool isError)> notified
    ) BuildRuntime()
    {
        var notified = new List<(string, bool)>();
        var src = new ManualTriggerSource();
        var options = new TriggerOptions();
        var rt = new TriggerRuntime(
            options,
            resolve: (_, _, _, _) => Task.CompletedTask,
            notify: (payload, isError, _) =>
            {
                lock (notified)
                {
                    notified.Add((payload, isError));
                }
                return Task.CompletedTask;
            }
        );
        rt.Register(
            new TriggerSourceRegistration
            {
                Kind = "manual",
                Description = "test",
                ArgsSchema = "{}",
                Capabilities = ManualTriggerSource.Caps,
                Source = src,
            }
        );
        return (rt, src, notified);
    }

    private static (
        TriggerRuntime rt,
        ManualTriggerSource src,
        List<(string payload, bool isError)> notified,
        InMemoryNotifyWaitStore store
    ) BuildRuntimeWithStore()
    {
        var notified = new List<(string, bool)>();
        var src = new ManualTriggerSource();
        var store = new InMemoryNotifyWaitStore();
        var options = new TriggerOptions { NotifyWaitStore = store, ThreadId = "t" };
        var rt = new TriggerRuntime(
            options,
            resolve: (_, _, _, _) => Task.CompletedTask,
            notify: (payload, isError, _) =>
            {
                lock (notified)
                {
                    notified.Add((payload, isError));
                }
                return Task.CompletedTask;
            }
        );
        rt.Register(
            new TriggerSourceRegistration
            {
                Kind = "manual",
                Description = "test",
                ArgsSchema = "{}",
                Capabilities = ManualTriggerSource.Caps,
                Source = src,
            }
        );
        return (rt, src, notified, store);
    }

    [Fact]
    public async Task Discard_stop_removes_durable_waits_without_enqueue_and_closes_admission()
    {
        var (rt, src, notified, store) = BuildRuntimeWithStore();
        await using var runtime = rt;
        (await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, null, default)).IsArmed.Should().BeTrue();
        (await store.LoadActiveAsync("t")).Should().ContainSingle();
        await rt.StopForDiscardAsync();
        await rt.StopForDiscardAsync();
        (await store.LoadActiveAsync("t")).Should().BeEmpty();
        rt.ListWaits().Should().BeEmpty();
        notified.Should().BeEmpty();
        (await rt.ArmAsync("w2", "manual", "{}", "1h", null, WaitMode.Notify, null, default))
            .IsArmed.Should()
            .BeFalse();
        src.Sinks.Should().BeEmpty();
        notified.Should().BeEmpty();
    }

    [Fact]
    public async Task Discard_stop_rejects_duplicate_wait_id_without_losing_source()
    {
        var (rt, src, _, _) = BuildRuntimeWithStore();
        await using var runtime = rt;
        (await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, null, default)).IsArmed.Should().BeTrue();
        (await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, null, default))
            .IsArmed.Should()
            .BeFalse();
        await rt.StopForDiscardAsync();
        src.Sinks.Should().BeEmpty();
    }

    [Fact]
    public async Task Discard_stop_cannot_claim_success_after_ordinary_disposal()
    {
        var (rt, _, _, store) = BuildRuntimeWithStore();
        await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, null, default);
        await rt.DisposeAsync();
        var stop = () => rt.StopForDiscardAsync();
        await stop.Should().ThrowAsync<Exception>();
        (await store.LoadActiveAsync("t")).Should().ContainSingle();
    }

    [Fact]
    public async Task Discard_stop_drains_inflight_notify_save_before_deleting_rows()
    {
        var (rt, src, _, store) = BuildRuntimeWithStore();
        await using var runtime = rt;
        await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, null, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var fire = src.Sinks["w1"].FireAsync(new TriggerFireEvent("fire"), default).AsTask();
        await entered.Task;
        var stop = rt.StopForDiscardAsync();
        stop.IsCompleted.Should().BeFalse();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledWait = () => rt.StopForDiscardAsync(cancelled.Token);
        await cancelledWait.Should().ThrowAsync<OperationCanceledException>();
        release.TrySetResult();
        await fire;
        await stop;
        (await store.LoadActiveAsync("t")).Should().BeEmpty();
        src.Sinks.Should().BeEmpty();
    }

    [Fact]
    public async Task Discard_stop_does_not_confirm_failed_durable_delete()
    {
        var (rt, _, _, store) = BuildRuntimeWithStore();
        await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, null, default);
        store.FailDelete = true;
        var stop = () => rt.StopForDiscardAsync();
        await stop.Should().ThrowAsync<IOException>();
        await stop.Should().ThrowAsync<IOException>();
        (await store.LoadActiveAsync("t")).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discard_stop_refuses_lost_source_cleanup_failure(bool synchronousFire)
    {
        var source = new FailingDisposeSource(synchronousFire);
        var rt = new TriggerRuntime(new TriggerOptions(), (_, _, _, _) => Task.CompletedTask);
        rt.Register(
            new TriggerSourceRegistration
            {
                Kind = "failure",
                Description = "fixture",
                ArgsSchema = "{}",
                Capabilities = ManualTriggerSource.Caps,
                Source = source,
            }
        );
        await rt.ArmAsync("w1", "failure", "{}", "1h", null, WaitMode.Block, null, default);
        if (!synchronousFire)
            await rt.CancelWaitsAsync("w1", null, null, default);
        var stop = () => rt.StopForDiscardAsync();
        await stop.Should().ThrowAsync<InvalidOperationException>();
        source.Disposals.Should().BeGreaterThan(0);
    }

    private sealed class FailingDisposeSource(bool synchronousFire) : ITriggerSource, IArmedTrigger
    {
        public string WaitId => "w1";
        public int Disposals { get; private set; }

        public async ValueTask<IArmedTrigger> ArmAsync(
            TriggerArmRequest request,
            ITriggerEventSink eventSink,
            CancellationToken ct
        )
        {
            if (synchronousFire)
                await eventSink.FireAsync(new TriggerFireEvent("synchronous"), ct);
            return this;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.FromException(new InvalidOperationException("Fixture source is still live"));
        }
    }

    [Fact]
    public async Task NotifyWait_StaysArmed_AcrossMultipleFires()
    {
        var (rt, src, notified) = BuildRuntime();
        var armed = await rt.ArmAsync(
            "w1",
            "manual",
            "{}",
            "1h",
            null,
            WaitMode.Notify,
            maxFires: null,
            CancellationToken.None
        );
        armed.IsArmed.Should().BeTrue();

        var sink = src.Sinks["w1"];
        await sink.FireAsync(new TriggerFireEvent("one"), CancellationToken.None);
        await sink.FireAsync(new TriggerFireEvent("two"), CancellationToken.None);

        notified.Should().HaveCount(2);
        rt.ListWaits().Should().ContainSingle(w => w.WaitId == "w1"); // still armed
    }

    [Fact]
    public async Task NotifyWait_Terminates_WhenMaxFiresReached()
    {
        var (rt, src, notified) = BuildRuntime();
        await rt.ArmAsync("w2", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: 2, CancellationToken.None);
        var sink = src.Sinks["w2"];

        await sink.FireAsync(new TriggerFireEvent("a"), CancellationToken.None);
        await sink.FireAsync(new TriggerFireEvent("b"), CancellationToken.None);
        await sink.FireAsync(new TriggerFireEvent("c"), CancellationToken.None); // over budget

        notified.Should().HaveCount(2); // exactly maxFires envelopes, none after
        rt.ListWaits().Should().NotContain(w => w.WaitId == "w2"); // terminated
    }

    [Fact]
    public async Task NotifyWait_Cancel_DeliversTerminalEnvelope()
    {
        var (rt, src, notified) = BuildRuntime();
        await rt.ArmAsync("w3", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: null, CancellationToken.None);

        var cancelled = await rt.CancelWaitsAsync("w3", null, null, CancellationToken.None);

        cancelled.Should().Be(1);
        notified.Should().ContainSingle(n => n.payload.Contains("cancelled"));
    }

    [Fact]
    public async Task NotifyWait_MaxFiresBounded_PersistsEveryFire_NotJustFirstAndFinal()
    {
        // Regression for the maxFires-persistence fix: shouldPersist now includes
        // `wait.MaxFires is not null`, so a bounded wait persists fires_so_far on EVERY fire, not
        // just the first (fireNumber == 1) and the final one. A single fire does not discriminate
        // the fix from the bug, because fireNumber == 1 already forces a persist unconditionally —
        // the bug only shows up on a middle fire (neither first, a multiple of 10, nor final).
        // maxFires: 5 with two fires exercises exactly that middle-fire case: fireNumber == 2 is
        // not 1, not a multiple of 10, and not final (2 < 5).
        var (rt, src, notified, store) = BuildRuntimeWithStore();
        await rt.ArmAsync("w6", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: 5, CancellationToken.None);
        var sink = src.Sinks["w6"];

        await sink.FireAsync(new TriggerFireEvent("one"), CancellationToken.None);
        await sink.FireAsync(new TriggerFireEvent("two"), CancellationToken.None);

        notified.Should().HaveCount(2);
        var rows = await store.LoadActiveAsync("t");
        rows.Should().ContainSingle(r => r.WaitId == "w6" && r.FiresSoFar == 2);
    }

    [Fact]
    public async Task NotifyWait_Timeout_DeliversSingleTimedOutEnvelope_AndTearsDownWait()
    {
        var (rt, _, notified) = BuildRuntime();
        await rt.ArmAsync("w7", "manual", "{}", "50ms", null, WaitMode.Notify, maxFires: null, CancellationToken.None);

        // The ceiling timer fires the "timed_out" terminal envelope on its own — no manual fire.
        // Poll rather than a fixed delay: the ceiling grace is only 50ms, but CI machines can be
        // slow, so wait generously and bail out as soon as the envelope shows up.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !notified.Any(n => n.payload.Contains("timed_out")))
        {
            await Task.Delay(20);
        }

        notified.Should().ContainSingle(n => n.payload.Contains("timed_out"));
        rt.ListWaits().Should().NotContain(w => w.WaitId == "w7", "a timed-out wait must be torn down");
    }

    [Fact]
    public async Task NotifyWait_Arm_PropagatesCancellation_WhenCallerTokenCancelledMidPersist()
    {
        // Regression: ArmCoreAsync's persist-on-arm step used to catch ALL exceptions from
        // NotifyWaitStore.SaveAsync (including OperationCanceledException) and merely log a
        // warning, so a caller whose token was cancelled while the save was in flight still got
        // back an "armed" result — reporting success for a call it had already abandoned. The
        // fix rethrows OCE tied to the caller's own token instead of swallowing it, so ArmAsync
        // surfaces the cancellation and does not leave the wait dangling as armed.
        using var cts = new CancellationTokenSource();
        var store = new CancelDuringSaveNotifyWaitStore(cts);
        var src = new ManualTriggerSource();
        var options = new TriggerOptions { NotifyWaitStore = store, ThreadId = "t" };
        await using var rt = new TriggerRuntime(
            options,
            resolve: (_, _, _, _) => Task.CompletedTask,
            notify: (_, _, _) => Task.CompletedTask
        );
        rt.Register(
            new TriggerSourceRegistration
            {
                Kind = "manual",
                Description = "test",
                ArgsSchema = "{}",
                Capabilities = ManualTriggerSource.Caps,
                Source = src,
            }
        );

        Func<Task> act = () =>
            rt.ArmAsync("w8", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: null, cts.Token);

        await act.Should()
            .ThrowAsync<OperationCanceledException>(
                "the caller's own token was cancelled mid-persist, so ArmAsync must surface that "
                    + "instead of reporting the wait as armed"
            );

        rt.ListWaits()
            .Should()
            .NotContain(
                w => w.WaitId == "w8",
                "the aborted arm must be fully torn down (source disposed, gate released), not left "
                    + "dangling as a live-armed wait"
            );
    }

    /// <summary>
    /// <see cref="INotifyWaitStore"/> double whose <see cref="SaveAsync"/> cancels the caller's
    /// own token mid-call (simulating the caller abandoning the request while the save is in
    /// flight) and then observes that cancellation, exactly as a real cancellation-aware store
    /// would.
    /// </summary>
    private sealed class CancelDuringSaveNotifyWaitStore(CancellationTokenSource callerCts) : INotifyWaitStore
    {
        public Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default)
        {
            callerCts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotifyWaitRecord>>([]);
    }

    /// <summary>Simple in-memory <see cref="INotifyWaitStore"/> test double — no SQLite needed here.</summary>
    private sealed class InMemoryNotifyWaitStore : INotifyWaitStore
    {
        private readonly ConcurrentDictionary<string, NotifyWaitRecord> _rows = new(StringComparer.Ordinal);

        public Func<Task>? BeforeSave { get; set; }
        public bool FailDelete { get; set; }

        public async Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default)
        {
            if (BeforeSave is not null)
                await BeforeSave();
            _rows[record.WaitId] = record;
        }

        public Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default)
        {
            if (FailDelete)
                throw new IOException("Fixture durable delete failed");
            _rows.TryRemove(waitId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default)
        {
            IReadOnlyList<NotifyWaitRecord> result =
            [
                .. _rows.Values.Where(r => r.ThreadId == threadId && r.Status == "active"),
            ];
            return Task.FromResult(result);
        }
    }
}
