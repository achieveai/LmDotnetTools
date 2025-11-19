using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs;

/// <summary>
/// Represents a tool/function call in the AG-UI protocol
/// </summary>
public sealed record ToolCall
{
    /// <summary>
    /// Unique identifier for this tool call
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Type of the tool call (always "function")
    /// </summary>
    [JsonPropertyName("type")]
    public string Type => "function";

    /// <summary>
    /// Function call details
    /// </summary>
    [JsonPropertyName("function")]
    public required FunctionCall Function { get; init; }
}

/// <summary>
/// Represents a function call within a tool call
/// </summary>
public sealed record FunctionCall
{
    /// <summary>
    /// Name of the function to call
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// JSON-encoded arguments for the function
    /// </summary>
    [JsonPropertyName("arguments")]
    public required JsonElement Arguments { get; init; }
}
