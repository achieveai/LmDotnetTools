using System.Collections.Concurrent;
using System.Text.Json;
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
    public async Task Fire_ResolvesWait_WithFiredStatus_ViaDummySource()
    {
        var resolver = new RecordingResolver();
        var source = new ManualTriggerSource();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.Register(Registration("dummy", source));

        var armed = await runtime.ArmAsync("tc1", "dummy", "{}", "10m", "wait-a", CancellationToken.None);
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
        await runtime.ArmAsync("tc-timeout", "dummy", "{}", "300ms", null, CancellationToken.None);

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

        await runtime.ArmAsync("tc-cancel", "dummy", "{}", "10m", "cancel-me", CancellationToken.None);

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

        await runtime.ArmAsync("tc-idem", "dummy", "{}", "10m", null, CancellationToken.None);
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

        await runtime.ArmAsync("a", "dummy", "{}", "10m", "batch", CancellationToken.None);
        await runtime.ArmAsync("b", "dummy", "{}", "10m", "batch", CancellationToken.None);

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

            await runtime.ArmAsync($"race-{i}", "dummy", "{}", "10m", null, CancellationToken.None);

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

        (await runtime.ArmAsync("one", "dummy", "{}", "10m", null, CancellationToken.None)).IsArmed.Should().BeTrue();

        var second = await runtime.ArmAsync("two", "dummy", "{}", "10m", null, CancellationToken.None);
        second.IsArmed.Should().BeFalse();
        second.Reason.Should().Be("max_concurrent_waits");
    }

    [Fact]
    public async Task Arm_Rejects_UnknownKind()
    {
        var resolver = new RecordingResolver();
        await using var runtime = new TriggerRuntime(FastOptions(), resolver.Resolve);
        runtime.RegisterBuiltIns();

        var result = await runtime.ArmAsync("x", "no-such-kind", "{}", "10m", null, CancellationToken.None);
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

        var result = await runtime.ArmAsync("x", "dummy", "{}", "not-a-time", null, CancellationToken.None);
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

        await runtime.ArmAsync("tc-cap", "dummy", "{}", "10m", null, CancellationToken.None);
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

        await runtime.ArmAsync("tc-retry", "dummy", "{}", "10m", null, CancellationToken.None);
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
}
