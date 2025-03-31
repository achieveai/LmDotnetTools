using System.Text.Json.Serialization;
namespace AchieveAi.LmDotnetTools.LmCore.Core;

public class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    //
    // Summary:
    //     Gets or Sets CompletionTokens
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    //
    // Summary:
    //     Gets or Sets TotalTokens
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("completion_tokens_details")]
    public CompletionTokenDetails? CompletionTokenDetails { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtraProperties { get; set; }

    public void SetExtraProperty<T>(string key, T value)
    {
        if (ExtraProperties == null)
        {
            ExtraProperties = new Dictionary<string, object?>();
        }

        ExtraProperties[key] = value;
    }

    public T? GetExtraProperty<T>(string key)
    {
        if (ExtraProperties == null)
        {
            return default;
        }

        if (ExtraProperties.TryGetValue(key, out var value)
            && value is T)
        {
            return (T)value;
        }

        return default;
    }
}

public class CompletionTokenDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }
}