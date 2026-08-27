using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The conversation-scoped default model for sub-agents: the key it is stored under at provision, and the
/// read that turns it back into a value when the thread's agent is built.
/// <para>
/// Both halves live here deliberately. The sibling knob on this exact path —
/// <see cref="SystemPromptAugmenter.AppendixPropertyKey"/> — declares its key next to a helper that no
/// production code ever calls, so the value is written at provision and read by nothing; the split between
/// "where the key is declared" and "who reads it" is what let that go unnoticed. Keeping the reader beside
/// the key means a future reader can see, in one file, whether this one is wired.
/// </para>
/// </summary>
public static class ConversationSubAgentModel
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding
    /// <c>ProvisionConversationRequest.SubAgentModelId</c>. Written once at provision and read back on every
    /// agent (re)creation for that thread, so the choice survives a process restart and a mode/provider
    /// switch exactly like the thread's workspace binding does.
    /// </summary>
    public const string PropertyKey = "sample.subAgentModelId";

    /// <summary>
    /// Reads the conversation's configured sub-agent model, or <c>null</c> when the thread was provisioned
    /// without one (every conversation created before this field existed, and every UI-created chat).
    /// <para>
    /// Never throws: a missing thread, a missing property, or a non-string value all mean "no conversation
    /// default", which is the pre-existing behavior. Sub-agent routing is not worth failing an agent build
    /// over — a conversation that cannot read this should still run, with every child on the parent model.
    /// </para>
    /// </summary>
    public static async Task<string?> ReadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties
            || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return null;
        }

        return raw is string modelId && !string.IsNullOrWhiteSpace(modelId) ? modelId : null;
    }
}
