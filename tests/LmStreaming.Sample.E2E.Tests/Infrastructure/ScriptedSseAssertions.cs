using System.Text.Json;

namespace LmStreaming.Sample.E2E.Tests.Infrastructure;

/// <summary>
/// Helper operators for asserting on a stream of WebSocket frames emitted by
/// <c>ChatWebSocketManager</c>. Each frame is a <see cref="JsonDocument"/> containing a
/// lowercase snake_case <c>$type</c> discriminator (e.g. <c>text</c>, <c>tool_call</c>,
/// <c>tools_call</c>, <c>tool_call_result</c>) as emitted by <c>IMessageJsonConverter</c>.
/// </summary>
public static class ScriptedSseAssertions
{
    /// <summary>Filter frames by their <c>$type</c> discriminator (case-sensitive).</summary>
    public static IEnumerable<JsonElement> OfMessageType(
        this IEnumerable<JsonDocument> frames,
        string typeName)
    {
        foreach (var frame in frames)
        {
            if (frame.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (frame.RootElement.TryGetProperty("$type", out var typeProp)
                && typeProp.ValueKind == JsonValueKind.String
                && string.Equals(typeProp.GetString(), typeName, StringComparison.Ordinal))
            {
                yield return frame.RootElement;
            }
        }
    }

    /// <summary>
    /// Concatenate all <c>text</c> fields from frames of type <c>text</c> or streaming
    /// <c>text_update</c>.
    /// </summary>
    public static string ConcatText(this IEnumerable<JsonDocument> frames)
    {
        var assistantTexts = frames
            .OfMessageType("text")
            .Concat(frames.OfMessageType("text_update"))
            .Where(IsAssistant)
            .Select(f => f.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty);
        return string.Concat(assistantTexts);
    }

    /// <summary>
    /// Returns every tool-call function name observed across the frames. Includes both streaming
    /// update (<c>tool_call_update</c>, <c>tools_call_update</c>) and final (<c>tool_call</c>,
    /// <c>tools_call</c>) shapes. Excludes <c>tool_call_result</c> / <c>tools_call_result</c> frames.
    /// </summary>
    public static IReadOnlyList<string> ToolCallNames(this IEnumerable<JsonDocument> frames)
    {
        var names = new List<string>();
        foreach (var frame in frames)
        {
            if (frame.RootElement.ValueKind != JsonValueKind.Object
                || !frame.RootElement.TryGetProperty("$type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = typeProp.GetString();
            if (!IsToolCallFrame(type!))
            {
                continue;
            }

            // Container: { $type: "tools_call", tool_calls: [ { function_name: "..." } ] }
            if (frame.RootElement.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                {
                    var name = TryReadFunctionName(call);
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name!);
                    }
                }
                continue;
            }

            var directName = TryReadFunctionName(frame.RootElement);
            if (!string.IsNullOrEmpty(directName))
            {
                names.Add(directName!);
            }
        }

        return names;
    }

    /// <summary>
    /// Returns every tool-call result string found across <c>tool_call_result</c> /
    /// <c>tools_call_result</c> / <c>tools_call_aggregate</c> frames.
    /// </summary>
    public static IReadOnlyList<string> ToolCallResults(this IEnumerable<JsonDocument> frames)
    {
        var results = new List<string>();
        foreach (var frame in frames)
        {
            if (frame.RootElement.ValueKind != JsonValueKind.Object
                || !frame.RootElement.TryGetProperty("$type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = typeProp.GetString();
            if (!IsToolCallResultFrame(type!))
            {
                continue;
            }

            if (frame.RootElement.TryGetProperty("tool_call_results", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("result", out var r)
                        && r.ValueKind == JsonValueKind.String)
                    {
                        results.Add(r.GetString()!);
                    }
                }
                continue;
            }

            if (frame.RootElement.TryGetProperty("result", out var direct)
                && direct.ValueKind == JsonValueKind.String)
            {
                results.Add(direct.GetString()!);
            }
        }

        return results;
    }

    private static bool IsAssistant(JsonElement frame)
    {
        if (!frame.TryGetProperty("role", out var role))
        {
            return true; // no role = assume assistant stream
        }

        return role.ValueKind == JsonValueKind.String
            && (role.GetString()?.Equals("assistant", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool IsToolCallFrame(string type) =>
        type is "tool_call"
            or "tools_call"
            or "tool_call_update"
            or "tools_call_update"
            or "tools_call_aggregate";

    private static bool IsToolCallResultFrame(string type) =>
        type is "tool_call_result"
            or "tools_call_result"
            or "tools_call_aggregate";

    private static string? TryReadFunctionName(JsonElement element)
    {
        // Prefer "function_name" (LmCore wire shape); fall back to "name".
        if (element.TryGetProperty("function_name", out var fn) && fn.ValueKind == JsonValueKind.String)
        {
            return fn.GetString();
        }

        if (element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            return n.GetString();
        }

        return null;
    }
}
