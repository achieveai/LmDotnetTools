using System.Text.Json;

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
/// That is not hypothetical: it is exactly how <c>SystemPromptAppendix</c> (#49) and
/// <c>SubAgentModelId</c> (#45/#118) both shipped inert AFTER being given readers. The daemon's review
/// methodology still did not reach the model and sub-agents still did not leave the parent model, behind
/// suites that were fully green. Use this helper for every provisioned property; do not hand-roll the
/// type test. <c>MultiTurnAgentPool.TryNormalizeStringValue</c> is the same logic for the workspace and
/// provider bindings — those two worked precisely because they had it.
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
        var extracted = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(extracted) ? null : extracted.Trim();
    }
}
