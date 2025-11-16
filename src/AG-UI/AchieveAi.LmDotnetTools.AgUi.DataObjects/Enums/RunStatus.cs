using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

/// <summary>
/// Represents the completion status of an agent run
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    /// <summary>
    /// Run completed successfully
    /// </summary>
    Success,

    /// <summary>
    /// Run failed due to an error
    /// </summary>
    Failed,

    /// <summary>
    /// Run was cancelled by user or system
    /// </summary>
    Cancelled
}
