using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Triggers;

/// <summary>
/// Unit tests for <see cref="WaitToolArgs.TryParse"/>'s <c>mode</c>/<c>maxFires</c> parsing: default
/// mode is <see cref="WaitMode.Block"/>, <c>notify</c> mode with a positive <c>maxFires</c> parses,
/// and unknown modes or non-positive <c>maxFires</c> are rejected.
/// </summary>
public class WaitToolArgsTests
{
    [Fact]
    public void TryParse_DefaultsToBlockMode_WhenModeOmitted()
    {
        var ok = WaitToolArgs.TryParse("""{"kind":"timer","timeout":"10m"}""", out var args);
        ok.Should().BeTrue();
        args.Mode.Should().Be(WaitMode.Block);
        args.MaxFires.Should().BeNull();
    }

    [Fact]
    public void TryParse_ReadsNotifyModeAndMaxFires()
    {
        var ok = WaitToolArgs.TryParse(
            """{"kind":"timer","timeout":"1h","mode":"notify","maxFires":3}""", out var args);
        ok.Should().BeTrue();
        args.Mode.Should().Be(WaitMode.Notify);
        args.MaxFires.Should().Be(3);
    }

    [Fact]
    public void TryParse_RejectsUnknownMode()
    {
        WaitToolArgs.TryParse("""{"kind":"timer","timeout":"1h","mode":"bogus"}""", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryParse_RejectsNonPositiveMaxFires()
    {
        WaitToolArgs.TryParse("""{"kind":"timer","timeout":"1h","mode":"notify","maxFires":0}""", out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"kind":"timer","timeout":"1h","mode":123}""")]
    [InlineData("""{"kind":"timer","timeout":"1h","mode":true}""")]
    public void TryParse_RejectsNonStringMode(string json)
    {
        WaitToolArgs.TryParse(json, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_TreatsNullModeAsBlock()
    {
        WaitToolArgs.TryParse("""{"kind":"timer","timeout":"1h","mode":null}""", out var args).Should().BeTrue();
        args.Mode.Should().Be(WaitMode.Block);
    }
}
