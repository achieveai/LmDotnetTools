using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The property-bag conventions the compaction projections share with the usage projection: a value
///     is a JSON STRING under a dotted key, it carries its own schema version, and a version this build
///     does not understand is read as absent and never overwritten (spec 679 §3.5, §8.2).
/// </summary>
internal static class MetadataProjectionJson
{
    /// <summary>The serializer options every compaction projection writes and reads with.</summary>
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>
    ///     The raw JSON under <paramref name="key" />, whether the store handed it back as the string that
    ///     was written (memory) or as a <see cref="JsonElement" /> (file, SQLite).
    /// </summary>
    public static string? RawJson(ThreadMetadata? metadata, string key)
    {
        if (metadata?.Properties is null || !metadata.Properties.TryGetValue(key, out var raw) || raw is null)
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
    ///     Reads the <c>schema_version</c> under <paramref name="key" /> even when the rest of the value is
    ///     unreadable to this build: 0 when absent or corrupt (safe to overwrite),
    ///     <paramref name="assumedWhenMissing" /> when the value has no version field.
    /// </summary>
    public static int SchemaVersion(ThreadMetadata? metadata, string key, int assumedWhenMissing)
    {
        var json = RawJson(metadata, key);
        if (json is null)
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema_version", out var value)
                && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : assumedWhenMissing;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>Deserializes <paramref name="json" />, reading a corrupt value as absent.</summary>
    public static T? Deserialize<T>(string? json)
        where T : class
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     <paramref name="existing" /> with the given properties set, or a fresh record for the thread when
    ///     the store had none: a projection may be the first thing ever written about a thread.
    /// </summary>
    public static ThreadMetadata WithProperties(
        ThreadMetadata? existing,
        string threadId,
        params (string Key, string Json)[] properties
    )
    {
        var bag = existing?.Properties ?? ImmutableDictionary<string, object>.Empty;
        foreach (var (key, json) in properties)
        {
            bag = bag.SetItem(key, json);
        }

        return existing is not null
            ? existing with
            {
                Properties = bag,
            }
            : new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = bag,
            };
    }
}
