using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>The per-provider reasoning replay switch (#769): off unless this provider asks for it.</summary>
public sealed class ReasoningReplaySettingsTests
{
    private const string Key = "LmStreaming:ReasoningReplay:gpt-5.6-terra";

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>(Key, value)]).Build();

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("soon", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOn_TurnsOnOnlyForAReadableTrue(string? value, bool expected)
    {
        ReasoningReplaySettings.IsOn(Config(value), "gpt-5.6-terra").Should().Be(expected);
    }

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOn_StaysOff_ForAnyOtherProvider(string? providerId)
    {
        ReasoningReplaySettings.IsOn(Config("true"), providerId).Should().BeFalse();
    }
}
