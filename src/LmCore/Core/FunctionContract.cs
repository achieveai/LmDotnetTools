namespace AchieveAi.LmDotnetTools.LmCore.Agents;

public class FunctionContract
{
    /// <summary>
    /// The namespace of the function.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// The class name of the function.
    /// </summary>
    public string? ClassName { get; set; }

    /// <summary>
    /// The name of the function.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The description of the function.
    /// If a structured comment is available, the description will be extracted from the summary section.
    /// Otherwise, the description will be null.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The parameters of the function.
    /// </summary>
    public IEnumerable<FunctionParameterContract>? Parameters { get; set; }

    /// <summary>
    /// The return type of the function.
    /// </summary>
    public Type? ReturnType { get; set; }

    /// <summary>
    /// The description of the return section.
    /// If a structured comment is available, the description will be extracted from the return section.
    /// Otherwise, the description will be null.
    /// </summary>
    public string? ReturnDescription { get; set; }
}
