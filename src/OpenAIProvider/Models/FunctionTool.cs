using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

/// <summary>
/// Represents a function tool for LLM tool calling
/// </summary>
public sealed record FunctionTool
{
    /// <summary>
    /// The type of tool, always "function" for function tools
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    /// <summary>
    /// The function definition for this tool
    /// </summary>
    [JsonPropertyName("function")]
    public FunctionDefinition Function { get; init; }

    /// <summary>
    /// Parameterless constructor for JSON deserialization
    /// </summary>
    public FunctionTool()
    {
        Function = new FunctionDefinition();
    }

    /// <summary>
    /// Creates a new function tool with the specified function definition
    /// </summary>
    /// <param name="definition">The function definition</param>
    public FunctionTool(FunctionDefinition definition)
    {
        Function = definition;
    }
}
