using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public class Error
{
    public Error(string type, string message, string? param = null, string? code = null)
    {
        Type = type;
        Message = message;
        Param = param;
        Code = code;
    }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    //
    // Summary:
    //     Gets or Sets Message
    [JsonPropertyName("message")]
    public string Message { get; set; }

    //
    // Summary:
    //     Gets or Sets Param
    [JsonPropertyName("param")]
    public string? Param { get; set; }

    //
    // Summary:
    //     Gets or Sets Code
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
