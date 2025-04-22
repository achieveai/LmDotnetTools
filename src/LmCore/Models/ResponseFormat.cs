using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

public sealed record ResponseFormat
{
    /// <summary>
    /// Predefined instance for JSON object response format
    /// </summary>
    public static readonly ResponseFormat JSON = new ResponseFormat();

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
        bool strictValidation = true)
    {
        return new ResponseFormat
        {
            ResponseFormatType = "json_schema",
            JsonSchema = new JsonSchemaDefinition
            {
                Name = schemaName,
                Strict = strictValidation,
                Schema = schemaObject
            }
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
    public string Type { get; init; } = "object";

    /// <summary>
    /// Property definitions for object type schemas
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonSchemaProperty>? Properties { get; init; }

    /// <summary>
    /// List of required property names
    /// </summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Required { get; init; }

    /// <summary>
    /// Indicates if additional properties are allowed
    /// </summary>
    [JsonPropertyName("additionalProperties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AdditionalProperties { get; init; } = true;

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
    /// Creates a builder for constructing a JSON Schema object more fluently
    /// </summary>
    public static JsonSchemaObjectBuilder Create(string type = "object") => 
        new JsonSchemaObjectBuilder(type);

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
            Type = "array",
            Description = description,
            Items = items
        };
    }
}

/// <summary>
/// Represents a property in a JSON Schema definition
/// </summary>
public sealed record JsonSchemaProperty
{
    /// <summary>
    /// The type of the property (e.g., "string", "number", "boolean", etc.)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Description of the property
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// For string types, specifies the format (e.g., "date-time", "email", etc.)
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    /// <summary>
    /// For string types, specifies the enum values
    /// </summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Enum { get; init; }

    /// <summary>
    /// For number types, specifies the minimum value
    /// </summary>
    [JsonPropertyName("minimum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Minimum { get; init; }

    /// <summary>
    /// For number types, specifies the maximum value
    /// </summary>
    [JsonPropertyName("maximum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Maximum { get; init; }

    /// <summary>
    /// For array types, specifies the items schema
    /// </summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonSchemaObject? Items { get; init; }

    /// <summary>
    /// For array types, specifies the minimum number of items
    /// </summary>
    [JsonPropertyName("minItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinItems { get; init; }

    /// <summary>
    /// For array types, specifies the maximum number of items
    /// </summary>
    [JsonPropertyName("maxItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxItems { get; init; }

    /// <summary>
    /// For array types, specifies whether items must be unique
    /// </summary>
    [JsonPropertyName("uniqueItems")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UniqueItems { get; init; }

    /// <summary>
    /// Creates a new string property with the given description
    /// </summary>
    public static JsonSchemaProperty String(string? description = null) =>
        new JsonSchemaProperty { Type = "string", Description = description };

    /// <summary>
    /// Creates a new number property with the given description
    /// </summary>
    public static JsonSchemaProperty Number(string? description = null) =>
        new JsonSchemaProperty { Type = "number", Description = description };

    /// <summary>
    /// Creates a new integer property with the given description
    /// </summary>
    public static JsonSchemaProperty Integer(string? description = null) =>
        new JsonSchemaProperty { Type = "integer", Description = description };

    /// <summary>
    /// Creates a new boolean property with the given description
    /// </summary>
    public static JsonSchemaProperty Boolean(string? description = null) =>
        new JsonSchemaProperty { Type = "boolean", Description = description };

    /// <summary>
    /// Creates a new array property with the given item schema and description
    /// </summary>
    /// <param name="items">Schema for the array items</param>
    /// <param name="description">Optional description of the array</param>
    /// <param name="minItems">Optional minimum number of items</param>
    /// <param name="maxItems">Optional maximum number of items</param>
    /// <param name="uniqueItems">Whether array items must be unique</param>
    /// <returns>A schema property of type array</returns>
    public static JsonSchemaProperty Array(
        JsonSchemaObject items, 
        string? description = null,
        int? minItems = null,
        int? maxItems = null,
        bool uniqueItems = false)
    {
        return new JsonSchemaProperty 
        { 
            Type = "array", 
            Description = description,
            Items = items,
            MinItems = minItems,
            MaxItems = maxItems,
            UniqueItems = uniqueItems
        };
    }

    /// <summary>
    /// Creates a new array property with string items
    /// </summary>
    /// <param name="description">Optional description of the array</param>
    /// <param name="itemDescription">Optional description of the array items</param>
    /// <returns>A schema property of type array with string items</returns>
    public static JsonSchemaProperty StringArray(string? description = null, string? itemDescription = null)
    {
        var items = new JsonSchemaObject
        {
            Type = "string",
            Description = itemDescription
        };
        
        return Array(items, description);
    }

    /// <summary>
    /// Creates a new array property with number items
    /// </summary>
    /// <param name="description">Optional description of the array</param>
    /// <param name="itemDescription">Optional description of the array items</param>
    /// <returns>A schema property of type array with number items</returns>
    public static JsonSchemaProperty NumberArray(string? description = null, string? itemDescription = null)
    {
        var items = new JsonSchemaObject
        {
            Type = "number",
            Description = itemDescription
        };
        
        return Array(items, description);
    }
}

/// <summary>
/// Builder class for constructing JSON Schema objects in a fluent manner
/// </summary>
public class JsonSchemaObjectBuilder
{
    private readonly string _type;
    private readonly Dictionary<string, JsonSchemaProperty> _properties = new();
    private readonly List<string> _required = new();
    private bool _additionalProperties = true;
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
    public JsonSchemaObjectBuilder WithProperty(string name, JsonSchemaProperty property, bool required = false)
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
            Properties = _properties.Count > 0 ? _properties : null,
            Required = _required.Count > 0 ? _required : null,
            AdditionalProperties = _additionalProperties,
            Description = _description
        };
    }
}
