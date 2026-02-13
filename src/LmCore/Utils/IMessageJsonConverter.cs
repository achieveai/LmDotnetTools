using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     A JSON converter for IMessage that uses JsonDerivedType attributes for type discrimination
///     and respects existing type-specific converters.
/// </summary>
public class IMessageJsonConverter : JsonConverter<IMessage>
{
    // Type discriminator property name to use in JSON
    private const string TypeDiscriminatorPropertyName = "$type";

    // Cache for derived types and their discriminators from JsonDerivedType attributes
    private readonly Dictionary<string, Type> _discriminatorToType;

    // Cache for type-specific converters
    private readonly Dictionary<Type, JsonConverter> _typeConverters = [];
    private readonly Dictionary<Type, string> _typeToDiscriminator;

    public IMessageJsonConverter()
    {
        _discriminatorToType = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        _typeToDiscriminator = [];

        // Get all JsonDerivedType attributes from the IMessage interface
        var attributes = typeof(IMessage).GetCustomAttributes<JsonDerivedTypeAttribute>().ToList();
        foreach (var attr in attributes)
        {
            var discriminator = GetDiscriminator(attr);
            _discriminatorToType[discriminator] = attr.DerivedType;
            _typeToDiscriminator[attr.DerivedType] = discriminator;
        }
    }

    public static bool CanHaveMetadata => true;

