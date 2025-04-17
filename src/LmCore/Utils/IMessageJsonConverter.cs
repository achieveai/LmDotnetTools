using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// A JSON converter for IMessage that uses JsonDerivedType attributes for type discrimination
/// and respects existing type-specific converters.
/// </summary>
public class IMessageJsonConverter : JsonConverter<IMessage>
{
    // Type discriminator property name to use in JSON
    private const string TypeDiscriminatorPropertyName = "$type";
    
    // Cache for derived types and their discriminators from JsonDerivedType attributes
    private readonly Dictionary<string, Type> _discriminatorToType;
    private readonly Dictionary<Type, string> _typeToDiscriminator;
    
    // Cache for type-specific converters
    private readonly Dictionary<Type, JsonConverter> _typeConverters = new();

    public IMessageJsonConverter()
    {
        _discriminatorToType = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        _typeToDiscriminator = new Dictionary<Type, string>();
        
        // Get all JsonDerivedType attributes from the IMessage interface
        var attributes = typeof(IMessage).GetCustomAttributes<JsonDerivedTypeAttribute>().ToList();
        foreach (var attr in attributes)
        {
            string discriminator = GetDiscriminator(attr);
            _discriminatorToType[discriminator] = attr.DerivedType;
            _typeToDiscriminator[attr.DerivedType] = discriminator;
        }
    }

    public bool CanHaveMetadata => true;

    private string GetDiscriminator(JsonDerivedTypeAttribute attr)
    {
        // Get the type discriminator - could be string or other type
        if (attr.TypeDiscriminator is string typeDiscriminator)
        {
            // Convert the typeDiscriminator to a shorter form by removing "_message" suffix
            if (typeDiscriminator.EndsWith("_message", StringComparison.OrdinalIgnoreCase))
            {
                return typeDiscriminator.Substring(0, typeDiscriminator.Length - 8);
            }
            return typeDiscriminator;
        }
        
        // If not a string, use the type name as a fallback
        return attr.DerivedType.Name;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(IMessage).IsAssignableFrom(typeToConvert);
    }

