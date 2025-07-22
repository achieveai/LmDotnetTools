using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Agents;

public class FunctionContract
{
    /// <summary>
    /// The namespace of the function.
    /// </summary>
    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    /// <summary>
    /// The class name of the function.
    /// </summary>
    [JsonPropertyName("class_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClassName { get; set; }

    /// <summary>
    /// The name of the function.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The description of the function.
    /// If a structured comment is available, the description will be extracted from the summary section.
    /// Otherwise, the description will be null.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// The parameters of the function.
    /// </summary>
    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<FunctionParameterContract>? Parameters { get; set; }

    /// <summary>
    /// The return type of the function.
    /// </summary>
    [JsonPropertyName("return_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Type? ReturnType { get; set; }

    /// <summary>
    /// The description of the return section.
    /// If a structured comment is available, the description will be extracted from the return section.
    /// Otherwise, the description will be null.
    /// </summary>
    [JsonPropertyName("return_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnDescription { get; set; }

    public JsonSchemaObject? GetJsonSchema()
    {
        if (Parameters == null)
        {
            return null;
        }

        var builder = new JsonSchemaObjectBuilder("object");
        foreach (var parameter in Parameters)
        {
            var property = new JsonSchemaProperty
            {
                Type = parameter.ParameterType.Type,
                Description = parameter.Description,
                Items = parameter.ParameterType.Items,
                Properties = parameter.ParameterType.Properties?
                    .ToDictionary(
                        p => p.Key,
                        p => new JsonSchemaProperty
                        {
                            Type = p.Value.Type,
                            Description = p.Value.Description,
                            Items = p.Value.Items,
                        })
            };

            builder.WithProperty(parameter.Name, property, parameter.IsRequired);
        }

        return builder.Build();

    }
}
