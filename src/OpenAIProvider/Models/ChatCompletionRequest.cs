using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public class ChatCompletionRequest
{
    public ChatCompletionRequest(
        string model,
        IEnumerable<ChatMessage> messages,
        double temperature = 0.7,
        int maxTokens = 4096,
        JsonObject? additionalParameters = null)
    {
        Model = model;
        Messages = messages.ToList();
        Temperature = temperature;
        MaxTokens = maxTokens;
        AdditionalParameters = additionalParameters;
    }

    [JsonPropertyName("model")]
    public string Model { get; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("n")]
    public int N { get; set; } = 1;

    [JsonPropertyName("stop")]
    public string[]? Stop { get; set; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("logit_bias")]
    public Dictionary<string, int>? LogitBias { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("safe_prompt")]
    public bool? SafePrompt { get; set; }

    [JsonPropertyName("random_seed")]
    public int? RandomSeed { get; set; }

    [JsonPropertyName("tools")]
    public List<FunctionTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public ToolChoiceEnum? ToolChoice { get; set; }

    [JsonPropertyName("response_format")]
    public ResponseFormat? ResponseFormat { get; set; }

    [JsonExtensionData]
    public JsonObject? AdditionalParameters { get; }
} 