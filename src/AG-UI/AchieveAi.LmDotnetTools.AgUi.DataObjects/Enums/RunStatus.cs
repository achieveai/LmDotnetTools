using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

/// <summary>
/// Represents the completion status of an agent run
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<RunStatus>))]
public enum RunStatus
{
    /// <summary>
    /// Run completed successfully
    /// Serialized as: "success"
    /// </summary>
    Success,

    /// <summary>
    /// Run failed due to an error
    /// Serialized as: "failed"
    /// </summary>
    Failed,

    /// <summary>
    /// Run was cancelled by user or system
    /// Serialized as: "cancelled"
    /// </summary>
    Cancelled,
}
