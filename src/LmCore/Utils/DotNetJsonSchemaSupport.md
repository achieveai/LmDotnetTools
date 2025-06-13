# .NET 9+ JSON Schema Generation Guide

## Overview

.NET 9 introduces the `JsonSchemaExporter` class, which provides built-in support for generating JSON schemas directly from .NET types. This eliminates the need for third-party libraries and provides seamless integration with `System.Text.Json`.

## Prerequisites

- .NET 9.0 or later
- `System.Text.Json` (included in .NET 9)
- `System.ComponentModel` (for description attributes)

## Basic Usage

### Simple Schema Generation

```csharp
using System.Text.Json;
using System.Text.Json.Schema;

// Define your model
public record Person(string Name, int Age, string? Address = null);

// Generate schema
JsonSerializerOptions options = JsonSerializerOptions.Default;
JsonNode schema = options.GetJsonSchemaAsNode(typeof(Person));

Console.WriteLine(schema.ToString());
```

**Output:**
```json
{
  "type": ["object", "null"],
  "properties": {
    "Name": { "type": "string" },
    "Age": { "type": "integer" },
    "Address": { "type": ["string", "null"], "default": null }
  },
  "required": ["Name", "Age"]
}
```

## Adding Descriptions to Fields

### Using Description Attributes

```csharp
using System.ComponentModel;
using System.Text.Json.Schema;

[Description("Represents a person in the system")]
public record Person(
    [property: Description("The person's full name")] string Name,
    [property: Description("The person's age in years")] int Age,
    [property: Description("The person's home address")] string? Address = null
);

// Configure schema generation to include descriptions
JsonSchemaExporterOptions exporterOptions = new()
{
    TransformSchemaNode = (context, schema) => {
        // Get attribute provider (type or property)
        ICustomAttributeProvider? attributeProvider = context.PropertyInfo is not null 
            ? context.PropertyInfo.AttributeProvider 
            : context.TypeInfo.Type;
        
        // Look for DescriptionAttribute
        DescriptionAttribute? descriptionAttr = attributeProvider?
            .GetCustomAttributes(inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault();
        
        // Add description to schema
        if (descriptionAttr != null)
        {
            if (schema is not JsonObject jObj)
            {
                // Handle boolean schema case
                JsonValueKind valueKind = schema.GetValueKind();
                schema = jObj = new JsonObject();
                if (valueKind is JsonValueKind.False)
                {
                    jObj.Add("not", true);
                }
            }
            jObj.Insert(0, "description", descriptionAttr.Description);
        }
        
        return schema;
    }
};

// Generate schema with descriptions
JsonNode schema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(typeof(Person), exporterOptions);
```

**Output:**
```json
{
  "description": "Represents a person in the system",
  "type": ["object", "null"],
  "properties": {
    "Name": { 
      "description": "The person's full name",
      "type": "string" 
    },
    "Age": { 
      "description": "The person's age in years",
      "type": "integer" 
    },
    "Address": { 
      "description": "The person's home address",
      "type": ["string", "null"], 
      "default": null 
    }
  },
  "required": ["Name", "Age"]
}
```

## Configuration Options

### JsonSchemaExporterOptions

```csharp
JsonSchemaExporterOptions exporterOptions = new()
{
    // Treat null-oblivious types as non-nullable
    TreatNullObliviousAsNonNullable = true,
    
    // Custom schema transformation
    TransformSchemaNode = (context, schema) => {
        // Your custom logic here
        return schema;
    }
};
```

### JsonSerializerOptions Integration

```csharp
JsonSerializerOptions options = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

JsonNode schema = options.GetJsonSchemaAsNode(typeof(Person), exporterOptions);
```

## Advanced Examples

### Complex Types with Validation Attributes

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

[Description("User account information")]
public class UserAccount
{
    [Description("Unique identifier for the user")]
    public Guid Id { get; set; }
    
    [Required]
    [EmailAddress]
    [Description("User's email address")]
    public string Email { get; set; } = string.Empty;
    
    [Range(18, 120)]
    [Description("User's age (must be between 18 and 120)")]
    public int Age { get; set; }
    
