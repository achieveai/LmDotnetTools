using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Core;

[JsonConverter(typeof(UsageShadowPropertiesJsonConverter))]
public record Usage
{
    [JsonPropertyName("prompt_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TotalTokens { get; init; }

    [JsonPropertyName("total_cost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double? TotalCost { get; init; }

    // OpenAI-style nested token details - generic approach
    [JsonPropertyName("input_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InputTokenDetails? InputTokenDetails { get; init; }

    [JsonPropertyName("output_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputTokenDetails? OutputTokenDetails { get; init; }

    // Unified access properties for convenience
    [JsonIgnore]
    public int TotalReasoningTokens => OutputTokenDetails?.ReasoningTokens ?? 0;

    [JsonIgnore]
    public int TotalCachedTokens => InputTokenDetails?.CachedTokens ?? 0;

    [JsonIgnore]
    public ImmutableDictionary<string, object?> ExtraProperties { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    public Usage SetExtraProperty<T>(string key, T value)
    {
        var rv = this;
        if (ExtraProperties == null)
        {
            rv = this with { ExtraProperties = ImmutableDictionary<string, object?>.Empty };
        }

        rv = rv with { ExtraProperties = rv.ExtraProperties!.Add(key, value) };
        return rv;
    }

    public T? GetExtraProperty<T>(string key)
    {
        if (ExtraProperties == null)
        {
            return default;
        }

        if (ExtraProperties.TryGetValue(key, out var value) && value is not null)
        {
            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Null)
                {
                    return default;
                }

                if (typeof(T) == typeof(string))
                {
                    return (T)(object)jsonElement.GetString()!;
                }

                if (typeof(T) == typeof(int))
                {
                    return (T)(object)jsonElement.GetInt32();
                }

                if (typeof(T) == typeof(double))
                {
                    return (T)(object)jsonElement.GetDouble();
                }

                if (typeof(T) == typeof(bool))
                {
                    return (T)(object)jsonElement.GetBoolean();
                }
            }

            return (T)value!;
        }

        return default;
    }
}

/// <summary>
/// OpenAI-style input token details structure
/// </summary>
public record InputTokenDetails
{
    [JsonPropertyName("cached_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CachedTokens { get; init; }
}

/// <summary>
/// OpenAI-style output token details structure
/// </summary>
public record OutputTokenDetails
{
    [JsonPropertyName("reasoning_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReasoningTokens { get; init; }
}