    private static string GetDiscriminator(JsonDerivedTypeAttribute attr)
    {
        // Get the type discriminator - could be string or other type
        if (attr.TypeDiscriminator is string typeDiscriminator)
        {
            // Convert the typeDiscriminator to a shorter form by removing "_message" suffix
            return typeDiscriminator.EndsWith("_message", StringComparison.OrdinalIgnoreCase)
                ? typeDiscriminator[..^8]
                : typeDiscriminator;
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

        using var jsonDocument = JsonDocument.ParseValue(ref reader);
        var rootElement = jsonDocument.RootElement;

        var hasTypeDiscriminator = rootElement.TryGetProperty(TypeDiscriminatorPropertyName, out var typeProperty);
        var typeDiscriminator = hasTypeDiscriminator ? typeProperty.GetString() : null;

        // Backward compatibility: legacy server tool messages deserialize to unified types.
        if (IsServerToolUseDiscriminator(typeDiscriminator) || IsLegacyServerToolUseShape(rootElement))
        {
            return DeserializeServerToolUse(rootElement);
        }

        if (IsServerToolResultDiscriminator(typeDiscriminator) || IsLegacyServerToolResultShape(rootElement))
        {
            return DeserializeServerToolResult(rootElement);
        }

        // Try to find the type discriminator
        Type targetType;
        if (hasTypeDiscriminator)
        {
            // Use the new method to resolve the type from discriminator
            targetType =
                GetTypeFromDiscriminator(typeDiscriminator!)
                ?? throw new JsonException($"Unknown type discriminator: {typeDiscriminator!}");
        }
        else
        {
            // If no type discriminator is present, try to infer the type from the properties
            targetType = InferTypeFromProperties(rootElement);
        }

        // Get the json string of the object
        var json = rootElement.GetRawText();

        // Create new options without this converter to avoid infinite recursion
        var innerOptions = new JsonSerializerOptions(options);

        // Remove this converter to avoid infinite recursion
        var convertersToKeep = innerOptions.Converters.Where(c => c is not IMessageJsonConverter).ToList();

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

    public override void Write(Utf8JsonWriter writer, IMessage value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        var valueType = value.GetType();

        // Create new options without this converter to avoid infinite recursion
        var innerOptions = new JsonSerializerOptions(options);

        // Remove this converter to avoid infinite recursion
        var convertersToKeep = innerOptions.Converters.Where(c => c is not IMessageJsonConverter).ToList();

        innerOptions.Converters.Clear();
        foreach (var conv in convertersToKeep)
        {
            innerOptions.Converters.Add(conv);
        }

        if (value is ToolCallMessage toolCallMessage
            && toolCallMessage.ExecutionTarget == ExecutionTarget.ProviderServer)
        {
            WriteWithDiscriminatorOverride(writer, value, valueType, innerOptions, "server_tool_use");
            return;
        }

        if (value is ToolCallResultMessage toolCallResultMessage
            && toolCallResultMessage.ExecutionTarget == ExecutionTarget.ProviderServer)
        {
            WriteWithDiscriminatorOverride(writer, value, valueType, innerOptions, "server_tool_result");
            return;
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
            {
                continue;
            }

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    // Helper method to derive a discriminator from a type
    private static string GetDiscriminatorFromType(Type type)
    {
        // First check if it's a known type with a mapping in GetTypeFromDiscriminator
        if (type == typeof(TextMessage))
        {
            return "text";
        }

        if (type == typeof(ImageMessage))
        {
            return "image";
        }

        if (type == typeof(ToolsCallMessage))
        {
            return "tools_call";
        }

        if (type == typeof(ToolCallMessage))
        {
            return "tool_call";
        }

        if (type == typeof(TextUpdateMessage))
        {
            return "text_update";
        }

        if (type == typeof(ToolsCallResultMessage))
        {
            return "tools_call_result";
        }

        if (type == typeof(ToolCallResultMessage))
        {
            return "tool_call_result";
        }

        if (type == typeof(ToolsCallUpdateMessage))
        {
            return "tools_call_update";
        }

        if (type == typeof(ToolCallUpdateMessage))
        {
            return "tool_call_update";
        }

        if (type == typeof(ToolsCallAggregateMessage))
        {
            return "tools_call_aggregate";
        }

        if (type == typeof(UsageMessage))
        {
            return "usage";
        }

        if (type == typeof(ReasoningMessage))
        {
            return "reasoning";
        }

        if (type == typeof(ReasoningUpdateMessage))
        {
            return "reasoning_update";
        }

        if (type == typeof(TextWithCitationsMessage))
        {
            return "text_with_citations";
        }

        // If not a known type, fallback to name conversion
        var typeName = type.Name;

        // If the type name ends with "Message", remove it
        if (typeName.EndsWith("Message", StringComparison.OrdinalIgnoreCase))
        {
            typeName = typeName[..^7];
        }

        // Convert to snake_case
        var snakeCase = string.Concat(
            typeName.Select((x, i) => i > 0 && char.IsUpper(x) ? "_" + char.ToLower(x) : char.ToLower(x).ToString())
        );

        return snakeCase;
    }

    private static Type InferTypeFromProperties(JsonElement element)
    {
        // Try to infer the message type based on the properties
        if (
            element.TryGetProperty("usage", out _)
            && !element.TryGetProperty("text", out _)
            && !element.TryGetProperty("tool_calls", out _)
        )
        {
            return typeof(UsageMessage);
        }

        if (element.TryGetProperty("text", out _))
        {
            // Check if this is a TextUpdateMessage or a regular TextMessage
            return
                element.TryGetProperty("isUpdate", out var isUpdateProp) && isUpdateProp.ValueKind == JsonValueKind.True
                ? typeof(TextUpdateMessage)
                : typeof(TextMessage);
        }

        if (element.TryGetProperty("image_data", out _))
        {
            return typeof(ImageMessage);
        }

        if (element.TryGetProperty("tool_calls", out _))
        {
            return typeof(ToolsCallMessage);
        }

        if (element.TryGetProperty("tool_call_updates", out _))
        {
            return typeof(ToolsCallUpdateMessage);
        }

        if (element.TryGetProperty("tool_call_results", out _))
        {
            return typeof(ToolsCallResultMessage);
        }

        if (element.TryGetProperty("tool_use_id", out _) && element.TryGetProperty("result", out _))
        {
            return typeof(ToolCallResultMessage);
        }

        if (
            element.TryGetProperty("tool_use_id", out _)
            && (element.TryGetProperty("input", out _) || element.TryGetProperty("tool_name", out _))
        )
        {
            return typeof(ToolCallMessage);
        }

        if (element.TryGetProperty("tool_call_message", out _) && element.TryGetProperty("tool_call_result", out _))
        {
            return typeof(ToolsCallAggregateMessage);
        }
        // Singular tool call types (check after plural types)
        else if (element.TryGetProperty("function_name", out _) || element.TryGetProperty("function_args", out _))
        {
            // Check if this is an update or a complete message
            return
                element.TryGetProperty("isUpdate", out var isUpdateProp) && isUpdateProp.ValueKind == JsonValueKind.True
                ? typeof(ToolCallUpdateMessage)
                : typeof(ToolCallMessage);
        }
        else if (element.TryGetProperty("result", out _) && element.TryGetProperty("tool_call_id", out _))
        {
            return typeof(ToolCallResultMessage);
        }

        // Default to TextMessage if we can't infer the type
        return typeof(TextMessage);
    }

    /// <summary>
    ///     Resolves a Type from a type discriminator string.
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
        var normalizedDiscriminator = typeDiscriminator;
        if (normalizedDiscriminator.EndsWith("_message", StringComparison.OrdinalIgnoreCase))
        {
            normalizedDiscriminator = normalizedDiscriminator[..^8];
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
            "tool_call" => typeof(ToolCallMessage),
            "text_update" => typeof(TextUpdateMessage),
            "tools_call_result" => typeof(ToolsCallResultMessage),
            "tool_call_result" => typeof(ToolCallResultMessage),
            "tools_call_update" => typeof(ToolsCallUpdateMessage),
            "tool_call_update" => typeof(ToolCallUpdateMessage),
            "tools_call_aggregate" => typeof(ToolsCallAggregateMessage),
            "usage" => typeof(UsageMessage),
            "reasoning" => typeof(ReasoningMessage),
            "reasoning_update" => typeof(ReasoningUpdateMessage),
            "server_tool_use" => typeof(ToolCallMessage),
            "server_tool_result" => typeof(ToolCallResultMessage),
            "text_with_citations" => typeof(TextWithCitationsMessage),
            _ => null,
        };
    }

    private static void WriteWithDiscriminatorOverride(
        Utf8JsonWriter writer,
        object value,
        Type valueType,
        JsonSerializerOptions innerOptions,
        string discriminator
    )
    {
        var json = JsonSerializer.Serialize(value, valueType, innerOptions);
        using var document = JsonDocument.Parse(json);

        writer.WriteStartObject();
        writer.WriteString(TypeDiscriminatorPropertyName, discriminator);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == TypeDiscriminatorPropertyName)
            {
                continue;
            }

            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static bool IsServerToolUseDiscriminator(string? typeDiscriminator)
    {
        return string.Equals(typeDiscriminator, "server_tool_use", StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeDiscriminator, "server_tool_use_message", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServerToolResultDiscriminator(string? typeDiscriminator)
    {
        return string.Equals(typeDiscriminator, "server_tool_result", StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeDiscriminator, "server_tool_result_message", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyServerToolUseShape(JsonElement element)
    {
        return element.TryGetProperty("tool_use_id", out _)
            && !element.TryGetProperty("result", out _)
            && (element.TryGetProperty("input", out _) || element.TryGetProperty("tool_name", out _));
    }

    private static bool IsLegacyServerToolResultShape(JsonElement element)
    {
        return element.TryGetProperty("tool_use_id", out _) && element.TryGetProperty("result", out _);
    }

    private static ToolCallMessage DeserializeServerToolUse(JsonElement element)
    {
        return new ToolCallMessage
        {
            ToolCallId = GetStringProperty(element, "tool_call_id", "tool_use_id", "id"),
            FunctionName = GetStringProperty(element, "function_name", "tool_name", "name"),
            FunctionArgs = GetJsonStringProperty(element, "{}", "function_args", "input"),
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = ParseRoleProperty(element, Role.Assistant),
            FromAgent = GetStringProperty(element, "from_agent", "fromAgent"),
            GenerationId = GetStringProperty(element, "generation_id", "generationId"),
            ThreadId = GetStringProperty(element, "threadId"),
            RunId = GetStringProperty(element, "runId"),
            ParentRunId = GetStringProperty(element, "parentRunId"),
            MessageOrderIdx = GetIntProperty(element, "messageOrderIdx"),
        };
    }

    private static ToolCallResultMessage DeserializeServerToolResult(JsonElement element)
    {
        return new ToolCallResultMessage
        {
            ToolCallId = GetStringProperty(element, "tool_call_id", "tool_use_id"),
            ToolName = GetStringProperty(element, "tool_name", "function_name", "name"),
            Result = GetJsonStringProperty(element, "{}", "result"),
            IsError = GetBoolProperty(element, false, "is_error", "isError"),
            ErrorCode = GetStringProperty(element, "error_code", "errorCode"),
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = ParseRoleProperty(element, Role.Assistant),
            FromAgent = GetStringProperty(element, "from_agent", "fromAgent"),
            GenerationId = GetStringProperty(element, "generation_id", "generationId"),
            ThreadId = GetStringProperty(element, "threadId"),
            RunId = GetStringProperty(element, "runId"),
            ParentRunId = GetStringProperty(element, "parentRunId"),
            MessageOrderIdx = GetIntProperty(element, "messageOrderIdx"),
        };
    }

    private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            return property.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => property.GetString(),
                _ => property.GetRawText(),
            };
        }

        return null;
    }

    private static string GetJsonStringProperty(JsonElement element, string defaultValue, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            var value = property.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => defaultValue,
                JsonValueKind.String => property.GetString(),
                _ => property.GetRawText(),
            };

            return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
        }

        return defaultValue;
    }

    private static bool GetBoolProperty(JsonElement element, bool defaultValue, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }

            if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static int? GetIntProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static Role ParseRoleProperty(JsonElement element, Role defaultRole)
    {
        if (!element.TryGetProperty("role", out var roleProperty) || roleProperty.ValueKind != JsonValueKind.String)
        {
            return defaultRole;
        }

        return Enum.TryParse<Role>(roleProperty.GetString(), ignoreCase: true, out var parsedRole)
            ? parsedRole
            : defaultRole;
    }
}
