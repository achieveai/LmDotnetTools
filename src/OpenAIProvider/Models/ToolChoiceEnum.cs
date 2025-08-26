using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

[JsonConverter(typeof(JsonPropertyNameEnumConverter<ToolChoiceEnum>))]
public enum ToolChoiceEnum
{
    [JsonPropertyName("auto")]
    Auto,

    [JsonPropertyName("none")]
    None,

    [JsonPropertyName("any")]
    Any,
}
