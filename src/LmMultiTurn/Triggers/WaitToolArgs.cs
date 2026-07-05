using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Parsed shape of a <c>Wait</c> tool call's arguments. Shared by the tool handler (fresh calls)
/// and the runtime's restart reconciliation (re-parsing persisted args), so both interpret the
/// wire format identically.
/// </summary>
internal sealed record WaitToolArgs(string Kind, string ArgsJson, string Timeout, string? Label)
{
    /// <summary>
    /// Parses <c>{ kind, args?, timeout, label? }</c>. Returns false when <c>kind</c> or
    /// <c>timeout</c> is missing/blank; <c>args</c> defaults to an empty object.
    /// </summary>
    public static bool TryParse(string? json, out WaitToolArgs result)
    {
        result = new WaitToolArgs(string.Empty, "{}", string.Empty, null);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var kind = GetString(root, "kind");
            var timeout = GetString(root, "timeout");
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(timeout))
            {
                return false;
            }

            var argsJson = root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object
                ? argsEl.GetRawText()
                : "{}";
            var label = GetString(root, "label");

            result = new WaitToolArgs(kind, argsJson, timeout, string.IsNullOrWhiteSpace(label) ? null : label);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
