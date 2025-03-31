
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

public class ResponseFormat
{
    public static readonly ResponseFormat JSON = new ResponseFormat();

    [JsonPropertyName("type")]
    public string ResponseFormatType { get; set; } = "json_object";
}
