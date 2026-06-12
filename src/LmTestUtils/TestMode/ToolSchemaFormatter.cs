using System.Text.Encodings.Web;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     Formats a single tool's description and parameter schema as Markdown,
///     with the schema rendered as an indented JSON code block for easy reading.
/// </summary>
internal static class ToolSchemaFormatter
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    ///     Builds a Markdown document describing a single tool.
    /// </summary>
    /// <param name="name">The tool name.</param>
    /// <param name="description">The tool description (may be null/empty).</param>
    /// <param name="schema">The parameter/input schema element (may be null).</param>
    public static string ToMarkdown(string name, string? description, JsonElement? schema)
    {
        var desc = string.IsNullOrWhiteSpace(description) ? "_No description provided._" : description;
        var schemaJson = schema.HasValue ? JsonSerializer.Serialize(schema.Value, PrettyOptions) : "{}";

        return $"# {name}\n\n## Description\n\n{desc}\n\n## Schema\n\n```json\n{schemaJson}\n```";
    }
}
