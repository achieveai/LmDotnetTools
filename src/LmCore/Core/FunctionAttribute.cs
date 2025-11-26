using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Core;

[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public class FunctionAttribute : Attribute
{
    [JsonPropertyName("function_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionName { get; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; }

    public FunctionAttribute(string? functionName = null, string? description = null)
    {
        FunctionName = functionName;
        Description = description;
    }
}
