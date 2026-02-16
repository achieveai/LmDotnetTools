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
}
