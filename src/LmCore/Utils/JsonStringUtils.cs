using System.Text.Json;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     Utility methods for handling JSON strings, especially partial or streaming JSON data
/// </summary>
public static class JsonStringUtils
{
    // A Markdown fenced code block (```json … ``` or a bare ``` … ```). Non-greedy body, singleline so the
    // body can span newlines; an optional language tag (json/jsonc/…) is skipped.
    private static readonly Regex FencedBlockPattern = new(
        @"```[A-Za-z0-9]*[ \t]*\r?\n?(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled
    );

    /// <summary>
    ///     Attempts to extract a self-contained JSON value from free-form model output that may wrap the JSON in
    ///     prose or a Markdown code fence. Tries, in order: (1) the whole trimmed text, (2) the body of a
    ///     <c>```json</c> / bare <c>```</c> fenced block, (3) the first balanced <c>{ … }</c> or <c>[ … ]</c>
    ///     span embedded in the text. Each candidate must itself parse as JSON to be accepted, so surrounding
    ///     prose or a mis-detected span can never be returned as a false positive.
    /// </summary>
    /// <param name="text">The raw model output (may be null, prose, fenced, or already-clean JSON).</param>
    /// <param name="json">The extracted JSON text when the method returns <c>true</c>; empty otherwise.</param>
    /// <returns><c>true</c> if a parseable JSON value was found; otherwise <c>false</c>.</returns>
    public static bool TryExtractJsonPayload(string? text, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // (1) The common case: the whole payload already is JSON.
        var trimmed = text.Trim();
        if (IsParseableJson(trimmed))
        {
            json = trimmed;
            return true;
        }

        // (2) A Markdown fenced block — take the first fence whose body parses as JSON.
        foreach (Match fence in FencedBlockPattern.Matches(text))
        {
            var candidate = fence.Groups["body"].Value.Trim();
            if (IsParseableJson(candidate))
            {
                json = candidate;
                return true;
            }
        }

        // (3) The first balanced object/array span embedded in surrounding prose.
        if (TryExtractBalancedSpan(text, out var span) && IsParseableJson(span))
        {
            json = span;
            return true;
        }

        return false;
    }

    private static bool IsParseableJson(string candidate)
    {
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Scans <paramref name="text"/> for the first <c>{</c> or <c>[</c> and returns the substring through its
    ///     matching close brace/bracket, tracking nesting depth while skipping over string literals (so a brace
    ///     inside a JSON string does not throw off the balance). Returns <c>false</c> if there is no opener or the
    ///     span never closes.
    /// </summary>
    private static bool TryExtractBalancedSpan(string text, out string span)
    {
        span = string.Empty;

        var start = text.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return false;
        }

        var opener = text[start];
        var closer = opener == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == opener)
            {
                depth++;
            }
            else if (c == closer && --depth == 0)
            {
                span = text[start..(i + 1)];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Unescapes a JSON string, converting escape sequences like \n to their actual characters
    /// </summary>
    /// <param name="jsonString">The JSON string to unescape</param>
    /// <returns>The unescaped string</returns>
    public static string UnescapeJsonString(string jsonString)
    {
        return string.IsNullOrEmpty(jsonString)
            ? jsonString
            : jsonString.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\t", "\t");
    }

    /// <summary>
    ///     Attempts to extract a property from potentially incomplete JSON
    ///     Uses JsonDocument.Parse first, falls back to regex for incomplete JSON
    /// </summary>
    /// <typeparam name="T">The expected type of the property value</typeparam>
    /// <param name="partialJson">The potentially incomplete JSON string</param>
    /// <param name="propertyName">The name of the property to extract</param>
    /// <param name="value">The extracted value if successful, default otherwise</param>
    /// <returns>True if extraction was successful, false otherwise</returns>
    public static bool TryExtractPropertyFromPartialJson<T>(string partialJson, string propertyName, out T? value)
    {
        value = default;
        if (string.IsNullOrEmpty(partialJson))
        {
            return false;
        }

        // First attempt: Try to parse as valid JSON
        try
        {
            using var doc = JsonDocument.Parse(partialJson, new JsonDocumentOptions { AllowTrailingCommas = true });

            if (doc.RootElement.TryGetProperty(propertyName, out var propElement))
            {
                value = propElement.Deserialize<T>();
                return value != null;
            }
        }
        catch
        {
            // If parsing fails, try with regex for incomplete JSON as a fallback
            try
            {
                // Only attempt regex for string types
                if (typeof(T) == typeof(string))
                {
                    var regex = new Regex($"\"{propertyName}\"\\s*:\\s*\"([^\"]*)\"");
                    var match = regex.Match(partialJson);
                    if (match.Success && match.Groups.Count > 1)
                    {
                        value = (T)(object)match.Groups[1].Value;
                        return true;
                    }
                }
            }
            catch
            {
                // Both approaches failed, return false
            }
        }

        return false;
    }

    /// <summary>
    ///     Attempts to determine if a JSON fragment is likely complete
    /// </summary>
    /// <param name="jsonFragment">The JSON fragment to check</param>
    /// <returns>True if the JSON appears to be complete, false otherwise</returns>
    public static bool IsLikelyCompleteJson(string jsonFragment)
    {
        if (string.IsNullOrEmpty(jsonFragment))
        {
            return false;
        }

        try
        {
            // Try to parse the JSON - if it succeeds, it's likely complete
            _ = JsonDocument.Parse(jsonFragment);
            return true;
        }
        catch
        {
            // Simple heuristic: Check for balanced braces
            var openBraces = jsonFragment.Count(c => c == '{');
            var closeBraces = jsonFragment.Count(c => c == '}');

            // Check for object completeness (balanced braces and ends with closing brace)
            if (openBraces > 0 && openBraces == closeBraces && jsonFragment.TrimEnd().EndsWith("}"))
            {
                return true;
            }

            // Check for array completeness
            var openBrackets = jsonFragment.Count(c => c == '[');
            var closeBrackets = jsonFragment.Count(c => c == ']');

            if (openBrackets > 0 && openBrackets == closeBrackets && jsonFragment.TrimEnd().EndsWith("]"))
            {
                return true;
            }
        }

        return false;
    }
}
