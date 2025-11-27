using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
///     Extension methods for converting FunctionContract to Markdown format.
/// </summary>
public static class FunctionContractMarkdownExtensions
{
    /// <summary>
    ///     Converts a FunctionContract to Markdown format.
    /// </summary>
    /// <param name="function">The function contract to convert.</param>
    /// <returns>A string containing the markdown representation of the function contract.</returns>
    public static string ToMarkdown(this FunctionContract function)
    {
        ArgumentNullException.ThrowIfNull(function);

        var markdown = new StringBuilder();

        // Create the heading with the function name
        _ = markdown.AppendLine($"## {function.Name}");

        // Add the description
        if (!string.IsNullOrEmpty(function.Description))
        {
            _ = markdown.AppendLine($"Description: {function.Description}");
            _ = markdown.AppendLine();
        }

        // Add parameters section if there are any parameters
        if (function.Parameters != null && function.Parameters.Any())
        {
            _ = markdown.AppendLine("Parameters:");

            foreach (var parameter in function.Parameters)
            {
                // Determine if the parameter is required or optional
                var requiredStatus = parameter.IsRequired ? "required" : "optional";

                // Add the parameter name and description
                _ = markdown.AppendLine($"- {parameter.Name} ({requiredStatus}): {parameter.Description}");

                // If parameter has a complex schema, include detailed information
                if (parameter.ParameterType != null)
                {
                    _ = markdown.AppendLine("  Schema Details:");
                    _ = markdown.AppendLine($"  - Type: {parameter.ParameterType.Type}");

                    // Add description from schema if available
                    if (!string.IsNullOrEmpty(parameter.ParameterType.Description))
                    {
                        _ = markdown.AppendLine($"  - Description: {parameter.ParameterType.Description}");
                    }

                    // Add enum values if present
                    if (parameter.ParameterType.Enum != null && parameter.ParameterType.Enum.Count > 0)
                    {
                        _ = markdown.AppendLine(
                            $"  - Allowed Values (Enum): {string.Join(", ", parameter.ParameterType.Enum)}"
                        );
                    }

                    // Add range constraints for numbers
                    if (parameter.ParameterType.Minimum.HasValue)
                    {
                        _ = markdown.AppendLine($"  - Minimum: {parameter.ParameterType.Minimum.Value}");
                    }

                    if (parameter.ParameterType.Maximum.HasValue)
                    {
                        _ = markdown.AppendLine($"  - Maximum: {parameter.ParameterType.Maximum.Value}");
                    }

                    // Add array constraints
                    if (parameter.ParameterType.Type.Contains("array"))
                    {
                        if (parameter.ParameterType.MinItems.HasValue)
                        {
                            _ = markdown.AppendLine($"  - Minimum Items: {parameter.ParameterType.MinItems.Value}");
                        }

                        if (parameter.ParameterType.MaxItems.HasValue)
                        {
                            _ = markdown.AppendLine($"  - Maximum Items: {parameter.ParameterType.MaxItems.Value}");
                        }

                        if (parameter.ParameterType.UniqueItems)
                        {
                            _ = markdown.AppendLine("  - Unique Items: Yes");
                        }

                        // Add information about array item type if available
                        if (parameter.ParameterType.Items != null)
                        {
                            _ = markdown.AppendLine($"  - Item Type: {parameter.ParameterType.Items.Type}");
                            if (!string.IsNullOrEmpty(parameter.ParameterType.Items.Description))
                            {
                                _ = markdown.AppendLine(
                                    $"    - Item Description: {parameter.ParameterType.Items.Description}"
                                );
                            }
                        }
                    }

                    // If it's an object with properties, list them
                    if (parameter.ParameterType.Properties != null && parameter.ParameterType.Properties.Count > 0)
                    {
                        _ = markdown.AppendLine("  - Properties:");
                        foreach (var prop in parameter.ParameterType.Properties)
                        {
                            _ = markdown.AppendLine($"    - {prop.Key}: Type={prop.Value.Type}");
                            if (!string.IsNullOrEmpty(prop.Value.Description))
                            {
                                _ = markdown.AppendLine($"      - Description: {prop.Value.Description}");
                            }

                            if (prop.Value.Enum != null && prop.Value.Enum.Count > 0)
                            {
                                _ = markdown.AppendLine(
                                    $"      - Allowed Values (Enum): {string.Join(", ", prop.Value.Enum)}"
                                );
                            }

                            if (prop.Value.Minimum.HasValue)
                            {
                                _ = markdown.AppendLine($"      - Minimum: {prop.Value.Minimum.Value}");
                            }

                            if (prop.Value.Maximum.HasValue)
                            {
                                _ = markdown.AppendLine($"      - Maximum: {prop.Value.Maximum.Value}");
                            }

                            if (prop.Value.Type.Contains("array"))
                            {
                                if (prop.Value.MinItems.HasValue)
                                {
                                    _ = markdown.AppendLine($"      - Minimum Items: {prop.Value.MinItems.Value}");
                                }

                                if (prop.Value.MaxItems.HasValue)
                                {
                                    _ = markdown.AppendLine($"      - Maximum Items: {prop.Value.MaxItems.Value}");
                                }

                                if (prop.Value.UniqueItems)
                                {
                                    _ = markdown.AppendLine("      - Unique Items: Yes");
                                }
                            }
                        }
                    }
                }
            }

            _ = markdown.AppendLine();
        }

        // Add return information if available
        if (function.ReturnType != null || !string.IsNullOrEmpty(function.ReturnDescription))
        {
            _ = markdown.AppendLine("Returns:");
            if (function.ReturnType != null)
            {
                _ = markdown.AppendLine($"- Type: {function.ReturnType.Name}");
            }

            if (!string.IsNullOrEmpty(function.ReturnDescription))
            {
                _ = markdown.AppendLine($"- Description: {function.ReturnDescription}");
            }

            _ = markdown.AppendLine();
        }

        // Add example section
        _ = markdown.AppendLine("Example:");
        _ = markdown.AppendLine();
        _ = markdown.AppendLine($"<tool_call name=\"{function.Name}\">");
        _ = markdown.AppendLine("```json");

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
        var exampleJson = JsonSerializer.Serialize(exampleObject, new JsonSerializerOptions { WriteIndented = true });

        _ = markdown.AppendLine(exampleJson);
        _ = markdown.AppendLine("```");
        _ = markdown.AppendLine("</tool_call>");

        return markdown.ToString();
    }