    public override IMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected {JsonTokenType.StartObject} but got {reader.TokenType}");
        }

        using (var jsonDocument = JsonDocument.ParseValue(ref reader))
        {
            var rootElement = jsonDocument.RootElement;
            
            // Try to find the type discriminator
            Type targetType;
            if (rootElement.TryGetProperty(TypeDiscriminatorPropertyName, out var typeProperty))
            {
                string typeDiscriminator = typeProperty.GetString()!;
                // Use the new method to resolve the type from discriminator
                targetType = GetTypeFromDiscriminator(typeDiscriminator) ?? 
                    throw new JsonException($"Unknown type discriminator: {typeDiscriminator}");
            }
            else
            {
                // If no type discriminator is present, try to infer the type from the properties
                targetType = InferTypeFromProperties(rootElement);
            }

            // Get the json string of the object
            string json = rootElement.GetRawText();
            
            // Create new options without this converter to avoid infinite recursion
            var innerOptions = new JsonSerializerOptions(options);
            
            // Remove this converter to avoid infinite recursion
            var convertersToKeep = innerOptions.Converters
                .Where(c => !(c is IMessageJsonConverter))
                .ToList();
            
            innerOptions.Converters.Clear();
            foreach (var conv in convertersToKeep)
            {
                innerOptions.Converters.Add(conv);
            }

            // Use a type-specific converter if available, otherwise use default deserialization
            try
            {
                // Try to deserialize with the target type
                return (IMessage)JsonSerializer.Deserialize(json, targetType, innerOptions)!;
            }
            catch (Exception ex)
            {
                throw new JsonException($"Failed to deserialize message of type {targetType.Name}: {ex.Message}", ex);
            }
        }
    }

    public override void Write(Utf8JsonWriter writer, IMessage value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        Type valueType = value.GetType();
        
        // Create new options without this converter to avoid infinite recursion
        var innerOptions = new JsonSerializerOptions(options);
        
        // Remove this converter to avoid infinite recursion
        var convertersToKeep = innerOptions.Converters
            .Where(c => !(c is IMessageJsonConverter))
            .ToList();
        
        innerOptions.Converters.Clear();
        foreach (var conv in convertersToKeep)
        {
            innerOptions.Converters.Add(conv);
        }
        
        // Serialize with innerOptions
        var json = JsonSerializer.Serialize(value, valueType, innerOptions);
        using var document = JsonDocument.Parse(json);
        
        // Start writing the object with the discriminator
        writer.WriteStartObject();
        
        // Write type discriminator - always include it for IMessage types
        string discriminator;
        if (_typeToDiscriminator.TryGetValue(valueType, out var registeredDiscriminator))
        {
            discriminator = registeredDiscriminator;
        }
        else
        {
            // Get a discriminator from the type name if not found in the dictionary
            discriminator = GetDiscriminatorFromType(valueType);
        }
        
        writer.WriteString(TypeDiscriminatorPropertyName, discriminator);
        
        // Copy all properties from the temp document
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Skip writing the type discriminator if it's already in the document
            if (property.Name == TypeDiscriminatorPropertyName)
                continue;
                
            property.WriteTo(writer);
        }
        
        writer.WriteEndObject();
    }

    // Helper method to derive a discriminator from a type
    private string GetDiscriminatorFromType(Type type)
    {
        // First check if it's a known type with a mapping in GetTypeFromDiscriminator
        if (type == typeof(TextMessage)) return "text";
        if (type == typeof(ImageMessage)) return "image";
        if (type == typeof(ToolsCallMessage)) return "tools_call";
        if (type == typeof(TextUpdateMessage)) return "text_update";
        if (type == typeof(ToolsCallResultMessage)) return "tools_call_result";
        if (type == typeof(ToolsCallUpdateMessage)) return "tools_call_update";
        if (type == typeof(ToolsCallAggregateMessage)) return "tools_call_aggregate";
        if (type == typeof(UsageMessage)) return "usage";
        
        // If not a known type, fallback to name conversion
        string typeName = type.Name;
        
        // If the type name ends with "Message", remove it
        if (typeName.EndsWith("Message", StringComparison.OrdinalIgnoreCase))
        {
            typeName = typeName.Substring(0, typeName.Length - 7);
        }
        
        // Convert to snake_case
        string snakeCase = string.Concat(typeName.Select((x, i) => i > 0 && char.IsUpper(x) ? "_" + char.ToLower(x) : char.ToLower(x).ToString()));
        
        return snakeCase;
    }

    private Type InferTypeFromProperties(JsonElement element)
    {
        // Try to infer the message type based on the properties
        if (element.TryGetProperty("usage", out _) && !element.TryGetProperty("text", out _) 
            && !element.TryGetProperty("tool_calls", out _))
        {
            return typeof(UsageMessage);
        }
        else if (element.TryGetProperty("text", out _))
        {
            // Check if this is a TextUpdateMessage or a regular TextMessage
            if (element.TryGetProperty("isUpdate", out var isUpdateProp) && 
                isUpdateProp.ValueKind == JsonValueKind.True)
            {
                return typeof(TextUpdateMessage);
            }
            return typeof(TextMessage);
        }
        else if (element.TryGetProperty("image_data", out _))
        {
            return typeof(ImageMessage);
        }
        else if (element.TryGetProperty("tool_calls", out _))
        {
            return typeof(ToolsCallMessage);
        }
        else if (element.TryGetProperty("tool_call_updates", out _))
        {
            return typeof(ToolsCallUpdateMessage);
        }
        else if (element.TryGetProperty("tool_call_results", out _))
        {
            return typeof(ToolsCallResultMessage);
        }
        else if (element.TryGetProperty("tool_call_message", out _) && 
                 element.TryGetProperty("tool_call_result", out _))
        {
            return typeof(ToolsCallAggregateMessage);
        }
        
        // Default to TextMessage if we can't infer the type
        return typeof(TextMessage);
    }
    
    /// <summary>
    /// Resolves a Type from a type discriminator string.
    /// </summary>
    /// <param name="typeDiscriminator">The type discriminator string, with or without "_message" suffix.</param>
    /// <returns>The resolved Type, or null if not found.</returns>
    public Type? GetTypeFromDiscriminator(string typeDiscriminator)
    {
        if (string.IsNullOrEmpty(typeDiscriminator))
        {
            return null;
        }
        
        // Normalize the discriminator by removing "_message" suffix if present
        string normalizedDiscriminator = typeDiscriminator;
        if (normalizedDiscriminator.EndsWith("_message", StringComparison.OrdinalIgnoreCase))
        {
            normalizedDiscriminator = normalizedDiscriminator.Substring(0, normalizedDiscriminator.Length - 8);
        }
        
        // Check if we have this type in our discriminator map
        if (_discriminatorToType.TryGetValue(normalizedDiscriminator, out var type))
        {
            return type;
        }
        
        // If not in our map, try to match known types based on the normalized discriminator
        return normalizedDiscriminator.ToLowerInvariant() switch
        {
            "text" => typeof(TextMessage),
            "image" => typeof(ImageMessage),
            "tools_call" => typeof(ToolsCallMessage),
            "text_update" => typeof(TextUpdateMessage),
            "tools_call_result" => typeof(ToolsCallResultMessage),
            "tools_call_update" => typeof(ToolsCallUpdateMessage),
            "tools_call_aggregate" => typeof(ToolsCallAggregateMessage),
            "usage" => typeof(UsageMessage),
            _ => null
        };
    }
} 