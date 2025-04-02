using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Core;

public record Usage
{
    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public int TotalTokens { get; init; }

    public CompletionTokenDetails? CompletionTokenDetails { get; init; }

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

public class CompletionTokenDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }
}