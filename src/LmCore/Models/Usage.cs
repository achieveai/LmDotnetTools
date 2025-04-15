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

    [JsonPropertyName("completion_token_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionTokenDetails? CompletionTokenDetails { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object?> ExtraProperties { get; init; } = ImmutableDictionary<string, object?>.Empty;

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

public record CompletionTokenDetails
{
    [JsonPropertyName("reasoning_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReasoningTokens { get; set; }
}