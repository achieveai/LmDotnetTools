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
}
