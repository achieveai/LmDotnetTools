using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public class ErrorResponse
{
    //
    // Summary:
    //     Gets or Sets Error
    [JsonPropertyName("error")]
    public Error Error { get; set; }

    public ErrorResponse(Error error)
    {
        Error = error;
    }
}