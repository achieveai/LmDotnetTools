using AchieveAi.LmDotnetTools.LmCore.Http;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
///     The host's provider retry policy comes from <c>LmStreaming:ProviderRetry</c> (#930 P1 item 1). No
///     section, or a value it cannot read, keeps the library default, so an unconfigured host is unchanged.
/// </summary>
public class ProviderRetrySettingsTests
{
    [Fact]
    public void NoSection_KeepsTheDefault()
    {
        ProviderRetrySettings.Bind(Configure()).Should().Be(RetryOptions.Default);
    }

    [Fact]
    public void ConfiguredValues_SetTheRetryCountAndTheRetryAfterCap()
    {
        var options = ProviderRetrySettings.Bind(
            Configure(
                ("LmStreaming:ProviderRetry:MaxRetries", "5"),
                ("LmStreaming:ProviderRetry:MaxRetryAfterSeconds", "120")
            )
        );

        options.MaxRetries.Should().Be(5);
        options.MaxRetryAfter.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("lots")]
    public void AnUnreadableValue_KeepsThatKnobsDefault(string value)
    {
        var options = ProviderRetrySettings.Bind(
            Configure(
                ("LmStreaming:ProviderRetry:MaxRetries", value),
                ("LmStreaming:ProviderRetry:MaxRetryAfterSeconds", value)
            )
        );

        options.Should().Be(RetryOptions.Default);
    }

    private static IConfiguration Configure(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
