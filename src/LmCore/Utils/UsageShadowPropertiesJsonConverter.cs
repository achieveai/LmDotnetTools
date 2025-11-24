using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public class UsageShadowPropertiesJsonConverter : ShadowPropertiesJsonConverter<Usage>
{
    protected override Usage CreateInstance()
    {
        return new Usage();
    }

    protected override (bool handled, Usage instance) ReadProperty(
        ref Utf8JsonReader reader,
        Usage instance,
        string propertyName,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(propertyName);

        switch (propertyName)
        {
            case "input_tokens":
                // OpenAI uses "input_tokens" but we store as "prompt_tokens"
                var inputTokens = reader.GetInt32();
                return (true, instance with { PromptTokens = inputTokens });

            case "output_tokens":
                // OpenAI uses "output_tokens" but we store as "completion_tokens"
                var outputTokens = reader.GetInt32();
                return (true, instance with { CompletionTokens = outputTokens });

            default:
                return (false, instance);
        }
    }

    public override void Write(Utf8JsonWriter writer, Usage value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        // Only write non-default values to respect JsonIgnoreCondition.WhenWritingDefault
        if (value.PromptTokens != 0)
        {
            writer.WriteNumber("prompt_tokens", value.PromptTokens);
        }

        if (value.CompletionTokens != 0)
        {
            writer.WriteNumber("completion_tokens", value.CompletionTokens);
        }

        if (value.TotalTokens != 0)
        {
            writer.WriteNumber("total_tokens", value.TotalTokens);
        }

        if (value.TotalCost.HasValue)
        {
            writer.WriteNumber("total_cost", value.TotalCost.Value);
        }

        if (value.InputTokenDetails != null)
        {
            writer.WritePropertyName("input_tokens_details");
            JsonSerializer.Serialize(writer, value.InputTokenDetails, options);
        }

        if (value.OutputTokenDetails != null)
        {
            writer.WritePropertyName("output_tokens_details");
            JsonSerializer.Serialize(writer, value.OutputTokenDetails, options);
        }

        // Handle extra properties
        if (value.ExtraProperties != null && value.ExtraProperties.Count > 0)
        {
            foreach (var kvp in value.ExtraProperties)
            {
                writer.WritePropertyName(kvp.Key);
                JsonSerializer.Serialize(writer, kvp.Value, options);
            }
        }

        writer.WriteEndObject();
    }
}
