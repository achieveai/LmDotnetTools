using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Core;

/// <summary>
///     Represents a function contract for Tool calls.
/// </summary>
public class FunctionContract
{
    /// <summary>
    ///     The namespace of the function.
    /// </summary>
    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    /// <summary>
    ///     The class name of the function.
    /// </summary>
    [JsonPropertyName("class_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassName { get; set; }

    /// <summary>
    ///     The name of the function.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    /// <summary>
    ///     The description of the function.
    ///     If a structured comment is available, the description will be extracted from the summary section.
    ///     Otherwise, the description will be null.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    ///     The parameters of the function.
    /// </summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<FunctionParameterContract>? Parameters { get; set; }

    /// <summary>
    ///     The return type of the function.
    /// </summary>
    [JsonPropertyName("return_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Type? ReturnType { get; set; }

    /// <summary>
    ///     The description of the return section.
    ///     If a structured comment is available, the description will be extracted from the return section.
    ///     Otherwise, the description will be null.
    /// </summary>
    [JsonPropertyName("return_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnDescription { get; set; }

    /// <summary>
    ///     Gets the JSON schema for the function contract.
    /// </summary>
    /// <returns>The JSON schema for the function contract, or null if no parameters are defined.</returns>
    public JsonSchemaObject? GetJsonSchema()
    {
        if (Parameters == null)
        {
            return null;
        }

        var builder = new JsonSchemaObjectBuilder("object");
        foreach (var parameter in Parameters)
        {
            _ = builder.WithProperty(parameter.Name, parameter.ParameterType, parameter.IsRequired);
        }

        return builder.Build();
    }

    /// <summary>
    ///     The parameters as the one JSON Schema object a provider sends for a tool:
    ///     <c>{ "type": "object", "properties": { … }, "required": [ … ], "additionalProperties": false }</c>.
    ///     Each property is the parameter's own schema, with the parameter's description on it when the schema has
    ///     none. A parameter with no name or no schema is left out; <c>required</c> is absent when nothing is.
    /// </summary>
    public JsonSchemaObject GetParametersSchema()
    {
        var properties = new Dictionary<string, JsonSchemaObject>(StringComparer.Ordinal);
        var required = new List<string>();

        if (Parameters is not null)
        {
            foreach (var parameter in Parameters)
            {
                if (string.IsNullOrEmpty(parameter.Name) || parameter.ParameterType is null)
                {
                    continue;
                }

                // Carry the parameter description onto the property schema when it has none of its own.
                var propertySchema =
                    parameter.ParameterType.Description is null && parameter.Description is not null
                        ? parameter.ParameterType with
                        {
                            Description = parameter.Description,
                        }
                        : parameter.ParameterType;

                properties[parameter.Name] = propertySchema;
                if (parameter.IsRequired)
                {
                    required.Add(parameter.Name);
                }
            }
        }

        return new JsonSchemaObject
        {
            Type = JsonSchemaTypeHelper.ToType("object"),
            Properties = properties,
            Required = required.Count > 0 ? required : null,
            AdditionalProperties = false,
        };
    }
}
