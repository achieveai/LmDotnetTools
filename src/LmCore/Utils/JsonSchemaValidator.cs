using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Validates JSON data against a schema.
/// </summary>
public class JsonSchemaValidator : IJsonSchemaValidator
{
    /// <summary>
    /// Validates the provided JSON string against the specified schema.
    /// </summary>
    /// <param name="json">The JSON string to validate.</param>
    /// <param name="schema">The schema object to validate against. Expected to be a FunctionContract or JsonSchemaObject.</param>
    /// <returns>True if the JSON validates against the schema; otherwise, false.</returns>
    public bool Validate(string json, object schema)
    {
        if (string.IsNullOrEmpty(json) || schema == null)
            return false;

        try
        {
            using var jsonDoc = JsonDocument.Parse(json);
            var rootElement = jsonDoc.RootElement;

            if (schema is FunctionContract functionContract)
            {
                return ValidateAgainstFunctionContract(rootElement, functionContract);
            }
            else if (schema is JsonSchemaObject jsonSchema)
            {
                return ValidateAgainstSchema(rootElement, jsonSchema);
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool ValidateAgainstFunctionContract(JsonElement element, FunctionContract contract)
    {
        if (contract.Parameters == null || !contract.Parameters.Any())
            return true;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var param in contract.Parameters)
        {
            if (param.IsRequired && !element.TryGetProperty(param.Name, out var propElement))
                return false;

            if (element.TryGetProperty(param.Name, out var propertyElement) && param.ParameterType != null)
            {
                if (!ValidateAgainstSchema(propertyElement, param.ParameterType))
                    return false;
            }
        }

        return true;
    }

    private bool ValidateAgainstSchema(JsonElement element, JsonSchemaObject schema)
    {
        if (schema == null)
            return true;

        switch (schema.Type.GetTypeString())
        {
            case "string":
                if (element.ValueKind != JsonValueKind.String)
                    return false;
                if (schema.Enum != null && schema.Enum.Count > 0)
                    return schema.Enum.Contains(element.GetString());
                return true;
            case "integer":
            case "number":
                if (element.ValueKind != JsonValueKind.Number)
                    return false;
                if (schema.Minimum.HasValue && element.GetDouble() < schema.Minimum.Value)
                    return false;
                if (schema.Maximum.HasValue && element.GetDouble() > schema.Maximum.Value)
                    return false;
                return true;
            case "boolean":
                return element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False;
            case "array":
                if (element.ValueKind != JsonValueKind.Array)
                    return false;
                var arrayElements = element.EnumerateArray().ToList();
                if (schema.MinItems.HasValue && arrayElements.Count < schema.MinItems.Value)
                    return false;
                if (schema.MaxItems.HasValue && arrayElements.Count > schema.MaxItems.Value)
                    return false;
                if (schema.UniqueItems)
                {
                    var elementStrings = arrayElements.Select(e => e.ToString()).ToList();
                    if (elementStrings.Distinct().Count() != elementStrings.Count)
                        return false;
                }
                if (schema.Items == null)
                    return true;
                return arrayElements.All(item => ValidateAgainstSchema(item, schema.Items));
            case "object":
                if (element.ValueKind != JsonValueKind.Object)
                    return false;
                if (schema.Properties == null || schema.Properties.Count == 0)
                    return true;
                foreach (var prop in schema.Properties)
                {
                    if (schema.Required != null && schema.Required.Contains(prop.Key) && !element.TryGetProperty(prop.Key, out _))
                        return false;
                    if (element.TryGetProperty(prop.Key, out var propElement))
                    {
                        if (!ValidateAgainstProperty(propElement, prop.Value))
                            return false;
                    }
                }
                return true;
            default:
                return false;
        }
    }

    private bool ValidateAgainstProperty(JsonElement element, JsonSchemaProperty property)
    {
        if (property == null)
            return true;

        switch (property.Type.GetTypeString())
        {
            case "string":
                if (element.ValueKind != JsonValueKind.String)
                    return false;
                if (property.Enum != null && property.Enum.Count > 0)
                    return property.Enum.Contains(element.GetString());
                return true;
            case "integer":
            case "number":
                if (element.ValueKind != JsonValueKind.Number)
                    return false;
                if (property.Minimum.HasValue && element.GetDouble() < property.Minimum.Value)
                    return false;
                if (property.Maximum.HasValue && element.GetDouble() > property.Maximum.Value)
                    return false;
                return true;
            case "boolean":
                return element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False;
            case "array":
                if (element.ValueKind != JsonValueKind.Array)
                    return false;
                var arrayElements = element.EnumerateArray().ToList();
                if (property.MinItems.HasValue && arrayElements.Count < property.MinItems.Value)
                    return false;
                if (property.MaxItems.HasValue && arrayElements.Count > property.MaxItems.Value)
                    return false;
                if (property.UniqueItems)
                {
                    var elementStrings = arrayElements.Select(e => e.ToString()).ToList();
                    if (elementStrings.Distinct().Count() != elementStrings.Count)
                        return false;
                }
                if (property.Items == null)
                    return true;
                return arrayElements.All(item => ValidateAgainstSchema(item, property.Items));
            default:
                return false;
        }
    }
}
