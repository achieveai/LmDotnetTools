using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Custom JsonConverter for ImmutableDictionary to handle serialization and deserialization.
/// </summary>
/// <typeparam name="TKey">The type of keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of values in the dictionary.</typeparam>
public class ImmutableDictionaryJsonConverter<TKey, TValue> : JsonConverter<ImmutableDictionary<TKey, TValue>>
    where TKey : notnull
{
    private readonly Type _keyType;
    private readonly Type _valueType;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImmutableDictionaryJsonConverter{TKey, TValue}"/> class.
    /// </summary>
    public ImmutableDictionaryJsonConverter()
    {
        _keyType = typeof(TKey);
        _valueType = typeof(TValue);
    }

    /// <inheritdoc />
    public override ImmutableDictionary<TKey, TValue> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected {JsonTokenType.StartObject} but got {reader.TokenType}");
        }

        var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return builder.ToImmutable();
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected {JsonTokenType.PropertyName} but got {reader.TokenType}");
            }

            // Read the key
            TKey key;
            if (typeof(TKey) == typeof(string))
            {
                key = (TKey)(object)reader.GetString()!;
            }
            else
            {
                // For non-string keys, deserialize using the key converter
                var propertyName = reader.GetString()!;
                using var document = JsonDocument.Parse($"\"{propertyName}\"");
                var keyReader = document.RootElement.GetRawText();
                key = JsonSerializer.Deserialize<TKey>(keyReader, options)!;
            }

            // Read the value
            _ = reader.Read();
            TValue value;

            value = JsonSerializer.Deserialize<TValue>(ref reader, options)!;

            builder.Add(key, value);
        }

        throw new JsonException("Expected end of object but reached end of data");
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        ImmutableDictionary<TKey, TValue> value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();

        foreach (var kvp in value)
        {
            string propertyName;

            if (typeof(TKey) == typeof(string))
            {
                propertyName = (string)(object)kvp.Key;
            }
            else
            {
                // For non-string keys, serialize using the key converter
                propertyName = JsonSerializer.Serialize(kvp.Key, options);
                // Remove the quotes that surround the serialized value
                propertyName = propertyName.Trim('"');
            }

            writer.WritePropertyName(propertyName);

            // Handle value serialization
            if (kvp.Value == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                // Handle primitive types directly
                if (typeof(TValue) == typeof(string))
                {
                    writer.WriteStringValue((string?)(object?)kvp.Value);
                }
                else if (typeof(TValue) == typeof(int))
                {
                    writer.WriteNumberValue((int)(object)kvp.Value);
                }
                else if (typeof(TValue) == typeof(long))
                {
                    writer.WriteNumberValue((long)(object)kvp.Value);
                }
                else if (typeof(TValue) == typeof(double))
                {
                    writer.WriteNumberValue((double)(object)kvp.Value);
                }
                else if (typeof(TValue) == typeof(decimal))
                {
                    writer.WriteNumberValue((decimal)(object)kvp.Value);
                }
                else if (typeof(TValue) == typeof(bool))
                {
                    writer.WriteBooleanValue((bool)(object)kvp.Value);
                }
                else if (typeof(TValue) == typeof(object))
                {
                    ImmutableDictionaryJsonConverter<TKey, TValue>.WriteObjectValue(writer, kvp.Value, options);
                }
                else
                {
                    JsonSerializer.Serialize(writer, kvp.Value, options);
                }
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteObjectValue(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value)
        {
            case string stringValue:
                writer.WriteStringValue(stringValue);
                break;

            case int intValue:
                writer.WriteNumberValue(intValue);
                break;

            case long longValue:
                writer.WriteNumberValue(longValue);
                break;

            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                break;

            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                break;

            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                break;

            case JsonElement jsonElement:
                jsonElement.WriteTo(writer);
                break;

            default:
                JsonSerializer.Serialize(writer, value, options);
                break;
        }
    }
}
