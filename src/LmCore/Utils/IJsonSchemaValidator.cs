namespace AchieveAi.LmDotnetTools.LmCore.Utils;

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
}
