using System.Globalization;
using AchieveAi.LmDotnetTools.LmCore.Http;

namespace LmStreaming.Sample.Services;

/// <summary>
///     The host's provider HTTP retry policy, read from the <c>LmStreaming:ProviderRetry</c> section and
///     handed to every provider client this host builds (#930 P1 item 1).
/// </summary>
/// <remarks>
///     Two knobs, both optional: <c>MaxRetries</c> (retries after the first attempt) and
///     <c>MaxRetryAfterSeconds</c> (the longest server-requested wait honored; a longer one fails the call at
///     once). An absent, unreadable or out-of-range value keeps <see cref="RetryOptions.Default" />, so a host
///     with no section behaves exactly as before.
/// </remarks>
public static class ProviderRetrySettings
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "LmStreaming:ProviderRetry";

    /// <summary>Reads the policy, falling back to the default for each knob it cannot read.</summary>
    public static RetryOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var options = RetryOptions.Default;

        if (ReadNonNegative(section, "MaxRetries") is { } maxRetries)
        {
            options = options with { MaxRetries = maxRetries };
        }

        if (ReadNonNegative(section, "MaxRetryAfterSeconds") is { } maxRetryAfter)
        {
            options = options with { MaxRetryAfter = TimeSpan.FromSeconds(maxRetryAfter) };
        }

        return options;
    }

    /// <summary>The knob's value when it is a whole number of zero or more; otherwise null.</summary>
    private static int? ReadNonNegative(IConfigurationSection section, string key) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
}
