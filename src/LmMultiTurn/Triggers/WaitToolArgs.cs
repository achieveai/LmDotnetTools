using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Parsed shape of a <c>Wait</c> tool call's arguments. Shared by the tool handler (fresh calls)
/// and the runtime's restart reconciliation (re-parsing persisted args), so both interpret the
/// wire format identically.
/// </summary>
internal sealed record WaitToolArgs(
    string Kind,
    string ArgsJson,
    string Timeout,
    string? Label,
    WaitMode Mode = WaitMode.Block,
    int? MaxFires = null)
{
    /// <summary>
    /// Parses <c>{ kind, args?, timeout, label?, mode?, maxFires? }</c>. Returns false when
    /// <c>kind</c> or <c>timeout</c> is missing/blank, <c>mode</c> is not <c>block</c>/<c>notify</c>,
    /// or <c>maxFires</c> is present but not a positive integer. <c>args</c> defaults to an empty
    /// object; <c>mode</c> defaults to <see cref="WaitMode.Block"/>.
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

            var mode = WaitMode.Block;
            if (root.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind != JsonValueKind.Null)
            {
                if (modeEl.ValueKind != JsonValueKind.String)
                {
                    return false; // mode must be a string when present
                }

                var modeText = modeEl.GetString();
                if (string.Equals(modeText, "notify", StringComparison.OrdinalIgnoreCase))
                {
                    mode = WaitMode.Notify;
                }
                else if (!string.Equals(modeText, "block", StringComparison.OrdinalIgnoreCase))
                {
                    return false; // unknown mode
                }
            }

            int? maxFires = null;
            if (root.TryGetProperty("maxFires", out var maxEl) && maxEl.ValueKind != JsonValueKind.Null)
            {
                if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out var mf) || mf < 1)
                {
                    return false; // maxFires must be a positive integer when present
                }
                maxFires = mf;
            }

            result = new WaitToolArgs(
                kind,
                argsJson,
                timeout,
                string.IsNullOrWhiteSpace(label) ? null : label,
                mode,
                maxFires);
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
