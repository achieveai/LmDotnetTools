using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// A base JsonConverter for types that use shadow properties pattern, where extra properties
/// are stored in a JsonObject property (like Metadata) but serialized inline with the main properties.
/// </summary>
public abstract class ShadowJsonObjectPropertiesConverter<T> : JsonConverter<T> where T : class
{
    private readonly PropertyInfo[]? _jsonProperties;
    private readonly PropertyInfo? _metadataProperty;

    protected ShadowJsonObjectPropertiesConverter()
    {
        var type = typeof(T);
        // Get all properties with JsonPropertyName attribute
        _jsonProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() != null)
            .ToArray();

        // Find JsonObject property marked as metadata storage
        _metadataProperty = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => 
                p.PropertyType == typeof(JsonObject) &&
                p.Name == "Metadata" &&
                p.GetCustomAttribute<JsonIgnoreAttribute>() != null);
    }

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected {JsonTokenType.StartObject} but got {reader.TokenType}");
        }

        var metadata = new JsonObject();
        var instance = CreateInstance();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return SetMetadata(instance, metadata);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected {JsonTokenType.PropertyName} but got {reader.TokenType}");
            }

            string propertyName = reader.GetString()!;
            reader.Read();

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
                    p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == propertyName);
                
                if (property != null)
                {
                    var value = JsonSerializer.Deserialize(ref reader, property.PropertyType, options);
                    property.SetValue(instance, value);
                    continue;
                }
            }

            // If not handled, treat as metadata property
            var jsonNode = JsonNode.Parse(ref reader);
            metadata[propertyName] = jsonNode;
        }

        throw new JsonException("Expected end of object but reached end of data");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));

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

        // Write metadata properties inline
        var metadata = GetMetadata(value);
        if (metadata != null)
        {
            foreach (var property in metadata)
            {
                writer.WritePropertyName(property.Key);
                property.Value?.WriteTo(writer, options);
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Creates a new instance of the type being deserialized.
    /// </summary>
    protected abstract T CreateInstance();

    /// <summary>
    /// Gets the metadata JsonObject from the instance.
    /// Can be overridden if the property can't be found via reflection.
    /// </summary>
    protected virtual JsonObject? GetMetadata(T value)
    {
        if (_metadataProperty != null)
        {
            return (JsonObject?)_metadataProperty.GetValue(value);
        }
        return null;
    }

    /// <summary>
    /// Sets the metadata JsonObject on the instance.
    /// Can be overridden if the property can't be found via reflection.
    /// </summary>
    protected virtual T SetMetadata(T instance, JsonObject metadata)
    {
        if (_metadataProperty != null)
        {
            _metadataProperty.SetValue(instance, metadata);
        }
        return instance;
    }

    /// <summary>
    /// Reads a known property from the JSON reader. Override this to handle properties that can't be handled via reflection.
    /// </summary>
    /// <returns>A tuple containing: 
    /// - bool: True if the property was handled, false if it should be handled by reflection or treated as a metadata property
    /// - T: The potentially updated instance (for record types)</returns>
    protected virtual (bool handled, T instance) ReadProperty(ref Utf8JsonReader reader, T instance, string propertyName, JsonSerializerOptions options)
    {
        return (false, instance);
    }

    /// <summary>
    /// Writes the known properties to the JSON writer. Override this to handle properties that can't be handled via reflection.
    /// </summary>
    protected virtual void WriteProperties(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
    }
} 