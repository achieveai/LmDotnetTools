using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models.OpenRouter;

public class OpenRouterStatsData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("streamed")]
    public bool Streamed { get; set; }

    [JsonPropertyName("generation_time")]
    public int GenerationTime { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("tokens_prompt")]
    public int TokensPrompt { get; set; }

    [JsonPropertyName("tokens_completion")]
    public int TokensCompletion { get; set; }

    [JsonPropertyName("native_tokens_prompt")]
    public int NativeTokensPrompt { get; set; }

    [JsonPropertyName("native_tokens_completion")]
    public int NativeTokensCompletion { get; set; }

    [JsonPropertyName("num_media_prompt")]
    public int? NumMediaPrompt { get; set; }

    [JsonPropertyName("num_media_completion")]
    public int? NumMediaCompletion { get; set; }

    [JsonPropertyName("origin")]
    public string? Origin { get; set; }

    [JsonPropertyName("total_cost")]
    public double TotalCost { get; set; }
}

public record OpenRouterStatsResponse
{
    [JsonPropertyName("data")]
    public required OpenRouterStatsData Data { get; set; }
}
