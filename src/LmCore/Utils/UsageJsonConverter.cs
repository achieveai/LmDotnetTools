using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public class UsageJsonConverter : JsonConverter<Usage>
{
    public override Usage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected {JsonTokenType.StartObject} but got {reader.TokenType}");
        }

        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        CompletionTokenDetails? completionTokenDetails = null;
        
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected {JsonTokenType.PropertyName} but got {reader.TokenType}");
            }

            string propertyName = reader.GetString()!;
            reader.Read();

            switch (propertyName)
            {
                case "prompt_tokens":
                    promptTokens = JsonSerializer.Deserialize<int>(ref reader, options);
                    break;

                case "completion_tokens":
                    completionTokens = JsonSerializer.Deserialize<int>(ref reader, options);
                    break;

                case "total_tokens":
                    totalTokens = JsonSerializer.Deserialize<int>(ref reader, options);
                    break;

                case "completion_token_details":
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        int reasoningTokens = 0;
                        bool hasNonDefaultValues = false;
                        
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName)
                            {
                                throw new JsonException($"Expected {JsonTokenType.PropertyName} but got {reader.TokenType}");
                            }

                            string detailsPropertyName = reader.GetString()!;
                            reader.Read();

                            if (detailsPropertyName == "reasoning_tokens")
                            {
                                reasoningTokens = JsonSerializer.Deserialize<int>(ref reader, options);
                                if (reasoningTokens != 0)
                                {
                                    hasNonDefaultValues = true;
                                }
                            }
                            else
                            {
                                // Skip unknown properties
                                reader.Skip();
                            }
                        }
                        
                        // Only set CompletionTokenDetails if it has non-default values
                        if (hasNonDefaultValues)
                        {
                            completionTokenDetails = new CompletionTokenDetails { ReasoningTokens = reasoningTokens };
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                    break;

                default:
                    // Skip unknown properties
                    reader.Skip();
                    break;
            }
        }
        
        return new Usage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            CompletionTokenDetails = completionTokenDetails
        };
    }

    public override void Write(Utf8JsonWriter writer, Usage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        bool hasAnyNonDefaultValues = false;

        // Only write non-default values
        if (value.PromptTokens != 0)
        {
            writer.WritePropertyName("prompt_tokens");
            writer.WriteNumberValue(value.PromptTokens);
            hasAnyNonDefaultValues = true;
        }

        if (value.CompletionTokens != 0)
        {
            writer.WritePropertyName("completion_tokens");
            writer.WriteNumberValue(value.CompletionTokens);
            hasAnyNonDefaultValues = true;
        }

        if (value.TotalTokens != 0)
        {
            writer.WritePropertyName("total_tokens");
            writer.WriteNumberValue(value.TotalTokens);
            hasAnyNonDefaultValues = true;
        }

        // Only write CompletionTokenDetails if it's not null and has non-default values
        if (value.CompletionTokenDetails != null && value.CompletionTokenDetails.ReasoningTokens != 0)
        {
            writer.WritePropertyName("completion_token_details");
            writer.WriteStartObject();
            writer.WritePropertyName("reasoning_tokens");
            writer.WriteNumberValue(value.CompletionTokenDetails.ReasoningTokens);
            writer.WriteEndObject();
            hasAnyNonDefaultValues = true;
        }

        // Write any extra properties
        if (value.ExtraProperties != null && value.ExtraProperties.Count > 0)
        {
            foreach (var kvp in value.ExtraProperties)
            {
                writer.WritePropertyName(kvp.Key);
                JsonSerializer.Serialize(writer, kvp.Value, options);
                hasAnyNonDefaultValues = true;
            }
        }

        writer.WriteEndObject();
    }
}