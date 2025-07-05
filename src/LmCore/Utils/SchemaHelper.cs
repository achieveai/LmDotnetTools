namespace AchieveAi.LmDotnetTools.LmCore.Utils;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
/// Helper utilities for working with JSON schema objects
/// </summary>
public static class SchemaHelper
{
    /// <summary>
    /// JsonSerializerOptions configured with Union converters for schema deserialization
    /// </summary>
    private static readonly JsonSerializerOptions SchemaDeserializationOptions = new()
    {
        Converters = 
        {
            new UnionJsonConverter<string, IReadOnlyList<string>>()
        }
    };

    /// <summary>
    /// JsonSerializerOptions configured with Union converters for schema serialization (debug output)
    /// </summary>
    private static readonly JsonSerializerOptions SchemaSerializationOptions = new()
    {
        WriteIndented = true,
        Converters = 
        {
            new UnionJsonConverter<string, IReadOnlyList<string>>()
        }
    };

    /// <summary>
    /// Cache to store already generated schema objects keyed by .NET type.
    /// Creation can be expensive, so we compute once and reuse.
    /// </summary>
    private static readonly Dictionary<Type, JsonSchemaObject> _schemaCache = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Creates a JsonSchemaObject from a .NET Type
    /// </summary>
    /// <param name="type">The .NET type to convert</param>
    /// <returns>A JsonSchemaObject representing the type</returns>
    public static JsonSchemaObject CreateJsonSchemaFromType(Type type)
    {
        // Fast path - return cached schema if present
        if (_schemaCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        lock (_lock)
        {
            if (_schemaCache.TryGetValue(type, out cached))
            {
                return cached;
            }

            JsonNode dotnetSchema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(type);
            JsonSchemaObject originalSchema = JsonSerializer.Deserialize<JsonSchemaObject>(dotnetSchema, SchemaDeserializationOptions)!;
            JsonSchemaObject transformedSchema = TransformSchemaUnions(originalSchema);

            _schemaCache[type] = transformedSchema;

            return transformedSchema;
        }
    }

    /// <summary>
    /// Transforms Union types in the schema to convert single-element arrays to simple string values
    /// for compatibility with OpenAI's API requirements.
    /// Also ensures all properties are marked as required for OpenAI structured outputs.
    /// </summary>
    /// <param name="schema">The schema to transform</param>
    /// <returns>The transformed schema with cleaned up Union types and proper required fields</returns>
    private static JsonSchemaObject TransformSchemaUnions(JsonSchemaObject schema)
    {
        // First transform all properties
        var transformedProperties = TransformPropertiesDictionary(schema.Properties);
        
        // For OpenAI structured outputs, ALL properties must be in the required array
        // This is a requirement of OpenAI's structured output API regardless of whether
        // properties are nullable or not. Optional properties are handled via union types with null.
        var requiredPropertyNames = new List<string>();
        
        if (transformedProperties != null)
        {
            foreach (var kvp in transformedProperties)
            {
                requiredPropertyNames.Add(kvp.Key);
                Console.WriteLine($"[DEBUG] Added '{kvp.Key}' to required array (OpenAI requires all properties)");
            }
        }
        
        // Transform the Items schema recursively if it exists (for array types)
        var transformedItems = schema.Items != null ? TransformSchemaUnions(schema.Items) : null;
        
        return new JsonSchemaObject
        {
            Type = TransformUnionType(schema.Type),
            Description = schema.Description,
            Properties = transformedProperties,
            Required = requiredPropertyNames, // All properties are required after transformation
            Items = transformedItems, // Recursively transformed items
            AdditionalProperties = false,
            Enum = schema.Enum,
            Minimum = schema.Minimum,
            Maximum = schema.Maximum,
            MinItems = schema.MinItems,
            MaxItems = schema.MaxItems,
            UniqueItems = schema.UniqueItems
        };
    }

    /// <summary>
    /// Transforms the properties dictionary to apply Union type cleaning and ensure all nested objects have additionalProperties = false
    /// </summary>
    /// <param name="properties">The properties dictionary to transform</param>
    /// <returns>The transformed properties dictionary</returns>
    private static IReadOnlyDictionary<string, JsonSchemaProperty>? TransformPropertiesDictionary(
        IReadOnlyDictionary<string, JsonSchemaProperty>? properties)
    {
        if (properties == null) return null;

        var transformedProperties = new Dictionary<string, JsonSchemaProperty>();
        
        foreach (var kvp in properties)
        {
            var originalProperty = kvp.Value;
            var transformedProperty = originalProperty with
            {
                Type = TransformUnionType(originalProperty.Type),
                Properties = TransformPropertiesDictionary(originalProperty.Properties),
                Items = originalProperty.Items != null ? TransformSchemaUnions(originalProperty.Items) : null,
                // Always set AdditionalProperties = false for OpenAI structured outputs
                AdditionalProperties = false
            };
            
            transformedProperties[kvp.Key] = transformedProperty;
        }

        return transformedProperties;
    }

    /// <summary>
    /// Transforms a Union type, converting nullable types to non-nullable for OpenAI structured outputs
    /// OpenAI structured outputs don't support nullable/optional fields - everything must be required
    /// </summary>
    private static Union<string, IReadOnlyList<string>> TransformUnionType(
        Union<string, IReadOnlyList<string>> unionType)
    {
        // If it's already a string, return as-is
        if (unionType.Is<string>())
        {
            var stringValue = unionType.Get<string>();
            // If it's a nullable type like "null", convert to a reasonable default
            if (stringValue == "null")
            {
                return JsonSchemaTypeHelper.ToType("string");
            }
            return unionType;
        }

        // If it's a list, check if we can simplify it
        if (unionType.Is<IReadOnlyList<string>>())
        {
            var typeList = unionType.Get<IReadOnlyList<string>>();
            
            // Filter out null types and get the remaining types
            var nonNullTypes = typeList.Where(t => t != "null").ToList();
            
            // If we have exactly one non-null type, convert to simple string
            if (nonNullTypes.Count == 1)
            {
                return JsonSchemaTypeHelper.ToType(nonNullTypes[0]);
            }
            
            // If we have multiple non-null types, keep the first one as a string
            // (This shouldn't happen much in practice for well-formed schemas)
            if (nonNullTypes.Count > 1)
            {
                return JsonSchemaTypeHelper.ToType(nonNullTypes[0]);
            }
            
            // If we only had null types, default to "string" for OpenAI compatibility
            return JsonSchemaTypeHelper.ToType("string");
        }

        // Return as-is for other cases
        return unionType;
    }
}