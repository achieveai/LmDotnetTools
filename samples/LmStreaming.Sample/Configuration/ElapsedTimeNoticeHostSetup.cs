using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

namespace LmStreaming.Sample.Configuration;

/// <summary>
/// The <c>ElapsedTimeNotice</c> section: the host switch for the experimental elapsed-time notice the
/// loop appends to a run once a minute (see <see cref="ElapsedTimeNoticeOptions"/>). On in the shipped
/// appsettings; <c>ElapsedTimeNotice__Enabled=false</c> turns it off without a code change.
/// </summary>
public sealed record ElapsedTimeNoticeHostOptions
{
    /// <summary>Whether root loops get the notice. Default on for this sample; the library default is off.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Minimum seconds between notices. Must be positive.</summary>
    public int IntervalSeconds { get; init; } = 60;
}

/// <summary>
/// Binds <see cref="ElapsedTimeNoticeHostOptions"/> and turns it into the library's opt-in record, or null
/// when disabled — null is the loop's "feature absent" contract.
/// </summary>
public static class ElapsedTimeNoticeHostSetup
{
    public const string SectionName = "ElapsedTimeNotice";

    /// <summary>Binds the section; an absent section yields the sample defaults (enabled, 60s).</summary>
    public static ElapsedTimeNoticeHostOptions BindOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(SectionName).Get<ElapsedTimeNoticeHostOptions>()
            ?? new ElapsedTimeNoticeHostOptions();
    }

    /// <summary>
    ///     The options handed to a root loop, or null when disabled. A non-positive interval is a
    ///     configuration error and throws here, at construction, rather than silently falling back.
    /// </summary>
    public static ElapsedTimeNoticeOptions? Create(ElapsedTimeNoticeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Enabled
            ? new ElapsedTimeNoticeOptions { Interval = TimeSpan.FromSeconds(options.IntervalSeconds) }
            : null;
    }
}
