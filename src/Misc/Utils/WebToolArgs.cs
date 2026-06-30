using System.Text.Json;

namespace AchieveAi.LmDotnetTools.Misc.Utils;

/// <summary>
///     Tolerant readers for the JSON argument payloads passed to the web tools. Each reader returns
///     <c>null</c> when the property is absent or of an unexpected kind, so a malformed or partial
///     payload degrades gracefully into a validation error rather than throwing.
/// </summary>
internal static class WebToolArgs
{
    /// <summary>
    ///     Returns the string value of <paramref name="name" /> when present as a JSON string;
    ///     otherwise <c>null</c>.
    /// </summary>
    public static string? ReadString(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    /// <summary>
    ///     Returns the boolean value of <paramref name="name" /> when present as a JSON boolean;
    ///     otherwise <c>null</c>.
    /// </summary>
    public static bool? ReadBool(JsonElement root, string name)
    {
        if (
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        )
        {
            return property.GetBoolean();
        }

        return null;
    }

    /// <summary>
    ///     Returns the integer value of <paramref name="name" /> when present as a JSON number that
    ///     fits in an <see cref="int" />; otherwise <c>null</c>.
    /// </summary>
    public static int? ReadInt(JsonElement root, string name)
    {
        if (
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value)
        )
        {
            return value;
        }

        return null;
    }
}
