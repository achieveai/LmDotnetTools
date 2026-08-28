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
    /// <b>Absence</b> never throws: a missing thread, a missing property or a non-string value all mean
    /// "no conversation default", which is the pre-existing behavior. Sub-agent routing is not worth
    /// failing an agent build over — a conversation with no recorded default should still run, with every
    /// child on the parent model.
    /// </para>
    /// <para>
    /// <b>Failure</b> does throw, and deliberately so — this is not a catch-all. A null
    /// <paramref name="store"/> is a wiring bug and throws <see cref="ArgumentNullException"/>, and
    /// whatever the store's own <c>LoadMetadataAsync</c> raises propagates unchanged:
    /// <c>FileConversationStore</c> swallows only <c>JsonException</c> around its read, so IO and access
    /// failures — and any backing-store failure from another implementation — reach the caller. This
    /// matches <see cref="SystemPromptAugmenter.ReadAppendixAsync"/>, the sibling reader on the same path;
    /// keep the two claims consistent.
    /// </para>
    /// <para>
    /// The value is extracted with <see cref="ThreadPropertyValue.AsString"/> and NOT with a bare
    /// <c>raw is string</c> test. The production store round-trips the property bag through JSON, so a
    /// string written at provision is read back as a <c>JsonElement</c>; a plain type test therefore
    /// returns null for every value that has actually been persisted, while every in-memory-store unit
    /// test still passes. See <see cref="ThreadPropertyValue"/>.
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

        return ThreadPropertyValue.AsString(raw);
    }
}
