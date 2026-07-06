using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// Unit tests for Task 13: notify-mode waits persist a row on arm, delete it on terminal, and
/// <see cref="TriggerRuntime.RestoreNotifyWaitsAsync"/> reconciles rows left over from a restart —
/// re-arming restorable kinds from their remaining fire budget/TTL, and delivering one
/// <c>trigger_lost_on_restart</c> envelope (then deleting the row) for non-restorable ones. Block
/// mode and the already-tested notify fire/maxFires/cancel lifecycle (see
/// <see cref="TriggerRuntimeNotifyTests"/>) must remain unaffected.
/// </summary>
public class TriggerRuntimeNotifyRestoreTests
{
    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long FutureUnixMs(int minutes) => DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeMilliseconds();

    private static (TriggerRuntime rt, ManualTriggerSource src, List<(string payload, bool isError)> notified) BuildRuntime(
        INotifyWaitStore store, string threadId, bool restorableManual)
    {
        var notified = new List<(string, bool)>();
        var src = new ManualTriggerSource();
        var options = new TriggerOptions { NotifyWaitStore = store, ThreadId = threadId };
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
            Capabilities = restorableManual
                ? new TriggerCapabilities(SupportsBlock: true, SupportsNotify: true, SupportsRestore: true)
                : ManualTriggerSource.Caps, // SupportsRestore: false
            Source = src,
        });
        rt.Register(new TriggerSourceRegistration
        {
            Kind = "manual-restorable",
            Description = "test",
            ArgsSchema = "{}",
            Capabilities = new TriggerCapabilities(SupportsBlock: true, SupportsNotify: true, SupportsRestore: true),
            Source = src,
        });

        return (rt, src, notified);
    }

    [Fact]
    public async Task RestoreNotifyWaits_ReArmsRestorableRows()
    {
        var store = new InMemoryNotifyWaitStore();
        await store.SaveAsync(new NotifyWaitRecord("w1", "t", "manual-restorable", "{}", null, null, 0,
            FutureUnixMs(minutes: 30), NowUnixMs(), "active"));

        var (rt, _, _) = BuildRuntime(store, threadId: "t", restorableManual: true);
        await rt.RestoreNotifyWaitsAsync(CancellationToken.None);

        rt.ListWaits().Should().ContainSingle(w => w.WaitId == "w1"); // re-armed
    }

    [Fact]
    public async Task RestoreNotifyWaits_NonRestorable_DeliversTriggerLostAndDeletesRow()
    {
        var store = new InMemoryNotifyWaitStore();
        await store.SaveAsync(new NotifyWaitRecord("w2", "t", "manual", "{}", null, null, 0,
            FutureUnixMs(30), NowUnixMs(), "active")); // "manual" = SupportsRestore:false

        var (rt, _, notified) = BuildRuntime(store, threadId: "t", restorableManual: false);
        await rt.RestoreNotifyWaitsAsync(CancellationToken.None);

        notified.Should().ContainSingle(n => n.payload.Contains("trigger_lost_on_restart"));
        (await store.LoadActiveAsync("t")).Should().BeEmpty(); // row deleted
    }

    [Fact]
    public async Task RestoreNotifyWaits_UnregisteredKind_DeliversTriggerLostAndDeletesRow()
    {
        var store = new InMemoryNotifyWaitStore();
        await store.SaveAsync(new NotifyWaitRecord("w2b", "t", "no-such-kind", "{}", null, null, 0,
            FutureUnixMs(30), NowUnixMs(), "active"));

        var (rt, _, notified) = BuildRuntime(store, threadId: "t", restorableManual: false);
        await rt.RestoreNotifyWaitsAsync(CancellationToken.None);

        notified.Should().ContainSingle(n => n.payload.Contains("trigger_lost_on_restart"));
        (await store.LoadActiveAsync("t")).Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreNotifyWaits_ElapsedTtl_DeliversTimedOutAndDeletesRow()
    {
        var store = new InMemoryNotifyWaitStore();
        // Already-elapsed deadline (armed 2 hours ago with a 1-minute TTL).
        var armedAt = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        var deadline = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(1).ToUnixTimeMilliseconds();
        await store.SaveAsync(new NotifyWaitRecord("w3", "t", "manual-restorable", "{}", null, null, 0,
            deadline, armedAt, "active"));

        var (rt, _, notified) = BuildRuntime(store, threadId: "t", restorableManual: true);
        await rt.RestoreNotifyWaitsAsync(CancellationToken.None);

        notified.Should().ContainSingle(n => n.payload.Contains("timed_out"));
        rt.ListWaits().Should().NotContain(w => w.WaitId == "w3");
        (await store.LoadActiveAsync("t")).Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreNotifyWaits_NoStoreConfigured_IsNoOp()
    {
        var options = new TriggerOptions(); // NotifyWaitStore and ThreadId both null
        var rt = new TriggerRuntime(options, resolve: (_, _, _, _) => Task.CompletedTask);

        // Must not throw even though nothing is configured.
        await rt.RestoreNotifyWaitsAsync(CancellationToken.None);

        rt.ListWaits().Should().BeEmpty();
    }

    [Fact]
    public async Task ArmAsync_NotifyMode_PersistsRowWhenStoreConfigured()
    {
        var store = new InMemoryNotifyWaitStore();
        var (rt, _, _) = BuildRuntime(store, threadId: "t", restorableManual: true);

        var armed = await rt.ArmAsync("w4", "manual-restorable", "{\"x\":1}", "1h", "my-label", WaitMode.Notify, maxFires: 3, CancellationToken.None);

        armed.IsArmed.Should().BeTrue();
        var rows = await store.LoadActiveAsync("t");
        rows.Should().ContainSingle(r =>
            r.WaitId == "w4"
            && r.ThreadId == "t"
            && r.Kind == "manual-restorable"
            && r.Args == "{\"x\":1}"
            && r.Label == "my-label"
            && r.MaxFires == 3
            && r.FiresSoFar == 0
            && r.Status == "active");
    }

    [Fact]
    public async Task NotifyWait_Terminal_DeletesRowFromStore()
    {
        var store = new InMemoryNotifyWaitStore();
        var (rt, _, _) = BuildRuntime(store, threadId: "t", restorableManual: true);

        await rt.ArmAsync("w5", "manual-restorable", "{}", "1h", null, WaitMode.Notify, maxFires: null, CancellationToken.None);
        (await store.LoadActiveAsync("t")).Should().ContainSingle(r => r.WaitId == "w5");

        var cancelled = await rt.CancelWaitsAsync("w5", null, null, CancellationToken.None);

        cancelled.Should().Be(1);
        (await store.LoadActiveAsync("t")).Should().BeEmpty();
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

        public Task DeleteAsync(string waitId, CancellationToken ct = default)
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
