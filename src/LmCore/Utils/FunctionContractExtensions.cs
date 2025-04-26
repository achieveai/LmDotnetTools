using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Extension methods for converting FunctionContract to Markdown format.
/// </summary>
public static class FunctionContractMarkdownExtensions
{
    /// <summary>
    /// Converts a FunctionContract to Markdown format.
    /// </summary>
    /// <param name="function">The function contract to convert.</param>
    /// <returns>A string containing the markdown representation of the function contract.</returns>
    public static string ToMarkdown(this FunctionContract function)
    {
        if (function == null)
            throw new ArgumentNullException(nameof(function));

        var markdown = new StringBuilder();

        // Create the heading with the function name
        markdown.AppendLine($"## {function.Name}");
        
        // Add the description
        if (!string.IsNullOrEmpty(function.Description))
        {
            markdown.AppendLine($"Description: {function.Description}");
            markdown.AppendLine();
        }

        // Add parameters section if there are any parameters
        if (function.Parameters != null && function.Parameters.Any())
        {
            markdown.AppendLine("Parameters:");
            
            foreach (var parameter in function.Parameters)
            {
                // Determine if the parameter is required or optional
                var requiredStatus = parameter.IsRequired ? "required" : "optional";
                
                // Add the parameter name and description
                markdown.AppendLine($"- {parameter.Name} ({requiredStatus}): {parameter.Description}");
                
                // If parameter has a complex schema (not a simple type), include the schema
                if (parameter.ParameterType != null && 
                    (parameter.ParameterType.Properties != null || 
                     parameter.ParameterType.Items != null ||
                     !string.IsNullOrEmpty(parameter.ParameterType.Description)))
                {
                    markdown.AppendLine("  Schema:");
                    markdown.AppendLine("  ```json");
                    
                    // Serialize the schema to JSON
                    var schemaJson = JsonSerializer.Serialize(parameter.ParameterType, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    
                    // Add the indented schema JSON
                    markdown.AppendLine("  " + schemaJson.Replace("\n", "\n  "));
                    
                    markdown.AppendLine("  ```");
                }
            }
            
            markdown.AppendLine();
        }
        
        // Add return information if available
        if (function.ReturnType != null || !string.IsNullOrEmpty(function.ReturnDescription))
        {
            markdown.AppendLine("Returns:");
            if (function.ReturnType != null)
            {
                markdown.AppendLine($"- Type: {function.ReturnType.Name}");
            }
            if (!string.IsNullOrEmpty(function.ReturnDescription))
            {
                markdown.AppendLine($"- Description: {function.ReturnDescription}");
            }
            markdown.AppendLine();
        }
        
        // Add example section
        markdown.AppendLine("Example:");
        markdown.AppendLine();
        markdown.AppendLine($"<{function.Name}>");
        markdown.AppendLine("```json");
        
        // Create a simple example object with the parameters
        var exampleObject = new Dictionary<string, object>();
        if (function.Parameters != null)
        {
            foreach (var parameter in function.Parameters)
            {
                // Create a simple example value based on parameter type
                exampleObject[parameter.Name] = CreateExampleValue(parameter);
            }
        }
        
        // Serialize the example object to JSON
        var exampleJson = JsonSerializer.Serialize(exampleObject, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        
        markdown.AppendLine(exampleJson);
        markdown.AppendLine("```");
        markdown.AppendLine($"</{function.Name}>");
        
        return markdown.ToString();
    }
      /// <summary>
    /// Creates a simple example value based on the parameter type.
    /// </summary>
    /// <param name="parameter">The function parameter contract.</param>
    /// <returns>A sample value for the parameter.</returns>
    private static object CreateExampleValue(FunctionParameterContract parameter)
    {
        if (parameter == null || parameter.ParameterType == null)
            return "value";
            
        return CreateExampleValueFromSchema(parameter.ParameterType);
    }
    
    /// <summary>
    /// Creates a sample value based on the JSON schema.
    /// </summary>
    /// <param name="schema">The JSON schema object.</param>
    /// <returns>A sample value for the schema.</returns>
    private static object CreateExampleValueFromSchema(JsonSchemaObject schema)
    {
        if (schema == null)
            return "value";
            
        switch (schema.Type.ToLower())
        {
            case "string":
                return "value";
            case "integer":
            case "number":
                return 42;
            case "boolean":
                return true;
            case "array":
                // Create an array with one sample item if we have an item schema
                if (schema.Items != null)
                {
                    return new[] { CreateExampleValueFromSchema(schema.Items) };
                }
                return new object[] { };
            case "object":
                // Create an object with sample properties
                var result = new Dictionary<string, object>();
                
                if (schema.Properties != null)
                {                    foreach (var property in schema.Properties)
                    {
                        // For each property in the schema, create a sample value
                        result[property.Key] = CreateExampleValueFromProperty(property.Value);
                    }
                }
                return result;            default:
                return "value";
        }
    }
    
    /// <summary>
    /// Creates a sample value based on the JSON property schema.
    /// </summary>
    /// <param name="property">The JSON schema property.</param>
    /// <returns>A sample value for the property.</returns>
    private static object CreateExampleValueFromProperty(JsonSchemaProperty property)
    {
        if (property == null)
            return "value";
            
        switch (property.Type.ToLower())
        {
            case "string":
                if (property.Enum != null && property.Enum.Count > 0)
                {
                    return property.Enum[0]; // Return first enum value as example
                }
                return "value";
            case "integer":
            case "number":
                if (property.Minimum.HasValue)
                {
                    return (int)property.Minimum.Value;
                }
                return 42;
            case "boolean":
                return true;
            case "array":
                // Create an array with one sample item if we have an item schema
                if (property.Items != null)
                {
                    return new[] { CreateExampleValueFromSchema(property.Items) };
                }
                return new object[] { };
            default:
                return "value";
        }
    }
}
