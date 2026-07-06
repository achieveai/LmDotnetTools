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
    private static (TriggerRuntime rt, ManualTriggerSource src, List<(string payload, bool isError)> notified)
        BuildRuntime()
    {
        var notified = new List<(string, bool)>();
        var src = new ManualTriggerSource();
        var options = new TriggerOptions();
        var rt = new TriggerRuntime(
            options,
            resolve: (_, _, _, _) => Task.CompletedTask,
            notify: (payload, isError, _) =>
            {
                lock (notified) { notified.Add((payload, isError)); }
                return Task.CompletedTask;
            });
        rt.Register(new TriggerSourceRegistration
        {
            Kind = "manual",
            Description = "test",
            ArgsSchema = "{}",
            Capabilities = ManualTriggerSource.Caps,
            Source = src,
        });
        return (rt, src, notified);
    }

    private static (TriggerRuntime rt, ManualTriggerSource src, List<(string payload, bool isError)> notified, InMemoryNotifyWaitStore store)
        BuildRuntimeWithStore()
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
                lock (notified) { notified.Add((payload, isError)); }
                return Task.CompletedTask;
            });
        rt.Register(new TriggerSourceRegistration
        {
            Kind = "manual",
            Description = "test",
            ArgsSchema = "{}",
            Capabilities = ManualTriggerSource.Caps,
            Source = src,
        });
        return (rt, src, notified, store);
    }

    [Fact]
    public async Task NotifyWait_StaysArmed_AcrossMultipleFires()
    {
        var (rt, src, notified) = BuildRuntime();
        var armed = await rt.ArmAsync("w1", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: null, CancellationToken.None);
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
            notify: (_, _, _) => Task.CompletedTask);
        rt.Register(new TriggerSourceRegistration
        {
            Kind = "manual",
            Description = "test",
            ArgsSchema = "{}",
            Capabilities = ManualTriggerSource.Caps,
            Source = src,
        });

        Func<Task> act = () =>
            rt.ArmAsync("w8", "manual", "{}", "1h", null, WaitMode.Notify, maxFires: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's own token was cancelled mid-persist, so ArmAsync must surface that " +
            "instead of reporting the wait as armed");

        rt.ListWaits().Should().NotContain(
            w => w.WaitId == "w8",
            "the aborted arm must be fully torn down (source disposed, gate released), not left " +
            "dangling as a live-armed wait");
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

        public Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default)
        {
            _rows[record.WaitId] = record;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string threadId, string waitId, CancellationToken ct = default)
        {
            _rows.TryRemove(waitId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default)
        {
            IReadOnlyList<NotifyWaitRecord> result =
                [.. _rows.Values.Where(r => r.ThreadId == threadId && r.Status == "active")];
            return Task.FromResult(result);
        }
    }
}
