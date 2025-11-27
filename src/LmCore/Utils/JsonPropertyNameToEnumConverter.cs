using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

public class JsonPropertyNameEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        var value = (reader.GetString() ?? throw new JsonException("Value was null.")).ToLowerInvariant();

        foreach (var field in typeToConvert.GetFields())
        {
            var attribute = field.GetCustomAttribute<JsonPropertyNameAttribute>();
            if (attribute?.Name == value)
            {
                return (T)Enum.Parse(typeToConvert, field.Name);
            }
        }

        throw new JsonException($"Unable to convert \"{value}\" to enum {typeToConvert}.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var field = value.GetType().GetField(value.ToString())!;
        var attribute = field.GetCustomAttribute<JsonPropertyNameAttribute>();

        if (attribute != null)
        {
            writer.WriteStringValue(attribute.Name);
        }
        else
        {
            writer.WriteStringValue(value.ToString());
        }
    }
}
