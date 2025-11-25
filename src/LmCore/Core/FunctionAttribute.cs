using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Agents;

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public class FunctionAttribute : Attribute
{
    public FunctionAttribute(string? functionName = null, string? description = null)
    {
        FunctionName = functionName;
        Description = description;
    }

    [JsonPropertyName("function_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionName { get; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; }
}
