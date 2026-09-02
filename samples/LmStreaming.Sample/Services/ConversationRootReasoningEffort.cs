using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The conversation-scoped reasoning effort requested for the root agent. The value is written at provision
/// and read whenever the hosted agent is built, so it survives process restarts and pool eviction.
/// </summary>
public static class ConversationRootReasoningEffort
{
    public const string PropertyKey = "sample.rootReasoningEffort";

    /// <summary>
    /// Parses a named effort token. Numeric enum values and composite names are rejected because enum
    /// ordinals are not provider effort ranks and must never become wire tokens.
    /// </summary>
    public static bool TryParse(string value, out ReasoningEffort effort)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (value.Trim().ToLowerInvariant())
        {
            case "low":
                effort = ReasoningEffort.Low;
                return true;
            case "medium":
                effort = ReasoningEffort.Medium;
                return true;
            case "high":
                effort = ReasoningEffort.High;
                return true;
            case "xhigh":
                effort = ReasoningEffort.Xhigh;
                return true;
            default:
                effort = default;
                return false;
        }
    }

    public static async Task<string?> ReadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return null;
        }

        return ThreadPropertyValue.AsStringPreservingEmpty(raw);
    }
}
