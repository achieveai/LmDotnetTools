using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Reads a string out of a thread's <c>ThreadMetadata.Properties</c> bag.
/// <para>
/// The bag is <c>ImmutableDictionary&lt;string, object&gt;</c> and the production store
/// (<c>FileConversationStore</c>, wired unconditionally in <c>Program.cs</c>) round-trips it through
/// <c>System.Text.Json</c>. A value written as a <see cref="string"/> therefore comes back as a
/// <see cref="JsonElement"/>, so <c>raw is string</c> is <b>false for every property that has survived a
/// write</b> — which is every property any reader actually sees. An in-memory or mocked store keeps the
/// original <see cref="string"/> reference and never reproduces this, so a reader with the bug passes
/// every unit test and returns null in production.
/// </para>
/// <para>
/// <c>MultiTurnAgentPool.TryNormalizeStringValue</c> is the same logic for the thread's workspace and
/// provider bindings — those two work precisely because they have it. Use this helper for every
/// provisioned property; do not hand-roll the type test, and do not accept an in-memory-store test as
/// evidence that a reader works.
/// </para>
/// </summary>
internal static class ThreadPropertyValue
{
    /// <summary>
    /// Returns the trimmed string carried by <paramref name="raw"/>, or <c>null</c> when it is absent,
    /// not a string on either side of the JSON round-trip, or blank.
    /// </summary>
    public static string? AsString(object? raw)
    {
        var extracted = AsStringPreservingEmpty(raw);
        return string.IsNullOrWhiteSpace(extracted) ? null : extracted.Trim();
    }

    /// <summary>
    /// Returns the exact string carried by <paramref name="raw"/>, including an explicit empty value, or
    /// <c>null</c> when it is absent or not a string. Use only where empty and absent have distinct semantics.
    /// </summary>
    public static string? AsStringPreservingEmpty(object? raw) =>
        raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

    /// <summary>
    /// Reads a security switch stored as a boolean under <paramref name="key"/>: absence or a missing thread reads as
    /// <see langword="false"/>. Accepts the <see cref="JsonElement"/> the production store hands back as well as the
    /// in-memory <see cref="bool"/>. Fails closed: any other stored value throws, because reading an unreadable switch
    /// as false would quietly drop the protection it stands for.
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="threadId">The conversation.</param>
    /// <param name="key">The property key the switch is stored under.</param>
    /// <param name="refusal">What reading it as false would do, for the exception message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The stored value is not a boolean.</exception>
    public static async Task<bool> ReadFailClosedBoolAsync(
        IConversationStore store,
        string threadId,
        string key,
        string refusal,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(key, out var raw))
        {
            return false;
        }

        return raw switch
        {
            bool value => value,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => throw new InvalidOperationException(
                $"Conversation '{threadId}' has an unreadable '{key}' value "
                    + $"({(raw is JsonElement element ? element.ValueKind.ToString() : raw?.GetType().Name ?? "null")}); "
                    + $"refusing to build its agent rather than {refusal}."
            ),
        };
    }
}
