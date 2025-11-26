using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

public sealed record ResponseFormat
{
    /// <summary>
    /// Predefined instance for JSON object response format
    /// </summary>
    public static readonly ResponseFormat JSON = new();

    /// <summary>
    /// The type of response format.
    /// - "json_object" (default): Request JSON output without schema validation
    /// - "json_schema": Request JSON output with schema validation
    /// </summary>
    [JsonPropertyName("type")]
    public string ResponseFormatType { get; init; } = "json_object";

    /// <summary>
    /// Schema validation definition for structured outputs (only used when type is "json_schema")
    /// </summary>
    [JsonPropertyName("json_schema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchemaDefinition? JsonSchema { get; init; }

    /// <summary>
    /// Creates a response format with JSON schema validation
    /// </summary>
    /// <param name="schemaName">Name identifier for the schema</param>
    /// <param name="schemaObject">JSON Schema object definition</param>
    /// <param name="strictValidation">Whether to enforce strict schema validation</param>
    /// <returns>A ResponseFormat configured for schema validation</returns>
    public static ResponseFormat CreateWithSchema(
        string schemaName,
        JsonSchemaObject schemaObject,
        bool strictValidation = true
    )
    {
        return new ResponseFormat
        {
            ResponseFormatType = "json_schema",
            JsonSchema = new JsonSchemaDefinition
            {
                Name = schemaName,
                Strict = strictValidation,
                Schema = schemaObject,
            },
        };
    }
}

/// <summary>
/// Defines the JSON schema validation requirements
/// </summary>
public sealed record JsonSchemaDefinition
{
    /// <summary>
    /// Name identifier for the schema
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Whether to enforce strict schema validation
    /// </summary>
    [JsonPropertyName("strict")]
    public bool Strict { get; init; } = true;

    /// <summary>
    /// The JSON Schema object definition
    /// </summary>
    [JsonPropertyName("schema")]
    public JsonSchemaObject Schema { get; init; } = new();
}

/// <summary>
/// Represents a JSON Schema object definition with strongly-typed properties
/// </summary>
public sealed record JsonSchemaObject
{
    /// <summary>
    /// The type of the schema object (e.g., "object", "array", "string", etc.)
    /// </summary>
    [JsonPropertyName("type")]
    public Union<string, IReadOnlyList<string>> Type { get; init; } =
        new Union<string, IReadOnlyList<string>>(["object", "null"]);

    /// <summary>
    /// Property definitions for object type schemas
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonSchemaObject>? Properties { get; init; }

    /// <summary>
    /// List of required property names
    /// </summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Required { get; init; }

    /// <summary>
    /// Whether additional properties are allowed in the object
    /// </summary>
    [JsonPropertyName("additionalProperties")]
    public bool AdditionalProperties { get; init; }

    /// <summary>
    /// Description of the schema object
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// Schema for the array items
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchemaObject? Items { get; init; }

    /// <summary>
    /// Enum values for string type schemas
    /// </summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Enum { get; init; }

    /// <summary>
    /// Minimum value for number/integer type schemas
    /// </summary>
    [JsonPropertyName("minimum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Minimum { get; init; }

    /// <summary>
    /// Maximum value for number/integer type schemas
    /// </summary>
    [JsonPropertyName("maximum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Maximum { get; init; }

    /// <summary>
    /// Minimum number of items for array type schemas
    /// </summary>
    [JsonPropertyName("minItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinItems { get; init; }

    /// <summary>
    /// Maximum number of items for array type schemas
    /// </summary>
    [JsonPropertyName("maxItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxItems { get; init; }

    /// <summary>
    /// Whether items in array must be unique
    /// </summary>
    [JsonPropertyName("uniqueItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UniqueItems { get; init; }

    /// <summary>
    /// Creates a builder for constructing a JSON Schema object more fluently
    /// </summary>
    public static JsonSchemaObjectBuilder Create(string type = "object")
    {
        return new JsonSchemaObjectBuilder(type);
    }

    /// <summary>
    /// Creates a new string schema with the given description
    /// </summary>
    public static JsonSchemaObject String(string? description = null)
    {
        return new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string"), Description = description };
    }

    /// <summary>
    /// Creates a new number schema with the given description
    /// </summary>
    public static JsonSchemaObject Number(string? description = null)
    {
        return new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("number"), Description = description };
    }

    /// <summary>
    /// Creates a new integer schema with the given description
    /// </summary>
    public static JsonSchemaObject Integer(string? description = null)
    {
        return new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("integer"), Description = description };
    }

    /// <summary>
    /// Creates a new boolean schema with the given description
    /// </summary>
    public static JsonSchemaObject Boolean(string? description = null)
    {
        return new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("boolean"), Description = description };
    }

    /// <summary>
    /// Creates a JSON Schema object for an array type
    /// </summary>
    /// <param name="items">Schema for the array items</param>
    /// <param name="description">Optional description of the array</param>
    /// <returns>A JSON Schema object for an array</returns>
    public static JsonSchemaObject Array(JsonSchemaObject items, string? description = null)
    {
        return new JsonSchemaObject
        {
            Type = JsonSchemaTypeHelper.ToType(["array", "null"]),
            Description = description,
            Items = items,
        };
    }

    /// <summary>
    /// Creates a new array schema with string items
    /// </summary>
    /// <param name="description">Optional description of the array</param>
    /// <param name="itemDescription">Optional description of the array items</param>
    /// <returns>A schema object of type array with string items</returns>
    public static JsonSchemaObject StringArray(string? description = null, string? itemDescription = null)
    {
        var items = new JsonSchemaObject
        {
            Type = JsonSchemaTypeHelper.ToType(["string", "null"]),
            Description = itemDescription,
        };

        return Array(items, description);
    }

    /// <summary>
    /// Creates a new array schema with number items
    /// </summary>
    /// <param name="description">Optional description of the array</param>
    /// <param name="itemDescription">Optional description of the array items</param>
    /// <returns>A schema object of type array with number items</returns>
    public static JsonSchemaObject NumberArray(string? description = null, string? itemDescription = null)
    {
        var items = new JsonSchemaObject { Type = "number", Description = itemDescription };

        return Array(items, description);
    }

    public static string GetJsonPrimaryType(JsonSchemaObject schemaObject)
    {
        if (schemaObject == null)
        {
            return "string";
        }

        // Handle both single string and list of strings
        if (schemaObject.Type.Is<string>())
        {
            var singleType = schemaObject.Type.Get<string>();
            return singleType == "null" ? "string" : singleType;
        }

        if (schemaObject.Type.Is<IReadOnlyList<string>>())
        {
            var typeList = schemaObject.Type.Get<IReadOnlyList<string>>();
            return typeList.FirstOrDefault(x => x != "null") ?? "string";
        }

        return "string";
    }
}

/// <summary>
/// Builder class for constructing JSON Schema objects in a fluent manner
/// </summary>
public class JsonSchemaObjectBuilder
{
    private readonly string _type;
    private readonly Dictionary<string, JsonSchemaObject> _properties = [];
    private readonly List<string> _required = [];
    private bool _additionalProperties;
    private string? _description;

    /// <summary>
    /// Creates a new JSON Schema object builder with the specified type
    /// </summary>
    public JsonSchemaObjectBuilder(string type)
    {
        _type = type;
    }

    /// <summary>
    /// Adds a property to the schema
    /// </summary>
    public JsonSchemaObjectBuilder WithProperty(string name, JsonSchemaObject property, bool required = false)
    {
        _properties.Add(name, property);
        if (required)
        {
            _required.Add(name);
        }
        return this;
    }

    /// <summary>
    /// Sets whether additional properties are allowed
    /// </summary>
    public JsonSchemaObjectBuilder AllowAdditionalProperties(bool allow)
    {
        _additionalProperties = allow;
        return this;
    }

    /// <summary>
    /// Sets the description of the schema
    /// </summary>
    public JsonSchemaObjectBuilder WithDescription(string description)
    {
        _description = description;
        return this;
    }

    /// <summary>
    /// Builds the JSON Schema object
    /// </summary>
    public JsonSchemaObject Build()
    {
        return new JsonSchemaObject
        {
            Type = _type,
            Properties = _properties,
            Required = _required.Count > 0 ? _required : null,
            AdditionalProperties = _additionalProperties,
            Description = _description,
        };
    }
}

public static class JsonSchemaTypeHelper
{
    public static bool IsNullable(JsonSchemaObject schemaObject)
    {
        return schemaObject.Type.Contains("null");
    }

    public static bool IsStringType(JsonSchemaObject schemaObject)
    {
        return schemaObject.Type.Contains("string");
    }

    public static bool IsTypeString(this JsonSchemaObject schemaObject, string type)
    {
        return schemaObject.Type.Contains(type);
    }

    public static bool Contains(this Union<string, IReadOnlyList<string>> type, string value)
    {
        return type.Is<string>() ? type.Get<string>() == value : type.Get<IReadOnlyList<string>>().Contains(value);
    }

    public static string GetTypeString(this Union<string, IReadOnlyList<string>> type)
    {
        return type.Is<string>()
            ? type.Get<string>()
            : type.Get<IReadOnlyList<string>>().FirstOrDefault(x => x != "null") ?? "object";
    }

    public static Union<string, IReadOnlyList<string>> ToType(string typeString)
    {
        return new Union<string, IReadOnlyList<string>>(typeString);
    }

    public static Union<string, IReadOnlyList<string>> ToType(string[] type)
    {
        return new Union<string, IReadOnlyList<string>>(type as IReadOnlyList<string>);
    }
}
