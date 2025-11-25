using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public abstract class ToolBase
{
    public ToolBase(string type)
    {
        Type = type;
    }

    [JsonPropertyName("type")]
    public string Type { get; set; }
}
