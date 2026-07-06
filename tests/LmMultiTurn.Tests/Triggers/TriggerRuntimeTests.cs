using System.Collections.Concurrent;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// Unit tests for <see cref="TriggerRuntime"/>: the lifecycle state machine, single-resolution
/// latch, ceiling timeout, cancellation cleanup, bounded concurrency, payload caps, and restart
/// reconciliation — exercised directly against a controllable dummy source, no agent loop.
/// </summary>
public class TriggerRuntimeTests
{
    private static string ReadStatus(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    private static TriggerOptions FastOptions(int maxConcurrent = 16) => new()
    {
        MaxConcurrentWaits = maxConcurrent,
        GateAcquireTimeout = TimeSpan.FromMilliseconds(150),
        MaxBlockWaitDuration = TimeSpan.FromMinutes(15),
    };

    [Fact]
    public async Task Ctor_ThreeArgBinaryCompatOverload_ConstructsAndArmsBlockWait_NoNotify()
    {
        // Regression: this PR inserted `notify` before `logger` in the primary ctor, breaking the
        // pre-existing positional (options, resolve, logger) callers. The compat overload must
        // still construct a working runtime that arms a block wait (no notify delegate needed).
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve, logger: null);
        runtime.Register(Registration("dummy", source));

        var armed = await runtime.ArmAsync("tc-compat", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        armed.IsArmed.Should().BeTrue("the 3-arg (options, resolve, logger) ctor must still construct a fully working runtime");

        await source.FireAsync("hello");

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("fired");
    }

    [Fact]
    public void Ctor_Throws_WhenNotifyWaitStoreSet_WithoutThreadId()
    {
        var options = new TriggerOptions { NotifyWaitStore = new NoopNotifyWaitStore(), ThreadId = null };

        var act = () => new TriggerRuntime(options, resolve: (_, _, _, _) => Task.CompletedTask);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("options");
    }

    [Fact]
    public void Ctor_DoesNotThrow_WhenNotifyWaitStoreAndThreadIdBothSet()
    {
        var options = new TriggerOptions { NotifyWaitStore = new NoopNotifyWaitStore(), ThreadId = "thread-1" };

        var act = () => new TriggerRuntime(options, resolve: (_, _, _, _) => Task.CompletedTask);

        act.Should().NotThrow();
    }

    /// <summary>Minimal no-op <see cref="INotifyWaitStore"/> for ctor-guard tests — never invoked.</summary>
    private sealed class NoopNotifyWaitStore : INotifyWaitStore
    {
        public Task SaveAsync(NotifyWaitRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string waitId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<NotifyWaitRecord>> LoadActiveAsync(string threadId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotifyWaitRecord>>([]);
    }

    [Fact]
    public async Task Fire_ResolvesWait_WithFiredStatus_ViaDummySource()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        var armed = await runtime.ArmAsync("tc1", "dummy", "{}", "10m", "wait-a", WaitMode.Block, maxFires: null, CancellationToken.None);
        armed.IsArmed.Should().BeTrue("an external source registered through the seam must arm");

        await source.FireAsync("hello");

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("fired");
        payload.Should().Contain("hello");
        resolver.Count.Should().Be(1);
        runtime.ListWaits().Should().BeEmpty("a fired wait is removed from the armed set");
    }

    [Fact]
    public async Task Timeout_ResolvesWait_WithTimedOutStatus_WhenSourceNeverFires()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        // Never fire; the runtime ceiling timer must resolve the wait as timed_out.
        await runtime.ArmAsync("tc-timeout", "dummy", "{}", "300ms", null, WaitMode.Block, maxFires: null, CancellationToken.None);

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("timed_out");
        resolver.Count.Should().Be(1);
    }

    [Fact]
    public async Task Cancel_ResolvesWait_WithCancelledStatus_AndDisposesSource_NoLaterFire()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        await runtime.ArmAsync("tc-cancel", "dummy", "{}", "10m", "cancel-me", WaitMode.Block, maxFires: null, CancellationToken.None);

        var cancelled = await runtime.CancelWaitsAsync("tc-cancel", null, null, CancellationToken.None);
        cancelled.Should().Be(1);

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("cancelled");
        source.Disposed.Should().BeTrue("cancellation must dispose the underlying source");

        // A fire arriving after cancellation must be a no-op (source already disposed / latch closed).
        await source.FireAsync("late");
        await Task.Delay(100);
        resolver.Count.Should().Be(1, "no delivery may occur after a terminal cancellation");
    }

