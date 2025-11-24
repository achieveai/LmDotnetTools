using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// A base JsonConverter for types that use shadow properties pattern, where extra properties
/// are stored in an ExtraProperties dictionary but serialized inline with the main properties.
/// </summary>
public abstract class ShadowPropertiesJsonConverter<T> : JsonConverter<T>
    where T : class
{
    private readonly PropertyInfo[]? _jsonProperties;
    private readonly PropertyInfo? _extraPropertiesProperty;

    protected ShadowPropertiesJsonConverter()
    {
        var type = typeof(T);
        // Get all properties with JsonPropertyName attribute
        _jsonProperties = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() != null)];

        // Find ImmutableDictionary property marked as extra properties storage
        _extraPropertiesProperty = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p =>
                p.PropertyType.IsGenericType
                && (p.Name == "ExtraProperties" || p.Name == "Metadata")
                && p.PropertyType.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>)
                && p.GetCustomAttribute<JsonIgnoreAttribute>() != null
            );
    }

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected {JsonTokenType.StartObject} but got {reader.TokenType}");
        }

        var extraProperties = ImmutableDictionary.CreateBuilder<string, object?>();
        var instance = CreateInstance();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return SetExtraProperties(instance, extraProperties.ToImmutable());
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected {JsonTokenType.PropertyName} but got {reader.TokenType}");
            }

            var propertyName = reader.GetString()!;
            _ = reader.Read();

            // Try to handle via the virtual method first
            var (customHandled, customInstance) = ReadProperty(ref reader, instance, propertyName, options);
            if (customHandled)
            {
                instance = customInstance;
                continue;
            }

            // Try reflection-based handling
            if (_jsonProperties != null)
            {
                var property = _jsonProperties.FirstOrDefault(p =>
                    p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == propertyName
                );

                if (property != null)
                {
                    var value = JsonSerializer.Deserialize(ref reader, property.PropertyType, options);
                    if (property.SetMethod != null)
                    {
                        // Readonly properties can't be set via reflection
                        property.SetValue(instance, value);
                    }

                    continue;
                }
            }

            // If not handled, treat as extra property
            var extraValue = ReadValue(ref reader, options);
            extraProperties.Add(propertyName, extraValue);
        }

        throw new JsonException("Expected end of object but reached end of data");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        // Let derived classes write their properties first
        WriteProperties(writer, value, options);

        // Write properties from reflection if any weren't handled
        if (_jsonProperties != null)
        {
            foreach (var property in _jsonProperties)
            {
                var attr = property.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (attr != null)
                {
                    var propertyValue = property.GetValue(value);
                    if (propertyValue != null)
                    {
                        writer.WritePropertyName(attr.Name!);
                        JsonSerializer.Serialize(writer, propertyValue, property.PropertyType, options);
                    }
                }
            }
        }

        // Write extra properties inline
        var extraProperties = GetExtraProperties(value);
        if (extraProperties != null)
        {
            foreach (var kvp in extraProperties)
            {
                writer.WritePropertyName(kvp.Key);
                JsonSerializer.Serialize(writer, kvp.Value, options);
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Creates a new instance of the type being deserialized.
    /// </summary>
    protected abstract T CreateInstance();

    /// <summary>
    /// Gets the extra properties dictionary from the instance.
    /// Can be overridden if the property can't be found via reflection.
    /// </summary>
    protected virtual ImmutableDictionary<string, object?> GetExtraProperties(T value)
    {
        return _extraPropertiesProperty != null
            ? (ImmutableDictionary<string, object?>?)_extraPropertiesProperty.GetValue(value)
                ?? ImmutableDictionary<string, object?>.Empty
            : ImmutableDictionary<string, object?>.Empty;
    }

    /// <summary>
    /// Sets the extra properties dictionary on the instance.
    /// Can be overridden if the property can't be found via reflection.
    /// </summary>
    protected virtual T SetExtraProperties(T instance, ImmutableDictionary<string, object?> extraProperties)
    {
        _extraPropertiesProperty?.SetValue(instance, extraProperties);
        return instance;
    }

    /// <summary>
    /// Reads a known property from the JSON reader. Override this to handle properties that can't be handled via reflection.
    /// </summary>
    /// <returns>A tuple containing:
    /// - bool: True if the property was handled, false if it should be handled by reflection or treated as an extra property
    /// - T: The potentially updated instance (for record types)</returns>
    protected virtual (bool handled, T instance) ReadProperty(
        ref Utf8JsonReader reader,
        T instance,
        string propertyName,
        JsonSerializerOptions options
    )
    {
        return (false, instance);
    }

    /// <summary>
    /// Writes the known properties to the JSON writer. Override this to handle properties that can't be handled via reflection.
    /// </summary>
    protected virtual void WriteProperties(Utf8JsonWriter writer, T value, JsonSerializerOptions options) { }

    private static object? ReadValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.True:
                return true;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var intValue))
                {
                    return intValue;
                }
                if (reader.TryGetInt64(out var longValue))
                {
                    return longValue;
                }
                if (reader.TryGetDouble(out var doubleValue))
                {
                    return doubleValue;
                }
                return reader.GetDecimal();
            case JsonTokenType.StartObject:
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    return document.RootElement.Clone();
                }
            case JsonTokenType.StartArray:
                using (var document = JsonDocument.ParseValue(ref reader))
                {
                    return document.RootElement.Clone();
                }

            case JsonTokenType.None:
            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray:
            case JsonTokenType.PropertyName:
            case JsonTokenType.Comment:
            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }
}
