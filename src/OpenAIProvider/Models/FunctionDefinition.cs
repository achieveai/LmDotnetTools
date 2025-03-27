using System.Text.Json.Serialization;
using Json.Schema;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public class FunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonSchema? Parameters { get; set; }

    public FunctionDefinition(string name, string description, JsonSchema? parameters = null)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
    }
}
