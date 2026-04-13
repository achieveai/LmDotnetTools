using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore.DynamicDescriptions;

/// <summary>
/// Represents cached metadata for an MCP tool method.
/// </summary>
public sealed class ToolMetadata
{
    /// <summary>
    /// The tool name as exposed via MCP (method name).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The default tool description from [Description] attribute.
    /// </summary>
    public string? DefaultDescription { get; init; }

    /// <summary>
    /// The declaring type for the tool (class with [McpServerToolType]).
    /// </summary>
    public required Type DeclaringType { get; init; }

    /// <summary>
    /// The method info for the tool.
    /// </summary>
    public required MethodInfo Method { get; init; }

    /// <summary>
    /// Cached parameter metadata for the tool.
    /// </summary>
    public required IReadOnlyList<ParameterMetadata> Parameters { get; init; }

    /// <summary>
    /// Pre-built JSON schema for the tool's input parameters.
    /// </summary>
    public required JsonObject InputSchema { get; init; }
}

/// <summary>
/// Represents cached metadata for a tool parameter.
/// </summary>
public sealed class ParameterMetadata
{
    /// <summary>
    /// The parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The default parameter description from [Description] attribute.
    /// </summary>
    public string? DefaultDescription { get; init; }

    /// <summary>
    /// The parameter type.
    /// </summary>
    public required Type ParameterType { get; init; }

    /// <summary>
    /// Whether the parameter is required (non-nullable and no default value).
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// Whether the parameter has a default value.
    /// </summary>
    public bool HasDefaultValue { get; init; }

    /// <summary>
    /// The default value if HasDefaultValue is true.
    /// </summary>
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Caches MCP tool metadata scanned from assemblies at startup.
/// </summary>
public sealed class McpToolMetadataCache
{
    private readonly Dictionary<string, ToolMetadata> _toolsByName;

    /// <summary>
    /// Gets all cached tool metadata.
    /// </summary>
    public IReadOnlyList<ToolMetadata> Tools { get; private set; }

    private McpToolMetadataCache(IReadOnlyList<ToolMetadata> tools)
    {
        Tools = tools;
        _toolsByName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates an empty cache that can be populated with AddTool.
    /// </summary>
    public McpToolMetadataCache()
    {
        _toolsByName = new Dictionary<string, ToolMetadata>(StringComparer.OrdinalIgnoreCase);
        Tools = [];
    }

    /// <summary>
    /// Adds a tool to the cache.
    /// </summary>
    /// <param name="tool">The tool metadata to add.</param>
    public void AddTool(ToolMetadata tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _toolsByName[tool.Name] = tool;
        // Update the read-only list
        Tools = [.. _toolsByName.Values];
    }

    /// <summary>
    /// Gets tool metadata by name.
    /// </summary>
    /// <param name="toolName">The tool name.</param>
    /// <returns>The tool metadata, or null if not found.</returns>
    public ToolMetadata? GetTool(string toolName)
    {
        return _toolsByName.TryGetValue(toolName, out var tool) ? tool : null;
    }

    /// <summary>
    /// Scans the specified assembly for [McpServerToolType] classes and [McpServerTool] methods.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>A populated cache of tool metadata.</returns>
    public static McpToolMetadataCache ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var tools = new List<ToolMetadata>();

        // Find all types with [McpServerToolType] attribute
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);

        foreach (var toolType in toolTypes)
        {
            // Find all methods with [McpServerTool] attribute
            var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

            foreach (var method in toolMethods)
            {
                var toolMetadata = CreateToolMetadata(toolType, method);
                tools.Add(toolMetadata);
            }
        }

        return new McpToolMetadataCache(tools);
    }

    /// <summary>
    /// Scans a single type for [McpServerTool] methods.
    /// The type must be decorated with [McpServerToolType].
    /// </summary>
    /// <param name="toolType">The type to scan.</param>
    /// <returns>A populated cache of tool metadata from the specified type.</returns>
    public static McpToolMetadataCache ScanType(Type toolType)
    {
        ArgumentNullException.ThrowIfNull(toolType);

        var tools = new List<ToolMetadata>();

        // Check if type has [McpServerToolType] attribute
        if (toolType.GetCustomAttribute<McpServerToolTypeAttribute>() == null)
        {
            return new McpToolMetadataCache(tools);
        }

        // Find all methods with [McpServerTool] attribute
        var toolMethods = toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

        foreach (var method in toolMethods)
        {
            var toolMetadata = CreateToolMetadata(toolType, method);
            tools.Add(toolMetadata);
        }

        return new McpToolMetadataCache(tools);
    }

