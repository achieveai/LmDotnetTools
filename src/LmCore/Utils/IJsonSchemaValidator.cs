namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Result of a schema validation attempt.
/// </summary>
public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>
/// Interface for validating JSON data against a schema.
/// </summary>
public interface IJsonSchemaValidator
{
    /// <summary>
    /// Validates the provided JSON string against the specified schema.
    /// </summary>
    /// <param name="json">The JSON string to validate.</param>
    /// <param name="schema">The schema object to validate against. Expected to be a FunctionContract or JsonSchemaObject.</param>
    /// <returns>True if the JSON validates against the schema; otherwise, false.</returns>
    bool Validate(string json, object schema);

    /// <summary>
    /// Validates the provided JSON string against the specified schema and returns detailed errors (if any).
    /// </summary>
    /// <param name="json">The JSON string to validate.</param>
    /// <param name="schema">The schema object to validate against. Expected to be a FunctionContract, JsonSchemaObject or JSON schema string.</param>
    /// <returns>Validation result containing the validity flag and any validation errors.</returns>
    SchemaValidationResult ValidateDetailed(string json, object schema);
}
