using System.Text.Json;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Utility methods for handling JSON strings, especially partial or streaming JSON data
/// </summary>
public static class JsonStringUtils
{
    /// <summary>
    /// Unescapes a JSON string, converting escape sequences like \n to their actual characters
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
    /// Attempts to extract a property from potentially incomplete JSON
    /// Uses JsonDocument.Parse first, falls back to regex for incomplete JSON
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
    /// Attempts to determine if a JSON fragment is likely complete
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