    /// <summary>
    /// Creates tool metadata from a method decorated with [McpServerTool].
    /// </summary>
    private static ToolMetadata CreateToolMetadata(Type declaringType, MethodInfo method)
    {
        var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
        var descriptionAttr = method.GetCustomAttribute<DescriptionAttribute>();
        var parameters = CreateParameterMetadata(method);
        var inputSchema = BuildInputSchema(parameters);

        // Determine tool name:
        // 1. If explicitly specified in attribute, use it as-is
        // 2. Otherwise, convert PascalCase method name to snake_case
        var toolName = !string.IsNullOrEmpty(toolAttr?.Name)
            ? toolAttr.Name
            : ToSnakeCase(method.Name);

        return new ToolMetadata
        {
            Name = toolName,
            DefaultDescription = descriptionAttr?.Description,
            DeclaringType = declaringType,
            Method = method,
            Parameters = parameters,
            InputSchema = inputSchema
        };
    }

    /// <summary>
    /// Converts a PascalCase or camelCase string to snake_case.
    /// Example: "UpdateQuestion" -> "update_question", "getUser" -> "get_user"
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var result = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                // Add underscore before uppercase letter (except at the start)
                if (i > 0)
                {
                    _ = result.Append('_');
                }
                _ = result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                _ = result.Append(c);
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Creates parameter metadata for all parameters of a method.
    /// </summary>
    private static IReadOnlyList<ParameterMetadata> CreateParameterMetadata(MethodInfo method)
    {
        var parameters = new List<ParameterMetadata>();

        foreach (var param in method.GetParameters())
        {
            // Skip CancellationToken parameters - they're handled specially
            if (param.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            var descriptionAttr = param.GetCustomAttribute<DescriptionAttribute>();
            var isNullable = IsNullableType(param);
            var isRequired = !isNullable && !param.HasDefaultValue;

            parameters.Add(new ParameterMetadata
            {
                Name = param.Name ?? $"param{param.Position}",
                DefaultDescription = descriptionAttr?.Description,
                ParameterType = param.ParameterType,
                IsRequired = isRequired,
                HasDefaultValue = param.HasDefaultValue,
                DefaultValue = param.HasDefaultValue ? param.DefaultValue : null
            });
        }

        return parameters;
    }

    /// <summary>
    /// Determines if a parameter type is nullable.
    /// </summary>
    private static bool IsNullableType(ParameterInfo param)
    {
        // Check for Nullable<T>
        if (Nullable.GetUnderlyingType(param.ParameterType) != null)
        {
            return true;
        }

        // Check for nullable reference type annotation
        var nullabilityContext = new NullabilityInfoContext();
        var nullabilityInfo = nullabilityContext.Create(param);
        return nullabilityInfo.WriteState == NullabilityState.Nullable;
    }

    /// <summary>
    /// Builds a JSON schema for the tool's input parameters.
    /// </summary>
    private static JsonObject BuildInputSchema(IReadOnlyList<ParameterMetadata> parameters)
    {
        var schema = new JsonObject
        {
            ["type"] = "object"
        };

        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var param in parameters)
        {
            var paramSchema = BuildTypeSchema(param.ParameterType);

            // Description will be added dynamically at request time
            // Add default description as placeholder (will be replaced)
            if (!string.IsNullOrEmpty(param.DefaultDescription))
            {
                paramSchema["description"] = param.DefaultDescription;
            }

            properties[param.Name] = paramSchema;

            if (param.IsRequired)
            {
                required.Add(param.Name);
            }
        }

        schema["properties"] = properties;
        schema["required"] = required;

        return schema;
    }

    /// <summary>
    /// Builds a JSON schema for a .NET type, including complex object types.
    /// </summary>
    private static JsonObject BuildTypeSchema(Type type)
    {
        // Unwrap Nullable<T>
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        // Handle arrays
        if (underlyingType.IsArray)
        {
            var elementType = underlyingType.GetElementType();
            var arraySchema = new JsonObject { ["type"] = "array" };
            if (elementType != null)
            {
                arraySchema["items"] = BuildTypeSchema(elementType);
            }
            return arraySchema;
        }

        // Handle generic collections (List<T>, IList<T>, IEnumerable<T>, ICollection<T>, etc.)
        var collectionElementType = GetCollectionElementType(underlyingType);
        if (collectionElementType != null)
        {
            var arraySchema = new JsonObject { ["type"] = "array" };
            arraySchema["items"] = BuildTypeSchema(collectionElementType);
            return arraySchema;
        }

        // Handle enums
        if (underlyingType.IsEnum)
        {
            var enumSchema = new JsonObject { ["type"] = "string" };
            var enumValues = new JsonArray();
            foreach (var value in Enum.GetNames(underlyingType))
            {
                enumValues.Add(value);
            }
            enumSchema["enum"] = enumValues;
            return enumSchema;
        }

        // Handle primitive/built-in types
        var (jsonType, format) = GetJsonSchemaType(underlyingType);
        if (jsonType != "object" || IsPrimitiveJsonType(underlyingType))
        {
            var primitiveSchema = new JsonObject { ["type"] = jsonType };
            if (format != null)
            {
                primitiveSchema["format"] = format;
            }
            return primitiveSchema;
        }

        // Handle complex object types - build schema from properties
        return BuildObjectSchema(underlyingType);
    }