    /// <summary>
    ///     Creates a simple example value based on the parameter type.
    /// </summary>
    /// <param name="parameter">The function parameter contract.</param>
    /// <returns>A sample value for the parameter.</returns>
    private static object CreateExampleValue(FunctionParameterContract parameter)
    {
        return parameter == null || parameter.ParameterType == null
            ? "value"
            : CreateExampleValueFromSchema(parameter.ParameterType);
    }

    /// <summary>
    ///     Creates a sample value based on the JSON schema.
    /// </summary>
    /// <param name="schema">The JSON schema object.</param>
    /// <returns>A sample value for the schema.</returns>
    private static object CreateExampleValueFromSchema(JsonSchemaObject schema)
    {
        if (schema == null)
        {
            return "value";
        }

        switch (schema.Type.GetTypeString())
        {
            case "string":
                if (schema.Enum != null && schema.Enum.Count > 0)
                {
                    return schema.Enum[0]; // Return first enum value as example
                }

                return "value";
            case "integer":
            case "number":
                if (schema.Minimum.HasValue)
                {
                    return schema.Minimum.Value;
                }

                return 42;
            case "boolean":
                return true;
            case "array":
                // Create an array with items respecting MinItems if possible
                if (schema.Items != null)
                {
                    var itemCount = schema.MinItems.HasValue ? Math.Max(1, schema.MinItems.Value) : 1;
                    if (schema.MaxItems.HasValue && itemCount > schema.MaxItems.Value)
                    {
                        itemCount = schema.MaxItems.Value;
                    }

                    var items = new List<object>();
                    for (var i = 0; i < itemCount; i++)
                    {
                        items.Add(CreateExampleValueFromSchema(schema.Items));
                    }

                    return items.ToArray();
                }

                return Array.Empty<object>();
            case "object":
                // Create an object with sample properties
                var result = new Dictionary<string, object>();

                if (schema.Properties != null)
                {
                    foreach (var property in schema.Properties)
                    {
                        // For each property in the schema, create a sample value
                        result[property.Key] = CreateExampleValueFromProperty(property.Value);
                    }
                }

                return result;
            default:
                return "value";
        }
    }

    /// <summary>
    ///     Creates a sample value based on the JSON schema object.
    /// </summary>
    /// <param name="schemaObject">The JSON schema object.</param>
    /// <returns>A sample value for the schema object.</returns>
    private static object CreateExampleValueFromProperty(JsonSchemaObject schemaObject)
    {
        if (schemaObject == null)
        {
            return "value";
        }

        switch (schemaObject.Type.GetTypeString())
        {
            case "string":
                if (schemaObject.Enum?.Count > 0)
                {
                    return schemaObject.Enum[0]; // Return first enum value as example
                }

                return "value";
            case "integer":
            case "number":
                if (schemaObject.Minimum.HasValue)
                {
                    return (int)schemaObject.Minimum.Value;
                }

                return 42;
            case "boolean":
                return true;
            case "array":
                // Create an array with items respecting MinItems if possible
                if (schemaObject.Items != null)
                {
                    var itemCount = schemaObject.MinItems.HasValue ? Math.Max(1, schemaObject.MinItems.Value) : 1;
                    if (schemaObject.MaxItems.HasValue && itemCount > schemaObject.MaxItems.Value)
                    {
                        itemCount = schemaObject.MaxItems.Value;
                    }

                    var items = new List<object>();
                    for (var i = 0; i < itemCount; i++)
                    {
                        items.Add(CreateExampleValueFromSchema(schemaObject.Items));
                    }

                    return items.ToArray();
                }

                return Array.Empty<object>();
            default:
                return "value";
        }
    }
}
