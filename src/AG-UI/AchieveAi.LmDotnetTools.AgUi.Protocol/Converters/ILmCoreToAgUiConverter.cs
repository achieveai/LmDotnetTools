using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;

/// <summary>
/// Converts LmCore messages to AG-UI protocol format (outbound conversion).
/// Handles complete messages, not streaming updates.
/// </summary>
public interface ILmCoreToAgUiConverter
{
    /// <summary>
    /// Converts a single LmCore message to AG-UI message(s).
    /// May return multiple messages for CompositeMessage, ToolsCallResultMessage, or ToolsCallAggregateMessage.
    /// </summary>
    /// <param name="message">The LmCore message to convert</param>
    /// <returns>List of AG-UI messages (may be empty for unsupported types)</returns>
    /// <exception cref="InvalidOperationException">When GenerationId is null or empty</exception>
    /// <exception cref="ArgumentNullException">When message is null</exception>
    ImmutableList<DataObjects.DTOs.Message> ConvertMessage(IMessage message);

    /// <summary>
    /// Converts a collection of LmCore messages to AG-UI message history.
    /// Flattens CompositeMessages and handles all message types.
    /// </summary>
    /// <param name="messages">The LmCore messages to convert</param>
    /// <returns>Flattened list of AG-UI messages</returns>
    /// <exception cref="ArgumentNullException">When messages is null</exception>
    ImmutableList<DataObjects.DTOs.Message> ConvertMessageHistory(IEnumerable<IMessage> messages);

    /// <summary>
    /// Converts a LmCore ToolCall to AG-UI ToolCall.
    /// Parses JSON string arguments to JsonElement.
    /// </summary>
    /// <param name="toolCall">The LmCore tool call to convert</param>
    /// <returns>AG-UI tool call</returns>
    /// <exception cref="System.Text.Json.JsonException">When FunctionArgs is invalid JSON</exception>
    /// <exception cref="ArgumentException">When ToolCallId is null or empty</exception>
    DataObjects.DTOs.ToolCall ConvertToolCall(ToolCall toolCall);
}
