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

        if (int.TryParse(section["MaxRetries"], out var maxRetries) && maxRetries >= 0)
        {
            options = options with { MaxRetries = maxRetries };
        }

        if (int.TryParse(section["MaxRetryAfterSeconds"], out var maxRetryAfter) && maxRetryAfter >= 0)
        {
            options = options with { MaxRetryAfter = TimeSpan.FromSeconds(maxRetryAfter) };
        }

        return options;
    }
}
