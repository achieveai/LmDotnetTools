using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

/// <summary>
/// Represents the execution status of a tool call
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolCallStatus
{
    /// <summary>
    /// Tool call has been initiated but not yet started execution
    /// </summary>
    Pending,

    /// <summary>
    /// Tool call is currently executing
    /// </summary>
    Executing,

    /// <summary>
    /// Tool call completed successfully
    /// </summary>
    Completed,

    /// <summary>
    /// Tool call failed with an error
    /// </summary>
    Failed
}
