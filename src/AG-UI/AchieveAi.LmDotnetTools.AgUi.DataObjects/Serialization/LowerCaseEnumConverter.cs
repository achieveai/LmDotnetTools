using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;

/// <summary>
/// JSON converter that serializes enum values to lowercase strings
/// </summary>
/// <typeparam name="TEnum">The enum type to convert</typeparam>
public class LowerCaseEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
        {
            throw new JsonException($"Cannot convert empty string to {typeof(TEnum).Name}");
        }

        // Try case-insensitive parse to be flexible on input
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var result))
        {
            return result;
        }

        throw new JsonException($"Cannot convert '{value}' to {typeof(TEnum).Name}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        // Write enum value as lowercase
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}
