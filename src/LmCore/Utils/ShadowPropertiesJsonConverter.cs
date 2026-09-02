using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     A base JsonConverter for types that use shadow properties pattern, where extra properties
///     are stored in an ExtraProperties dictionary but serialized inline with the main properties.
/// </summary>
public abstract class ShadowPropertiesJsonConverter<T> : JsonConverter<T>
    where T : class
{
    private readonly PropertyInfo? _extraPropertiesProperty;
    private readonly JsonPropertyEntry[] _jsonProperties;

    protected ShadowPropertiesJsonConverter()
    {
        var type = typeof(T);

        // Resolve every [JsonPropertyName] property once, together with the [JsonIgnore] condition
        // and the default value it is compared against, so Read/Write do no attribute reflection.
        _jsonProperties =
        [
            .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => JsonPropertyEntry.Create(p, p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name))
                .Where(entry => entry != null)!,
        ];

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
            var entry = Array.Find(_jsonProperties, p => p.Name == propertyName);
            if (entry != null)
            {
                var value = JsonSerializer.Deserialize(ref reader, entry.Property.PropertyType, options);
                if (entry.Property.SetMethod != null)
                {
                    // Readonly properties can't be set via reflection
                    entry.Property.SetValue(instance, value);
                }

                continue;
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
        foreach (var entry in _jsonProperties)
        {
            var propertyValue = entry.Property.GetValue(value);
            if (!entry.ShouldWrite(propertyValue))
            {
                continue;
            }

            writer.WritePropertyName(entry.Name);
            JsonSerializer.Serialize(writer, propertyValue, entry.Property.PropertyType, options);
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
    ///     Creates a new instance of the type being deserialized.
    /// </summary>
    protected abstract T CreateInstance();

    /// <summary>
    ///     Gets the extra properties dictionary from the instance.
    ///     Can be overridden if the property can't be found via reflection.
    /// </summary>
    protected virtual ImmutableDictionary<string, object?> GetExtraProperties(T value)
    {
        return _extraPropertiesProperty != null
            ? (ImmutableDictionary<string, object?>?)_extraPropertiesProperty.GetValue(value)
                ?? ImmutableDictionary<string, object?>.Empty
            : ImmutableDictionary<string, object?>.Empty;
    }

    /// <summary>
    ///     Sets the extra properties dictionary on the instance.
    ///     Can be overridden if the property can't be found via reflection.
    /// </summary>
    protected virtual T SetExtraProperties(T instance, ImmutableDictionary<string, object?> extraProperties)
    {
        _extraPropertiesProperty?.SetValue(instance, extraProperties);
        return instance;
    }

    /// <summary>
    ///     Reads a known property from the JSON reader. Override this to handle properties that can't be handled via
    ///     reflection.
    /// </summary>
    /// <returns>
    ///     A tuple containing:
    ///     - bool: True if the property was handled, false if it should be handled by reflection or treated as an extra
    ///     property
    ///     - T: The potentially updated instance (for record types)
    /// </returns>
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
    ///     Writes the known properties to the JSON writer. Override this to handle properties that can't be handled via
    ///     reflection.
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

    /// <summary>
    ///     A <c>[JsonPropertyName]</c> property with its <c>[JsonIgnore]</c> condition resolved once.
    /// </summary>
    /// <param name="Property">The CLR property.</param>
    /// <param name="Name">The JSON name from <see cref="JsonPropertyNameAttribute"/>.</param>
    /// <param name="Condition">The <see cref="JsonIgnoreAttribute.Condition"/>, or <see cref="JsonIgnoreCondition.Never"/> without the attribute.</param>
    /// <param name="DefaultValue">
    ///     Boxed <c>default</c> of the property type — <c>false</c>, <c>0</c>, an enum's zero member —
    ///     or null for reference and nullable types, which the null check already covers.
    /// </param>
    private sealed record JsonPropertyEntry(
        PropertyInfo Property,
        string Name,
        JsonIgnoreCondition Condition,
        object? DefaultValue
    )
    {
        /// <summary>Resolves the entry for <paramref name="property"/>, or null when it carries no JSON name.</summary>
        public static JsonPropertyEntry? Create(PropertyInfo property, string? jsonName) =>
            jsonName == null
                ? null
                : new JsonPropertyEntry(
                    property,
                    jsonName,
                    property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition ?? JsonIgnoreCondition.Never,
                    property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null
                );

        /// <summary>
        ///     Mirrors what <see cref="JsonSerializer"/> itself would do with the attribute: nulls are
        ///     never written (the converter has always behaved as if every property were
        ///     <see cref="JsonIgnoreCondition.WhenWritingNull"/>), <see cref="JsonIgnoreCondition.Always"/>
        ///     is skipped, and <see cref="JsonIgnoreCondition.WhenWritingDefault"/> also drops a value
        ///     equal to the type's default.
        /// </summary>
        public bool ShouldWrite(object? value)
        {
            if (value == null || Condition == JsonIgnoreCondition.Always)
            {
                return false;
            }

            if (value is JsonElement je && je.ValueKind == JsonValueKind.Undefined)
            {
                return false;
            }

            return Condition != JsonIgnoreCondition.WhenWritingDefault || !object.Equals(value, DefaultValue);
        }
    }
}
