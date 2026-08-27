using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Eval;

namespace CodeReviewDaemon.Sample.Tests.Eval;

/// <summary>
/// The three outcomes of reading the eval-sweep knobs: off, on, and a configuration the daemon must
/// refuse to start under.
/// <para>
/// The refusals lived in <c>Program.cs</c>, which is a top-level program — so the only way to reach
/// them was to boot the host with the bad value, and nothing did. That is how the interval's
/// asymmetry survived: a window of <c>-5</c> got a named startup refusal while an interval of
/// <c>-5</c> read as "off" and the daemon came up silently with the sweep not running (#455 item 4).
/// A knob whose wrong value looks exactly like its default value is the one a test has to hold.
/// </para>
/// </summary>
public class EvalSweepConfigurationTests
{
    private static CodeReviewDaemonOptions Options(double intervalMinutes, int window = 1000) =>
        new() { EvalCorpusSweepIntervalMinutes = intervalMinutes, EvalCorpusSweepWindow = window };

    [Fact]
    public void A_zero_interval_is_off()
    {
        EvalSweepConfiguration
            .Resolve(Options(0))
            .Should()
            .BeNull("zero is the default and the one value that means the sweep is not registered");
    }

    [Fact]
    public void A_positive_interval_carries_the_cadence_and_the_window()
    {
        var resolved = EvalSweepConfiguration.Resolve(Options(90, window: 250));

        resolved.Should().NotBeNull();
        resolved!.Value.Interval.Should().Be(TimeSpan.FromMinutes(90));
        resolved.Value.Window.Should().Be(250);
    }

    /// <summary>
    /// The asymmetry #455 names. A negative interval is a typo, not a setting: read as "off" it
    /// produces a daemon that starts cleanly, logs nothing, and never sweeps — and the operator's
    /// evidence that they turned the sweep on is the line they edited.
    /// </summary>
    [Fact]
    public void A_negative_interval_is_refused_by_name_rather_than_read_as_off()
    {
        var resolve = () => EvalSweepConfiguration.Resolve(Options(-5));

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*EvalCorpusSweepIntervalMinutes*");
    }

    /// <summary>
    /// NaN and the infinities are refused on the same line and for a sharper reason: every comparison
    /// against NaN is false, so it would fall through to <c>TimeSpan.FromMinutes</c> and throw there —
    /// naming an argument no operator ever passed, from a stack with no configuration key in it.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void An_interval_that_is_not_a_finite_number_is_refused_by_name(double minutes)
    {
        var resolve = () => EvalSweepConfiguration.Resolve(Options(minutes));

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*EvalCorpusSweepIntervalMinutes*");
    }

    /// <summary>
    /// A finite interval can still be un-runnable. `TimeSpan.FromMinutes` throws `OverflowException`
    /// above `TimeSpan.MaxValue.TotalMinutes` (~1.54e10), so a finiteness-only guard let such a value
    /// through to exactly the failure the NaN case above exists to prevent: an exception naming an
    /// argument no operator ever passed, from a stack holding no configuration key. Refused by name
    /// on the same line instead.
    /// </summary>
    [Theory]
    [InlineData(1e12)]
    [InlineData(1.6e10)]
    public void A_finite_interval_too_large_to_be_a_timespan_is_refused_by_name(double minutes)
    {
        var resolve = () => EvalSweepConfiguration.Resolve(Options(minutes));

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*EvalCorpusSweepIntervalMinutes*");
    }

    /// <summary>
    /// The boundary itself is a cadence, absurd but representable, so it is NOT refused — the guard
    /// draws its line where `TimeSpan` does, not one short of it. Without this, a guard that refused
    /// everything large would pass the test above while quietly moving the boundary.
    /// </summary>
    [Fact]
    public void The_largest_representable_interval_is_still_accepted()
    {
        EvalSweepConfiguration
            .Resolve(Options(TimeSpan.MaxValue.TotalMinutes))
            .Should()
            .NotBeNull();
    }

    [Fact]
    public void A_non_positive_window_beside_a_configured_interval_is_refused_by_name()
    {
        var resolve = () => EvalSweepConfiguration.Resolve(Options(90, window: 0));

        resolve
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*EvalCorpusSweepWindow*");
    }

    /// <summary>
    /// The window is only refused when a cadence asks for it. A deployment with the sweep off has no
    /// use for the window, and refusing to boot over an unused value would make the default harder to
    /// leave alone than to configure.
    /// </summary>
    [Fact]
    public void A_bad_window_beside_a_zero_interval_is_not_a_startup_refusal()
    {
        EvalSweepConfiguration.Resolve(Options(0, window: 0)).Should().BeNull();
    }
}
