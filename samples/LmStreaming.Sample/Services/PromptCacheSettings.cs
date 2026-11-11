using AchieveAi.LmDotnetTools.LmCore.Core;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Which prompt-cache lifetime a conversation's model gets, read per model from the
///     <c>LmStreaming:PromptCache</c> section (#930 P1 item 4).
/// </summary>
/// <remarks>
///     <c>LmStreaming:PromptCache:{modelId}:Ttl</c> set to <c>1h</c> turns on 1-hour caching for that model. A
///     missing or unreadable value keeps the 5-minute <see cref="PromptCachingMode.Auto" />. It is off for every
///     model by default because a 1-hour write costs more: only a model whose conversations often pause for 5
///     to 60 minutes repays it. Set it only for a backend known to accept <c>ttl</c>; a backend that refuses
///     the field fails every turn.
/// </remarks>
public static class PromptCacheSettings
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "LmStreaming:PromptCache";

    /// <summary>The caching mode for <paramref name="modelId" />.</summary>
    public static PromptCachingMode ModeForModel(IConfiguration configuration, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return
            !string.IsNullOrWhiteSpace(modelId)
            && string.Equals(
                configuration[$"{SectionName}:{modelId}:Ttl"]?.Trim(),
                "1h",
                StringComparison.OrdinalIgnoreCase
            )
            ? PromptCachingMode.AutoOneHour
            : PromptCachingMode.Auto;
    }
}
