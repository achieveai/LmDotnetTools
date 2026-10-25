using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The conversation-scoped switch that gives a thread a read-only-egress sandbox: every allow rule that reaches
/// an ADO host keeps only GET and HEAD (<see cref="WorkspaceRef.ReadOnlyEgress"/>). The key it is stored under
/// at provision, and the read that turns it back into a value when the thread's agent is built. Kept together
/// for the reason <see cref="ConversationSubAgentModel"/> gives.
/// <para>
/// A judge thread (Reviewd's <c>judge run</c>) must read ADO and never write to it, yet it runs on the same
/// workspace and app id as the review lanes, whose sandboxes must keep full egress. So the split is per thread.
/// </para>
/// </summary>
public static class ConversationReadOnlyEgress
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding
    /// <c>ProvisionConversationRequest.ReadOnlyEgress</c>. Written only when true, and read back on every agent
    /// (re)creation for that thread, so the choice survives a restart and a mode or provider switch.
    /// </summary>
    public const string PropertyKey = "sample.readOnlyEgress";

    /// <summary>
    /// Whether the conversation was provisioned with read-only egress. Absence or a missing thread reads as
    /// false, the behaviour every conversation had before. Accepts the <see cref="JsonElement"/> the production
    /// store hands back as well as the in-memory <see cref="bool"/>.
    /// <para>
    /// Fails closed, unlike <see cref="ConversationClientTools.ReadDisabledAsync"/>: any other stored value
    /// throws. Reading an unreadable security bit as false would silently give the thread full write.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The stored value is not a boolean.</exception>
    public static async Task<bool> ReadAsync(IConversationStore store, string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return false;
        }

        return raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new InvalidOperationException(
                $"Conversation '{threadId}' has an unreadable '{PropertyKey}' value "
                    + $"({(raw is JsonElement element ? element.ValueKind.ToString() : raw?.GetType().Name ?? "null")}); "
                    + "refusing to build its agent rather than give it full egress."
            ),
        };
    }
}
