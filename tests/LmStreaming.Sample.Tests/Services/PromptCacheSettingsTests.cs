using AchieveAi.LmDotnetTools.LmCore.Core;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>The per-model 1-hour switch (#930 P1 item 4): off unless this exact model asks for it.</summary>
public sealed class PromptCacheSettingsTests
{
    private const string Key = "LmStreaming:PromptCache:claude-opus-5:Ttl";

    private static IConfiguration Config(string key, string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)]).Build();

    [Theory]
    [InlineData("1h", PromptCachingMode.AutoOneHour)]
    [InlineData(" 1H ", PromptCachingMode.AutoOneHour)]
    [InlineData("5m", PromptCachingMode.Auto)]
    [InlineData("soon", PromptCachingMode.Auto)]
    [InlineData("", PromptCachingMode.Auto)]
    public void ModeForModel_TurnsOnOnlyForAnExactOneHourValue(string value, PromptCachingMode expected)
    {
        PromptCacheSettings.ModeForModel(Config(Key, value), "claude-opus-5").Should().Be(expected);
    }

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("")]
    [InlineData(null)]
    public void ModeForModel_StaysOnFiveMinutes_ForAnyOtherModel(string? modelId)
    {
        PromptCacheSettings.ModeForModel(Config(Key, "1h"), modelId).Should().Be(PromptCachingMode.Auto);
    }
}