    [Description("User's profile settings")]
    public UserProfile? Profile { get; set; }
}

[Description("User profile configuration")]
public class UserProfile
{
    [Description("Display name for the user")]
    public string DisplayName { get; set; } = string.Empty;
    
    [Description("User's preferred theme")]
    public Theme Theme { get; set; } = Theme.Light;
}

[Description("Available UI themes")]
public enum Theme
{
    [Description("Light theme")]
    Light,
    
    [Description("Dark theme")]
    Dark,
    
    [Description("Auto theme based on system")]
    Auto
}
```

### Generic Types

```csharp
[Description("API response wrapper")]
public class ApiResponse<T>
{
    [Description("Indicates if the request was successful")]
    public bool Success { get; set; }
    
    [Description("Response data")]
    public T? Data { get; set; }
    
    [Description("Error message if request failed")]
    public string? ErrorMessage { get; set; }
}

// Generate schema for specific generic type
JsonNode schema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(
    typeof(ApiResponse<Person>), 
    exporterOptions
);
```

## Helper Extension Method

Create a reusable extension method for easy schema generation with descriptions:

```csharp
public static class JsonSchemaExtensions
{
    public static JsonNode GetJsonSchemaWithDescriptions(this JsonSerializerOptions options, Type type)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) =>
            {
                ICustomAttributeProvider? attributeProvider = context.PropertyInfo is not null 
                    ? context.PropertyInfo.AttributeProvider 
                    : context.TypeInfo.Type;
                
                var descriptionAttr = attributeProvider?
                    .GetCustomAttributes(inherit: true)
                    .OfType<DescriptionAttribute>()
                    .FirstOrDefault();
                
                if (descriptionAttr != null)
                {
                    if (schema is not JsonObject jObj)
                    {
                        JsonValueKind valueKind = schema.GetValueKind();
                        schema = jObj = new JsonObject();
                        if (valueKind is JsonValueKind.False)
                        {
                            jObj.Add("not", true);
                        }
                    }
                    jObj.Insert(0, "description", descriptionAttr.Description);
                }
                
                return schema;
            }
        };
        
        return options.GetJsonSchemaAsNode(type, exporterOptions);
    }
}

// Usage
JsonNode schema = JsonSerializerOptions.Default.GetJsonSchemaWithDescriptions(typeof(Person));
```

## Best Practices

1. **Use Description Attributes**: Always add `[Description("...")]` attributes to your types and properties for better documentation
2. **Consistent Naming**: Use consistent property naming policies that match your API standards
3. **Validation Attributes**: Leverage `System.ComponentModel.DataAnnotations` attributes for additional constraints
4. **Reusable Configuration**: Create reusable `JsonSchemaExporterOptions` configurations for consistent schema generation
5. **Null Handling**: Configure `TreatNullObliviousAsNonNullable` based on your nullability requirements

## Integration with ASP.NET Core

```csharp
// In your controller or minimal API
[HttpGet("schema")]
public IActionResult GetPersonSchema()
{
    var schema = JsonSerializerOptions.Default.GetJsonSchemaWithDescriptions(typeof(Person));
    return Ok(schema);
}
```

## Troubleshooting

### Common Issues

1. **Missing Descriptions**: Ensure you're using the `TransformSchemaNode` delegate to process `DescriptionAttribute`
2. **Null Reference Types**: Configure `TreatNullObliviousAsNonNullable` appropriately for your project's nullable context
3. **Complex Types**: For inheritance hierarchies, consider using `JsonDerivedType` attributes for proper schema generation

### Performance Considerations

- Schema generation uses reflection, so consider caching generated schemas in production
- Use source generation when possible for better performance in high-throughput scenarios

---

## Resources

- [Microsoft Documentation: JSON Schema Exporter](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema)
- [System.Text.Json in .NET 9 Blog Post](https://devblogs.microsoft.com/dotnet/system-text-json-in-dotnet-9/)
- [JSON Schema Specification](https://json-schema.org/)

---

*This guide covers the essential features of .NET 9+ JSON schema generation. For more advanced scenarios, refer to the official Microsoft documentation.*