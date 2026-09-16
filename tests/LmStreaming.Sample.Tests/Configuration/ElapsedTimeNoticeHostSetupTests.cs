using LmStreaming.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Configuration;

/// <summary>
/// Covers the host switch for the experimental elapsed-time notice: on by default for this sample,
/// off with one setting, and the interval flows through to the library record.
/// </summary>
public class ElapsedTimeNoticeHostSetupTests
{
    private static ElapsedTimeNoticeHostOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return ElapsedTimeNoticeHostSetup.BindOptions(configuration);
    }

    [Fact]
    public void MissingSection_IsOnWithAOneMinuteInterval()
    {
        var setup = ElapsedTimeNoticeHostSetup.Create(Bind([]));

        setup.Should().NotBeNull("the sample opts in by default");
        setup!.Interval.Should().Be(TimeSpan.FromMinutes(1));
        setup.Clock.Should().BeNull("the loop uses the system clock unless a test supplies one");
    }

    [Fact]
    public void EnabledFalse_LeavesTheFeatureAbsent()
    {
        var setup = ElapsedTimeNoticeHostSetup.Create(
            Bind(new Dictionary<string, string?> { ["ElapsedTimeNotice:Enabled"] = "false" })
        );

        setup.Should().BeNull("a null options record is the loop's 'feature absent' contract");
    }

    [Fact]
    public void IntervalSeconds_FlowsThroughToTheRecord()
    {
        var setup = ElapsedTimeNoticeHostSetup.Create(
            Bind(new Dictionary<string, string?> { ["ElapsedTimeNotice:IntervalSeconds"] = "90" })
        );

        setup!.Interval.Should().Be(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void NonPositiveInterval_FailsAtConstruction_RatherThanFallingBack()
    {
        var options = Bind(new Dictionary<string, string?> { ["ElapsedTimeNotice:IntervalSeconds"] = "0" });

        var act = () => ElapsedTimeNoticeHostSetup.Create(options);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
