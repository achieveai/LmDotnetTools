using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

namespace LmStreaming.Sample.Tests.Triggers;

/// <summary>
/// Unit tests for <see cref="ScheduleTriggerSource"/>. Covers interval + cron firing (notify-mode
/// repeat semantics — the source keeps firing until disposed) and arm-time argument validation
/// (exactly one of cron/intervalSeconds, interval floor).
/// </summary>
public class ScheduleTriggerSourceTests
{
    private static TriggerArmRequest ArmReq(string argsJson) =>
        new()
        {
            WaitId = "tc-" + Guid.NewGuid().ToString("N"),
            Kind = ScheduleTriggerSource.KindName,
            ArgsJson = argsJson,
            ArmedAt = DateTimeOffset.UtcNow,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(10),
        };

    private sealed class NoopSinkImpl : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static readonly NoopSinkImpl NoopSink = new();

    private sealed class CountingSink(Action onFire) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            onFire();
            return ValueTask.CompletedTask;
        }
    }

    private static CountingSink SinkCounting(Action onFire) => new(onFire);

    private sealed class SignalingSink(TaskCompletionSource tcs) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private static SignalingSink SinkThatSignals(TaskCompletionSource tcs) => new(tcs);

    [Fact]
    public async Task Interval_FiresRepeatedly()
    {
        var src = new ScheduleTriggerSource();
        var fires = 0;
        var sink = SinkCounting(() => Interlocked.Increment(ref fires));
        await using var handle = await src.ArmAsync(
            ArmReq("""{"intervalSeconds":1}"""), sink, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        fires.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Cron_FiresOnSchedule()
    {
        var src = new ScheduleTriggerSource();
        var fired = new TaskCompletionSource();
        var sink = SinkThatSignals(fired);
        // "* * * * * *" every second (Cronos with seconds enabled)
        await using var handle = await src.ArmAsync(
            ArmReq("""{"cron":"* * * * * *"}"""), sink, CancellationToken.None);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Arm_Rejects_WhenBothCronAndInterval()
    {
        var src = new ScheduleTriggerSource();
        var act = () =>
            src.ArmAsync(
                    ArmReq("""{"cron":"* * * * *","intervalSeconds":5}"""),
                    NoopSink,
                    CancellationToken.None)
                .AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Arm_Rejects_IntervalBelowFloor()
    {
        var src = new ScheduleTriggerSource();
        var act = () =>
            src.ArmAsync(ArmReq("""{"intervalSeconds":0}"""), NoopSink, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Arm_Rejects_InvalidCronExpression()
    {
        var src = new ScheduleTriggerSource();
        var act = () =>
            src.ArmAsync(ArmReq("""{"cron":"not a cron"}"""), NoopSink, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Arm_Rejects_WhenNeitherCronNorInterval()
    {
        var src = new ScheduleTriggerSource();
        var act = () => src.ArmAsync(ArmReq("{}"), NoopSink, CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Dispose_StopsFurtherFires()
    {
        var src = new ScheduleTriggerSource();
        var fires = 0;
        var sink = SinkCounting(() => Interlocked.Increment(ref fires));

        var handle = await src.ArmAsync(ArmReq("""{"intervalSeconds":1}"""), sink, CancellationToken.None);
        await handle.DisposeAsync();

        var observedAtDispose = Volatile.Read(ref fires);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Volatile.Read(ref fires).Should().Be(observedAtDispose, "a disposed handle must never fire again");
    }
}
