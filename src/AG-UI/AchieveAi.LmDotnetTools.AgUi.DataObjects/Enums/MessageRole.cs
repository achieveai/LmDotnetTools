using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

/// <summary>
/// Represents the role of a message participant in the conversation
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageRole
{
    /// <summary>
    /// System-level message (e.g., instructions, context)
    /// </summary>
    System,

    /// <summary>
    /// Message from the user/human
    /// </summary>
    User,

    /// <summary>
    /// Message from the AI assistant
    /// </summary>
    Assistant,

    /// <summary>
    /// Message from a tool/function execution
    /// </summary>
    Tool
}
