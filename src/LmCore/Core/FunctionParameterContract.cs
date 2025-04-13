namespace AchieveAi.LmDotnetTools.LmCore.Agents;

/// <summary>
/// Represents a contract for a function parameter, defining its name, type, and other metadata.
/// </summary>
public record FunctionParameterContract
{
    /// <summary>
    /// The name of the parameter. This is required and cannot be null.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The description of the parameter.
    /// This will be extracted from the param section of the structured comment if available.
    /// Otherwise, the description will be null.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The type of the parameter. This is required and cannot be null.
    /// </summary>
    public required Type ParameterType { get; init; }

    /// <summary>
    /// If the parameter is a required parameter.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// The default value of the parameter.
    /// </summary>
    public object? DefaultValue { get; init; }
}