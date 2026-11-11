namespace LmStreaming.Sample.Services;

/// <summary>
///     The per-provider reasoning replay switch (#769): <c>LmStreaming:ReasoningReplay:{providerId}</c> set to
///     <c>true</c> keeps a Responses model's encrypted reasoning and sends it back to the same model.
/// </summary>
/// <remarks>
///     A missing or unreadable value is off, because a backend may refuse a replayed item. The value is read when
///     a provider agent is built, and the pool keeps reusing that agent. A change therefore reaches only agents
///     built after it. To stop all replay, change the value and restart the host.
///     <para>
///     With the switch on, an opt-in recording (<c>?record=1</c>) holds the blob as plain text, both in the
///     request it sends back and in the response that returned it. The recording is verbatim on purpose, so
///     the blob is not redacted there. Treat such recordings like the conversation store.
///     </para>
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
