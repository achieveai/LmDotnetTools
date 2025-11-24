using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using Json.Schema;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Validates JSON data against a schema.
/// </summary>
public class JsonSchemaValidator : IJsonSchemaValidator
{
    // JsonSerializerOptions with Union converter for proper serialization
    public static readonly JsonSerializerOptions SchemaSerializationOptions = new()
    {
        Converters = { new UnionJsonConverter<string, IReadOnlyList<string>>() },
    };

    // --- Implementation using JsonSchema.Net (simplified) -------------------------

    /// <inheritdoc />
    public bool Validate(string json, object schema)
    {
        return ValidateDetailed(json, schema).IsValid;
    }

    /// <inheritdoc />
    public SchemaValidationResult ValidateDetailed(string json, object schema)
    {
        // Basic checks
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SchemaValidationResult(false, ["Input json is null or empty"]);
        }

        if (schema is null)
        {
            return new SchemaValidationResult(false, ["Schema is null"]);
        }

        JsonNode? dataNode;
        try
        {
            dataNode = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return new SchemaValidationResult(false, ["Invalid JSON payload"]);
        }

        Json.Schema.JsonSchema jsonSchema;

        try
        {
            jsonSchema = schema switch
            {
                string schemaText => JsonSchema.FromText(schemaText),
                Models.JsonSchemaObject schemaObj => JsonSchema.FromText(
                    JsonSerializer.Serialize(schemaObj, SchemaSerializationOptions)
                ),
                FunctionContract funcContract => BuildSchemaFromFunctionContract(funcContract),
                _ => throw new InvalidOperationException($"Unsupported schema type: {schema.GetType().Name}"),
            };
        }
        catch (Exception ex)
        {
            return new SchemaValidationResult(false, [$"Failed to parse schema: {ex.Message}"]);
        }

        Console.WriteLine($"[DEBUG] Validating JSON: {json}");

        try
        {
            var evaluationOptions = new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical };

            var result = jsonSchema.Evaluate(dataNode, evaluationOptions);
            var isValid = result.IsValid;
            var errors = ExtractValidationErrors(result);

            Console.WriteLine($"[DEBUG] Validation result: IsValid={isValid}, HasErrors={errors.Count > 0}");

            return new SchemaValidationResult(isValid, errors);
        }
        catch (Exception ex)
        {
            return new SchemaValidationResult(false, [$"Validation error: {ex.Message}"]);
        }
    }

    private static Json.Schema.JsonSchema BuildSchemaFromFunctionContract(FunctionContract contract)
    {
        var root = new JsonObject { ["type"] = "object" };

        var properties = new JsonObject();
        var required = new JsonArray();

        if (contract.Parameters is not null)
        {
            foreach (var param in contract.Parameters)
            {
                // Create a simple schema node with just the type
                var paramSchemaNode = JsonSerializer.SerializeToNode(param.ParameterType, SchemaSerializationOptions);
                properties[param.Name] = paramSchemaNode;

                if (param.IsRequired)
                {
                    required.Add(param.Name);
                }
            }
        }

        if (properties.Count > 0)
        {
            root["properties"] = properties;
        }

        if (required.Count > 0)
        {
            root["required"] = required;
        }

        var schemaText = root.ToJsonString();
        Console.WriteLine($"[DEBUG] Generated schema text: {schemaText}");
        return JsonSchema.FromText(schemaText);
    }

    private static List<string> ExtractValidationErrors(EvaluationResults result)
    {
        var errors = new List<string>();

        if (result.HasErrors)
        {
            // Extract error details from the evaluation result
            foreach (var detail in result.Details)
            {
                if (detail.HasErrors)
                {
                    var errorMessage = !string.IsNullOrEmpty(detail.Errors?["error"]?.ToString())
                        ? detail.Errors["error"].ToString()
                        : $"Validation failed at '{detail.InstanceLocation}'";
                    errors.Add(errorMessage);
                }
            }
        }

        return errors;
    }
}
