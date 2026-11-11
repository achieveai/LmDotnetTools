namespace AchieveAi.LmDotnetTools.LmCore.Core;

/// <summary>
///     Controls prompt caching behavior for providers that support it.
/// </summary>
public enum PromptCachingMode
{
    /// <summary>No prompt caching (default).</summary>
    Off = 0,

    /// <summary>Automatically apply cache breakpoints (provider decides placement).</summary>
    Auto = 1,

    /// <summary>
    ///     As <see cref="Auto" />, but the entries live for 1 hour instead of 5 minutes. A 1-hour write costs
    ///     2x base input against 1.25x, so this pays only when conversations pause for 5 to 60 minutes.
    /// </summary>
    AutoOneHour = 2,
}
