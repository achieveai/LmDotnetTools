namespace AchieveAi.LmDotnetTools.LmCore.Agents;

public class FunctionParameterContract
{
    /// <summary>
    /// The name of the parameter.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The description of the parameter.
    /// This will be extracted from the param section of the structured comment if available.
    /// Otherwise, the description will be null.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The type of the parameter.
    /// </summary>
    public Type? ParameterType { get; set; }

    /// <summary>
    /// If the parameter is a required parameter.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// The default value of the parameter.
    /// </summary>
    public object? DefaultValue { get; set; }
}