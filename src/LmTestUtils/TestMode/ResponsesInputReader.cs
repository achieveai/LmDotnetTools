using System.Text;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     Shared reader for OpenAI Responses API request bodies. Centralizes the
///     "<c>input</c> array → user-text" extraction so the mock SSE handler, scripted SSE responder,
///     and the in-process WebSocket emitter can't drift as the wire schema evolves.
/// </summary>
public static class ResponsesInputReader
{
    /// <summary>
    ///     Pulls the latest <c>role=user</c> entry's text from the <c>input</c> array, returning
    ///     <c>null</c> if no user item exists.
    /// </summary>
    /// <param name="root">JSON root of a <c>response.create</c> request body.</param>
    /// <param name="concatenateAll">
    ///     When the latest user item carries multiple <c>input_text</c>/<c>output_text</c>/<c>text</c>
    ///     parts, <c>true</c> concatenates them with <c>\n</c> separators (matches the responder
    ///     pattern); <c>false</c> returns the first match (matches the SSE-handler pattern).
    /// </param>
    public static string? ExtractLatestUserText(JsonElement root, bool concatenateAll = false)
    {
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? latest = null;
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !IsRole(item, "user"))
            {
                continue;
            }

            var text = ReadContentText(item, concatenateAll);
            if (!string.IsNullOrEmpty(text))
            {
                latest = text;
            }
        }

        return latest;
    }

    /// <summary>
    ///     Reads the <c>content</c> field of an <c>input</c>-array item, accepting either a bare
    ///     string or an array of <c>{type,text}</c> parts where <c>type</c> ∈
    ///     {<c>input_text</c>, <c>output_text</c>, <c>text</c>}.
    /// </summary>
    /// <param name="item">A single object from the <c>input</c> array.</param>
    /// <param name="concatenateAll">
    ///     <c>true</c> joins all matching parts with <c>\n</c>; <c>false</c> returns the first
    ///     matching part.
    /// </param>
    public static string ReadContentText(JsonElement item, bool concatenateAll)
    {
        if (!item.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        if (concatenateAll)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (TryGetPartText(part, out var t))
                {
                    if (sb.Length > 0)
                    {
                        _ = sb.Append('\n');
                    }

                    _ = sb.Append(t);
                }
            }

            return sb.ToString();
        }

        foreach (var part in content.EnumerateArray())
        {
            if (TryGetPartText(part, out var t))
            {
                return t;
            }
        }

        return string.Empty;
    }

    private static bool TryGetPartText(JsonElement part, out string text)
    {
        text = string.Empty;
        if (part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var t = typeEl.GetString();
        if (t is not ("input_text" or "output_text" or "text"))
        {
            return false;
        }

        if (!part.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = textProp.GetString() ?? string.Empty;
        return true;
    }

    private static bool IsRole(JsonElement item, string role)
    {
        return item.TryGetProperty("role", out var r)
            && r.ValueKind == JsonValueKind.String
            && string.Equals(r.GetString(), role, StringComparison.OrdinalIgnoreCase);
    }
}
