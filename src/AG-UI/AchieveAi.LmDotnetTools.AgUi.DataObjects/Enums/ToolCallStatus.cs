using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

/// <summary>
/// Represents the execution status of a tool call
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<ToolCallStatus>))]
public enum ToolCallStatus
{
    /// <summary>
    /// Tool call has been initiated but not yet started execution
    /// Serialized as: "pending"
    /// </summary>
    Pending,

    /// <summary>
    /// Tool call is currently executing
    /// Serialized as: "executing"
    /// </summary>
    Executing,

    /// <summary>
    /// Tool call completed successfully
    /// Serialized as: "completed"
    /// </summary>
    Completed,

    /// <summary>
    /// Tool call failed with an error
    /// Serialized as: "failed"
    /// </summary>
    Failed
}
