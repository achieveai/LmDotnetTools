using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public class FunctionTool
{
    public FunctionTool(FunctionDefinition definition)
    {
        Type = "function";
        Function = definition;
    }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("function")]
    public FunctionDefinition Function { get; }
}