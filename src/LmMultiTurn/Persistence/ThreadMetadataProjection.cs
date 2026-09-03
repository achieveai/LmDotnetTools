using System.Collections.Immutable;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// The three mechanics every projection stored in <see cref="ThreadMetadata.Properties"/> needs:
/// reading its blob back whatever the store handed over, reading its schema version even when that
/// version is newer than this build, and writing it back without disturbing the rest of the bag.
/// </summary>
/// <remarks>
/// <para>
/// Factored out rather than copied. Two projections already share this bag (the todo board and, from
/// #676, the collaboration identity binding) and a third will; a copied
/// <see cref="PersistedSchemaVersion"/> in particular is the kind of duplicate that stays correct
/// right up until one copy's probe name and its record's <c>[JsonPropertyName]</c> disagree, at which
/// point the forward-compatibility guard silently stops guarding.
/// </para>
/// <para>
/// The schema-version probe takes its property name from the caller for exactly that reason: a
/// projection passes the same constant it pins on its record, so the two cannot drift.
/// </para>
/// </remarks>
internal static class ThreadMetadataProjection
{
    /// <summary>
    /// The projection's blob as JSON text, or null when the bag has no such entry.
    /// </summary>
    /// <remarks>
    /// Store-agnostic: an in-memory store hands back the <see cref="string"/> that was written, while
    /// the file and SQLite stores re-hydrate it as a <see cref="JsonElement"/>.
    /// </remarks>
    public static string? RawJson(ThreadMetadata? metadata, string propertyKey)
    {
        if (metadata?.Properties is null || !metadata.Properties.TryGetValue(propertyKey, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// The persisted schema version, read off the raw document so a version this build cannot
    /// deserialize is still legible.
    /// </summary>
    /// <param name="metadata">The row to read.</param>
    /// <param name="propertyKey">The bag key the projection occupies.</param>
    /// <param name="versionPropertyName">
    /// The JSON member holding the version — the same constant the record pins with
    /// <c>[JsonPropertyName]</c>.
    /// </param>
    /// <param name="whenUnversioned">
    /// What to report for a well-formed blob that carries no readable version. Callers pass their own
    /// current version, so an early row that predates the member is treated as "mine", not as newer.
    /// </param>
    /// <returns>
    /// The persisted version, <paramref name="whenUnversioned"/> for a blob with no readable version,
    /// or 0 for an absent or corrupt blob — 0 meaning "nothing worth preserving is there".
    /// </returns>
    public static int PersistedSchemaVersion(
        ThreadMetadata? metadata,
        string propertyKey,
        string versionPropertyName,
        int whenUnversioned
    )
    {
        var json = RawJson(metadata, propertyKey);
        if (json is null)
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(versionPropertyName, out var value)
                && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : whenUnversioned;
        }
        catch (JsonException)
        {
            return 0; // corrupt — treat as absent, allow overwrite
        }
    }

    /// <summary>
    /// Returns <paramref name="existing"/> with one projection written into its property bag.
    /// </summary>
    /// <remarks>
    /// Takes a NON-NULL row by design — there is deliberately no create branch, so no projection
    /// writer can mint an ownership-less conversation even if its own no-mint guard is forgotten.
    /// <see cref="ThreadMetadata.LastUpdated"/> is deliberately NOT bumped: it drives the sidebar's
    /// default ordering, and a background projection write must not float a conversation to the top
    /// of the user's list. <c>SetItem</c>, never a wholesale replace: the bag is shared with the mode
    /// binding, the workspace binding, the usage projection, and the sub-agent ordinal counter.
    /// </remarks>
    public static ThreadMetadata WithProjection(ThreadMetadata existing, string propertyKey, string json)
    {
        var properties = (existing.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(propertyKey, json);

        return existing with
        {
            Properties = properties,
        };
    }
}