    /// <summary>
    /// Builds a JSON schema for a complex object type by reflecting its properties.
    /// </summary>
    private static JsonObject BuildObjectSchema(Type type)
    {
        var schema = new JsonObject { ["type"] = "object" };
        var properties = new JsonObject();
        var required = new JsonArray();

        var nullabilityContext = new NullabilityInfoContext();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Skip properties without getters
            if (!prop.CanRead)
            {
                continue;
            }

            // Get JSON property name from attribute or use property name
            var jsonPropertyName = GetJsonPropertyName(prop);
            var propSchema = BuildTypeSchema(prop.PropertyType);

            // Add description from Description attribute if present
            var descAttr = prop.GetCustomAttribute<DescriptionAttribute>();
            if (descAttr != null)
            {
                propSchema["description"] = descAttr.Description;
            }

            properties[jsonPropertyName] = propSchema;

            // Check if property is required (non-nullable and has 'required' modifier)
            if (IsPropertyRequired(prop, nullabilityContext))
            {
                required.Add(jsonPropertyName);
            }
        }

        schema["properties"] = properties;
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    /// <summary>
    /// Gets the JSON property name from JsonPropertyName attribute or property name.
    /// </summary>
    private static string GetJsonPropertyName(PropertyInfo prop)
    {
        var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        return jsonAttr?.Name ?? prop.Name;
    }

    /// <summary>
    /// Determines if a property is required based on nullability.
    /// </summary>
    private static bool IsPropertyRequired(PropertyInfo prop, NullabilityInfoContext nullabilityContext)
    {
        // Check for Nullable<T>
        if (Nullable.GetUnderlyingType(prop.PropertyType) != null)
        {
            return false;
        }

        // Check for nullable reference type annotation
        var nullabilityInfo = nullabilityContext.Create(prop);
        return nullabilityInfo.WriteState != NullabilityState.Nullable;
    }

    /// <summary>
    /// Checks if a type should be treated as a primitive JSON type (not an object with properties).
    /// </summary>
    private static bool IsPrimitiveJsonType(Type type)
    {
        return type == typeof(string)
            || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(float) || type == typeof(double) || type == typeof(decimal)
            || type == typeof(bool)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(Guid) || type == typeof(Uri)
            || type.IsEnum;
    }

    /// <summary>
    /// Gets the element type for generic collection types (List&lt;T&gt;, IList&lt;T&gt;, IEnumerable&lt;T&gt;, etc.).
    /// Returns null if the type is not a recognized collection type.
    /// </summary>
    private static Type? GetCollectionElementType(Type type)
    {
        // Skip string (which implements IEnumerable<char>)
        if (type == typeof(string))
        {
            return null;
        }

        // Check for generic IEnumerable<T> interface
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();

            // Check common collection types
            if (genericDef == typeof(List<>) ||
                genericDef == typeof(IList<>) ||
                genericDef == typeof(ICollection<>) ||
                genericDef == typeof(IEnumerable<>) ||
                genericDef == typeof(IReadOnlyList<>) ||
                genericDef == typeof(IReadOnlyCollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        // Check if type implements IEnumerable<T>
        var enumerableInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerableInterface?.GetGenericArguments()[0];
    }

    /// <summary>
    /// Maps a .NET type to JSON schema type and optional format.
    /// </summary>
    private static (string Type, string? Format) GetJsonSchemaType(Type type)
    {
        // Unwrap Nullable<T>
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        // Handle arrays
        if (underlyingType.IsArray)
        {
            return ("array", null);
        }

        // Handle common types
        return underlyingType switch
        {
            Type t when t == typeof(string) => ("string", null),
            Type t when t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) => ("integer", null),
            Type t when t == typeof(float) || t == typeof(double) || t == typeof(decimal) => ("number", null),
            Type t when t == typeof(bool) => ("boolean", null),
            Type t when t == typeof(DateTime) || t == typeof(DateTimeOffset) => ("string", "date-time"),
            Type t when t == typeof(Guid) => ("string", "uuid"),
            Type t when t == typeof(Uri) => ("string", "uri"),
            Type t when t.IsEnum => ("string", null),
            _ => ("object", null)
        };
    }
}
