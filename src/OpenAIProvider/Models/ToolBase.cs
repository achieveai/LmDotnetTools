using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public abstract class ToolBase
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    public ToolBase(string type)
    {
        Type = type;
    }
}