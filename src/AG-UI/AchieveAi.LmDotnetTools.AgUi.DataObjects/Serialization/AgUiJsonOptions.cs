using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;

/// <summary>
/// JSON serialization options configured for AG-UI protocol compliance
/// </summary>
public static class AgUiJsonOptions
{
    /// <summary>
    /// Default JSON serializer options for AG-UI events and DTOs
    /// </summary>
    public static JsonSerializerOptions Default { get; } = new()
    {
        // Use camelCase for property names (AG-UI convention)
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Omit null values when writing
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Convert enums to strings
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        },

        // Don't indent for compactness
        WriteIndented = false,

        // Case-insensitive property matching when reading
        PropertyNameCaseInsensitive = true,

        // Allow trailing commas
        AllowTrailingCommas = true,

        // Allow comments in JSON
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// JSON options with indentation for human-readable output
    /// </summary>
    public static JsonSerializerOptions Pretty { get; } = new(Default)
    {
        WriteIndented = true
    };
}
