namespace LmStreaming.Sample.Services;

/// <summary>
///     The per-provider reasoning replay switch (#769): <c>LmStreaming:ReasoningReplay:{providerId}</c> set to
///     <c>true</c> keeps a Responses model's encrypted reasoning and sends it back to the same model.
/// </summary>
/// <remarks>
///     A missing or unreadable value is off, because a backend may refuse a replayed item. The value is read when
///     a provider agent is built, and the pool keeps reusing that agent. A change therefore reaches only agents
///     built after it. To stop all replay, change the value and restart the host.
/// </remarks>
public static class ReasoningReplaySettings
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "LmStreaming:ReasoningReplay";

    /// <summary>True only when this provider's value reads as <c>true</c>.</summary>
    public static bool IsOn(IConfiguration configuration, string? providerId)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !string.IsNullOrEmpty(providerId)
            && bool.TryParse(configuration[$"{SectionName}:{providerId}"], out var on)
            && on;
    }
}
