using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The conversation-scoped (provision-layer) sandbox environment (spec §5): the key it is stored
/// under at provision, and the read that turns it back into a value.
/// <para>
/// Both halves live here deliberately, matching <see cref="ConversationSubAgentModel"/> — the reader
/// sits beside the key so a future reader can see, in one file, whether this property is wired.
/// </para>
/// </summary>
public static class ConversationSandboxEnv
{
    /// <summary>
    /// Property key in a thread's <c>ThreadMetadata.Properties</c> holding
    /// <c>ProvisionConversationRequest.Env</c>. Written once at provision and read back whenever the
    /// thread's merged sandbox env (workspace &lt; mode &lt; provision) is resolved. Values are plain
    /// text and are never logged.
    /// </summary>
    public const string PropertyKey = "sample.sandboxEnv";

    /// <summary>
    /// Reads the conversation's provisioned env, or <c>null</c> when the thread was provisioned
    /// without one (every conversation created before this field existed, and every UI-created chat).
    /// <para>
    /// <b>Absence</b> never throws: a missing thread, a missing property, or a value <see cref="Parse"/>
    /// cannot interpret all mean "no conversation-layer env", which is the pre-existing behavior.
    /// </para>
    /// <para>
    /// <b>Failure</b> does throw, and deliberately so — this is not a catch-all. A null
    /// <paramref name="store"/> is a wiring bug and throws <see cref="ArgumentNullException"/>, and
    /// whatever the store's own <c>LoadMetadataAsync</c> raises propagates unchanged, matching
    /// <see cref="SystemPromptAugmenter.ReadAppendixAsync"/>, the sibling reader on the same path.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>?> ReadAsync(
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

        return Parse(raw);
    }

    /// <summary>
    /// Interprets a raw property value as a sandbox env map. Accepts an in-memory
    /// <see cref="IReadOnlyDictionary{TKey, TValue}"/> of strings (a fresh write that has not gone
    /// through a store's JSON round-trip — e.g. <see cref="InMemoryConversationStore"/>) or a
    /// <see cref="JsonElement"/> object (what the production <c>FileConversationStore</c> hands back
    /// after round-tripping the property bag through <c>System.Text.Json</c>). Anything else —
    /// including a non-object <see cref="JsonElement"/> or an unrelated type — returns <c>null</c>
    /// rather than throwing, matching every other provisioned-property reader on this path.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Parse(object? raw)
    {
        switch (raw)
        {
            case IReadOnlyDictionary<string, string> dict:
                return dict;
            case JsonElement { ValueKind: JsonValueKind.Object } element:
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    result[property.Name] = property.Value.GetString() ?? string.Empty;
                }

                return result;
            }
            default:
                return null;
        }
    }
}
