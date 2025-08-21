using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
/// Represents the response format capabilities of a model.
/// </summary>
public record ResponseFormatCapability
{
    /// <summary>
    /// Whether the model supports JSON mode for structured responses.
    /// </summary>
    [JsonPropertyName("supports_json_mode")]
    public bool SupportsJsonMode { get; init; } = false;

    /// <summary>
    /// Whether the model supports structured output with schema validation.
    /// </summary>
    [JsonPropertyName("supports_structured_output")]
    public bool SupportsStructuredOutput { get; init; } = false;

    /// <summary>
    /// Whether the model supports JSON schema for response validation.
    /// </summary>
    [JsonPropertyName("supports_json_schema")]
    public bool SupportsJsonSchema { get; init; } = false;

    /// <summary>
    /// Whether the model supports XML format responses.
    /// </summary>
    [JsonPropertyName("supports_xml")]
    public bool SupportsXml { get; init; } = false;

    /// <summary>
    /// Whether the model supports YAML format responses.
    /// </summary>
    [JsonPropertyName("supports_yaml")]
    public bool SupportsYaml { get; init; } = false;

    /// <summary>
    /// Whether the model supports CSV format responses.
    /// </summary>
    [JsonPropertyName("supports_csv")]
    public bool SupportsCsv { get; init; } = false;

    /// <summary>
    /// Whether the model supports markdown format responses.
    /// </summary>
    [JsonPropertyName("supports_markdown")]
    public bool SupportsMarkdown { get; init; } = false;

    /// <summary>
    /// Whether the model supports HTML format responses.
    /// </summary>
    [JsonPropertyName("supports_html")]
    public bool SupportsHtml { get; init; } = false;

    /// <summary>
    /// Whether the model supports custom format specifications.
    /// </summary>
    [JsonPropertyName("supports_custom_formats")]
    public bool SupportsCustomFormats { get; init; } = false;

    /// <summary>
    /// Supported JSON schema versions (e.g., "draft-07", "draft-2020-12").
    /// </summary>
    [JsonPropertyName("supported_schema_versions")]
    public IReadOnlyList<string> SupportedSchemaVersions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported custom format types for responses.
    /// </summary>
    [JsonPropertyName("supported_custom_formats")]
    public IReadOnlyList<string> SupportedCustomFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Maximum depth allowed for nested objects in structured responses.
    /// </summary>
    [JsonPropertyName("max_nesting_depth")]
    public int? MaxNestingDepth { get; init; }

    /// <summary>
    /// Maximum number of properties allowed in structured responses.
    /// </summary>
    [JsonPropertyName("max_properties")]
    public int? MaxProperties { get; init; }

    /// <summary>
    /// Whether the model supports strict schema adherence (no additional properties).
    /// </summary>
    [JsonPropertyName("supports_strict_schema")]
    public bool SupportsStrictSchema { get; init; } = false;

    /// <summary>
    /// Whether the model supports partial schema matching for flexible responses.
    /// </summary>
    [JsonPropertyName("supports_partial_schema")]
    public bool SupportsPartialSchema { get; init; } = false;
}