    [Fact]
    public async Task CancelWait_IsIdempotent_ForUnknownAndAlreadyTerminal()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        // Unknown id/label/kind → nothing cancelled, no throw.
        (await runtime.CancelWaitsAsync("nope", null, null, CancellationToken.None)).Should().Be(0);

        await runtime.ArmAsync("tc-idem", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        (await runtime.CancelWaitsAsync("tc-idem", null, null, CancellationToken.None)).Should().Be(1);
        // Second cancel of an already-terminal wait → 0.
        (await runtime.CancelWaitsAsync("tc-idem", null, null, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task CancelWait_BySelector_CancelsAllMatching_ByKind()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        await runtime.ArmAsync("a", "dummy", "{}", "10m", "batch", WaitMode.Block, maxFires: null, CancellationToken.None);
        await runtime.ArmAsync("b", "dummy", "{}", "10m", "batch", WaitMode.Block, maxFires: null, CancellationToken.None);

        var byLabel = await runtime.CancelWaitsAsync(null, "batch", null, CancellationToken.None);
        byLabel.Should().Be(2);
        runtime.ListWaits().Should().BeEmpty();
    }

    [Fact]
    public async Task ConcurrentFireAndCancel_ResolveExactlyOnce()
    {
        // The single-resolution latch must ensure exactly one terminal transition even when a
        // fire and a cancel race.
        for (var i = 0; i < 25; i++)
        {
            var resolver = new RecordingResolver();
            var source = new ManualTriggerSource();
            await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
            runtime.Register(Registration("dummy", source));

            await runtime.ArmAsync($"race-{i}", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);

            var fire = Task.Run(() => source.FireAsync("x"));
            var cancel = Task.Run(() => runtime.CancelWaitsAsync($"race-{i}", null, null, CancellationToken.None));
            await Task.WhenAll(fire, cancel);

            await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(30);
            resolver.Count.Should().Be(1, "fire/cancel race must produce exactly one resolution");
        }
    }

    [Fact]
    public async Task Arm_Rejects_WhenMaxConcurrentWaitsReached()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(maxConcurrent: 1), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        (await runtime.ArmAsync("one", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None)).IsArmed.Should().BeTrue();

        var second = await runtime.ArmAsync("two", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        second.IsArmed.Should().BeFalse();
        second.Reason.Should().Be("max_concurrent_waits");
    }

    [Fact]
    public async Task Arm_Rejects_UnknownKind()
    {
        var resolver = new RecordingResolver();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.RegisterBuiltIns();

        var result = await runtime.ArmAsync("x", "no-such-kind", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        result.IsArmed.Should().BeFalse();
        result.Reason.Should().Be("unknown_kind");
    }

    [Fact]
    public async Task Arm_Rejects_InvalidTimeout()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        var result = await runtime.ArmAsync("x", "dummy", "{}", "not-a-time", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        result.IsArmed.Should().BeFalse();
        result.Reason.Should().Be("invalid_timeout");
    }

    [Fact]
    public async Task Fire_Payload_IsTruncated_AtCap()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        var options = FastOptions() with { MaxPayloadBytes = 64 };
        await using var runtime = new TriggerRuntime(options, resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        await runtime.ArmAsync("tc-cap", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        await source.FireAsync(new string('A', 10_000));

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        payload.Should().Contain("[truncated to 64 bytes]");
        payload.Length.Should().BeLessThan(10_000);
    }

    [Fact]
    public async Task Deliver_RetriesTransientMissingPlaceholder_ThenSucceeds()
    {
        // Simulates the register-window race: the deferred placeholder isn't in history for the
        // first few resolve attempts. The runtime must retry rather than drop the resolution.
        var attempts = 0;
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Resolve(string id, string payload, bool isError, CancellationToken ct)
        {
            if (Interlocked.Increment(ref attempts) < 3)
            {
                throw new InvalidOperationException($"no matching deferred call for '{id}'");
            }
            delivered.TrySetResult(payload);
            return Task.CompletedTask;
        }

        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), Resolve);
        runtime.Register(Registration("dummy", source));

        await runtime.ArmAsync("tc-retry", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        await source.FireAsync("x");

        var payload = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("fired");
        attempts.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ReconcileRestored_ExpiredTimer_ResolvesImmediately()
    {
        var resolver = new RecordingResolver();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.RegisterBuiltIns(); // timer supports restore

        // A block timer wait armed long ago with a short timeout: on restart it has elapsed.
        var longAgo = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var waitArgs = JsonSerializer.Serialize(new { kind = "timer", args = new { }, timeout = "5m" });
        await runtime.ReconcileRestoredAsync(
            [new RestoredWait("tc-restore", waitArgs, longAgo)],
            CancellationToken.None);

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        // Elapsed-while-offline timer fires as soon as it is re-armed.
        ReadStatus(payload).Should().Be("fired");
    }

    [Fact]
    public async Task ReconcileRestored_NonRestorableKind_ResolvesTriggerLostOnRestart()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("volatile", source, supportsRestore: false));

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var waitArgs = JsonSerializer.Serialize(new { kind = "volatile", args = new { }, timeout = "10m" });
        await runtime.ReconcileRestoredAsync(
            [new RestoredWait("tc-lost", waitArgs, now)],
            CancellationToken.None);

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("failed");
        payload.Should().Contain("trigger_lost_on_restart");
    }

    [Fact]
    public async Task ReconcileRestored_ExpiredTimer_LeavesNoZombieWait()
    {
        // Regression: an already-elapsed restored timer must fire cleanly and leave no armed entry
        // (an earlier version fired synchronously inside ArmAsync, re-inserting a terminal zombie).
        var resolver = new RecordingResolver();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.RegisterBuiltIns();

        var longAgo = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var waitArgs = JsonSerializer.Serialize(new { kind = "timer", args = new { }, timeout = "5m" });
        await runtime.ReconcileRestoredAsync(
            [new RestoredWait("tc-zombie", waitArgs, longAgo)],
            CancellationToken.None);

        await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        runtime.ListWaits().Should().BeEmpty("an expired restored timer must not leave an armed entry");
    }

    [Fact]
    public async Task ReconcileRestored_RejectedReArm_ResolvesInsteadOfHanging()
    {
        // Regression: if re-arming a restored wait is rejected (e.g. concurrency limit), the wait
        // must be resolved (failed), never left parked forever.
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        // One slot, already taken by a live wait, so the restored re-arm cannot acquire it.
        await using var runtime = new TriggerRuntime(FastOptions(maxConcurrent: 1), resolver.Resolve);
        runtime.Register(Registration("dummy", source));
        await runtime.ArmAsync("live", "dummy", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var waitArgs = JsonSerializer.Serialize(new { kind = "dummy", args = new { }, timeout = "10m" });
        await runtime.ReconcileRestoredAsync(
            [new RestoredWait("tc-reject", waitArgs, now)],
            CancellationToken.None);

        var payload = await resolver.FirstPayload.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("failed");
    }

    [Fact]
    public async Task Fire_Delivers_EvenWhenSourceTokenCancelsOnDispose_AndDeliveryRetries()
    {
        // Regression for the fire-path token bug: disposing the source (which cancels the source's
        // token) must not abort delivery, even when the first resolve attempt is transiently
        // rejected (the register-window). Delivery uses an uncancellable token.
        var attempts = 0;
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Resolve(string id, string payload, bool isError, CancellationToken ct)
        {
            if (Interlocked.Increment(ref attempts) < 3)
            {
                throw new InvalidOperationException($"no matching deferred call for '{id}'");
            }
            delivered.TrySetResult(payload);
            return Task.CompletedTask;
        }

        var source = new TokenCancellingSource();
        await using var runtime = new TriggerRuntime(FastOptions(), Resolve);
        runtime.Register(Registration("cancel_on_dispose", source));

        await runtime.ArmAsync("tc-token", "cancel_on_dispose", "{}", "10m", null, WaitMode.Block, maxFires: null, CancellationToken.None);
        await source.FireAsync();

        var payload = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ReadStatus(payload).Should().Be("fired");
    }

    private static TriggerSourceRegistration Registration(string kind, ITriggerSource source, bool supportsRestore = true) => new()
    {
        Kind = kind,
        Description = $"test source {kind}",
        ArgsSchema = "{}",
        Capabilities = new TriggerCapabilities(SupportsBlock: true, SupportsNotify: false, SupportsRestore: supportsRestore),
        Source = source,
    };

    /// <summary>Records every resolve delegate invocation and signals the first payload.</summary>
    private sealed class RecordingResolver
    {
        private readonly ConcurrentQueue<(string, string, bool)> _calls = new();
        private readonly TaskCompletionSource<string> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> FirstPayload => _first.Task;
        public int Count => _calls.Count;

        public Task Resolve(string toolCallId, string payload, bool isError, CancellationToken ct)
        {
            _calls.Enqueue((toolCallId, payload, isError));
            _first.TrySetResult(payload);
            return Task.CompletedTask;
        }
    }

    /// <summary>A source whose single fire is triggered manually by the test.</summary>
    private sealed class ManualTriggerSource : ITriggerSource
    {
        private volatile ITriggerEventSink? _sink;
        private readonly Handle _handle = new();

        public bool Disposed => _handle.Disposed;

        public ValueTask<IArmedTrigger> ArmAsync(TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
        {
            _sink = eventSink;
            _handle.WaitId = request.WaitId;
            return ValueTask.FromResult<IArmedTrigger>(_handle);
        }

        public async Task FireAsync(string? payload)
        {
            var sink = _sink;
            if (sink != null && !_handle.Disposed)
            {
                await sink.FireAsync(new TriggerFireEvent(payload), CancellationToken.None);
            }
        }

        private sealed class Handle : IArmedTrigger
        {
            public string WaitId { get; set; } = string.Empty;
            public bool Disposed { get; private set; }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// A source that fires with a token it cancels on disposal — mimics the built-in timer, whose
    /// DisposeAsync cancels the token the fire callback carries. Used to prove delivery is robust to
    /// that cancellation.
    /// </summary>
    private sealed class TokenCancellingSource : ITriggerSource
    {
        private readonly Handle _handle = new();
        private volatile ITriggerEventSink? _sink;

        public ValueTask<IArmedTrigger> ArmAsync(TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
        {
            _sink = eventSink;
            return ValueTask.FromResult<IArmedTrigger>(_handle);
        }

        public async Task FireAsync()
        {
            var sink = _sink;
            if (sink != null)
            {
                // Fire carries the source's own token, exactly as the timer source does.
                await sink.FireAsync(new TriggerFireEvent("payload"), _handle.Token);
            }
        }

        private sealed class Handle : IArmedTrigger
        {
            private readonly CancellationTokenSource _cts = new();

            public string WaitId { get; } = "tc-token";
            public CancellationToken Token => _cts.Token;

            public ValueTask DisposeAsync()
            {
                // Cancels the token the in-flight fire callback is carrying — the runtime must not
                // let this abort delivery.
                _cts.Cancel();
                _cts.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
