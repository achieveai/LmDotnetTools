using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(JsonPropertyNameEnumConverter<Role>))]
public enum Role
{
    [JsonPropertyName("none")]
    None,

    [JsonPropertyName("user")]
    User,

    [JsonPropertyName("assistant")]
    Assistant,

    [JsonPropertyName("system")]
    System,

    [JsonPropertyName("tool")]
    Tool,
}
