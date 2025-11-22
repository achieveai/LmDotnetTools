using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;

/// <summary>
/// Represents the role of a message participant in the conversation
/// </summary>
[JsonConverter(typeof(LowerCaseEnumConverter<MessageRole>))]
public enum MessageRole
{
    /// <summary>
    /// System-level message (e.g., instructions, context)
    /// Serialized as: "system"
    /// </summary>
    System,

    /// <summary>
    /// Message from the user/human
    /// Serialized as: "user"
    /// </summary>
    User,

    /// <summary>
    /// Message from the AI assistant
    /// Serialized as: "assistant"
    /// </summary>
    Assistant,

    /// <summary>
    /// Message from a tool/function execution
    /// Serialized as: "tool"
    /// </summary>
    Tool,
}
