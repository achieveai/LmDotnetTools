using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

/// <summary>
/// Represents a function definition for tool calling
/// </summary>
public sealed record FunctionDefinition
{
    /// <summary>
    /// The name of the function
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; }

    /// <summary>
    /// A description of what the function does
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; init; }

    /// <summary>
    /// The parameters the function accepts, defined as a JSON Schema object
    /// </summary>
    [JsonPropertyName("parameters")]
    public JsonSchemaObject Parameters { get; init; }

    /// <summary>
    /// Creates a new function definition
    /// </summary>
    /// <param name="name">The name of the function</param>
    /// <param name="description">A description of what the function does</param>
    /// <param name="parameters">The parameters the function accepts</param>
    public FunctionDefinition(string name, string description, JsonSchemaObject parameters)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
    }

    /// <summary>
    /// Creates a new function definition with parameters defined by a schema builder
    /// </summary>
    /// <param name="name">The name of the function</param>
    /// <param name="description">A description of what the function does</param>
    /// <param name="parametersBuilder">A builder for constructing the parameters schema</param>
    /// <returns>A new function definition</returns>
    public static FunctionDefinition Create(
        string name, 
        string description, 
        Func<JsonSchemaObjectBuilder, JsonSchemaObjectBuilder> parametersBuilder)
    {
        var builder = JsonSchemaObject.Create();
        var schema = parametersBuilder(builder).Build();
        
        return new FunctionDefinition(name, description, schema);
    }
}
