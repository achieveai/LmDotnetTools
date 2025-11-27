using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Core;

/// <summary>
///     Represents a contract for a function parameter, defining its name, type, and other metadata.
/// </summary>
public record FunctionParameterContract
{
    /// <summary>
    ///     The name of the parameter. This is required and cannot be null.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    ///     The description of the parameter.
    ///     This will be extracted from the param section of the structured comment if available.
    ///     Otherwise, the description will be null.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    ///     The type of the parameter. This is required and cannot be null.
    /// </summary>
    [JsonPropertyName("schema")]
    public required JsonSchemaObject ParameterType { get; init; }

    /// <summary>
    ///     If the parameter is a required parameter.
    /// </summary>
    [JsonPropertyName("required")]
    public bool IsRequired { get; init; }

    /// <summary>
    ///     The default value of the parameter.
    /// </summary>
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? DefaultValue { get; init; }
}
