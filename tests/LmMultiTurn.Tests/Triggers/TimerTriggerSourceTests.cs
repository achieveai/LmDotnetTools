using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers.Sources;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// Unit tests for the built-in one-shot <see cref="TimerTriggerSource"/>: it fires exactly once at
/// the resolved instant (from <c>delay</c>, <c>deadline</c>, or the wait ceiling) and disposal
/// before firing suppresses the fire.
/// </summary>
public class TimerTriggerSourceTests
{
    private sealed class CapturingSink : ITriggerEventSink
    {
        private readonly TaskCompletionSource<TriggerFireEvent> _fired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TriggerFireEvent> Fired => _fired.Task;

        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            _fired.TrySetResult(fire);
            return ValueTask.CompletedTask;
        }
    }

    private static TriggerArmRequest Request(string argsJson, DateTimeOffset armedAt, DateTimeOffset deadline) => new()
    {
        WaitId = "tc",
        Kind = TimerTriggerSource.KindName,
        ArgsJson = argsJson,
        ArmedAt = armedAt,
        Deadline = deadline,
    };

    [Fact]
    public async Task Fires_AfterDelay()
    {
        var source = new TimerTriggerSource();
        var sink = new CapturingSink();
        var now = DateTimeOffset.UtcNow;

        await using var handle = await source.ArmAsync(
            Request("{\"delay\":\"200ms\"}", now, now.AddMinutes(10)), sink, CancellationToken.None);

        (await sink.Fired.WaitAsync(TimeSpan.FromSeconds(5))).Should().NotBeNull();
    }

    [Fact]
    public async Task Fires_AtCeiling_WhenNoArgs()
    {
        var source = new TimerTriggerSource();
        var sink = new CapturingSink();
        var now = DateTimeOffset.UtcNow;

        // Empty args → the timer defaults to firing at the wait's ceiling deadline.
        await using var handle = await source.ArmAsync(
            Request("{}", now, now.AddMilliseconds(200)), sink, CancellationToken.None);

        (await sink.Fired.WaitAsync(TimeSpan.FromSeconds(5))).Should().NotBeNull();
    }

    [Fact]
    public async Task DoesNotFire_WhenDisposedBeforeDeadline()
    {
        var source = new TimerTriggerSource();
        var sink = new CapturingSink();
        var now = DateTimeOffset.UtcNow;

        var handle = await source.ArmAsync(
            Request("{\"delay\":\"5s\"}", now, now.AddMinutes(10)), sink, CancellationToken.None);

        await handle.DisposeAsync();

        var fired = await Task.WhenAny(sink.Fired, Task.Delay(400));
        (fired == sink.Fired).Should().BeFalse("a disposed timer must not fire");
    }

    [Fact]
    public async Task Fires_Immediately_WhenDelayAlreadyElapsed()
    {
        var source = new TimerTriggerSource();
        var sink = new CapturingSink();
        // Armed 30 minutes ago with a 5-minute delay → already elapsed → fires ~immediately.
        var armedAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        await using var handle = await source.ArmAsync(
            Request("{\"delay\":\"5m\"}", armedAt, armedAt.AddMinutes(10)), sink, CancellationToken.None);

        (await sink.Fired.WaitAsync(TimeSpan.FromSeconds(5))).Should().NotBeNull();
    }
}
