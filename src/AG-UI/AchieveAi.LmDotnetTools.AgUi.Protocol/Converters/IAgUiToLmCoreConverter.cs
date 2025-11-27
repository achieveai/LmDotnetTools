using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.DTOs;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using ToolCall = AchieveAi.LmDotnetTools.LmCore.Messages.ToolCall;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;

/// <summary>
///     Converts AG-UI protocol messages to LmCore format (inbound conversion)
/// </summary>
public interface IAgUiToLmCoreConverter
{
    /// <summary>
    ///     Converts a single AG-UI message to LmCore message.
    ///     Returns appropriate message type based on AG-UI message properties.
    /// </summary>
    /// <param name="message">The AG-UI message to convert</param>
    /// <returns>LmCore message (TextMessage, ToolsCallMessage, or ToolsCallResultMessage)</returns>
    /// <exception cref="ArgumentNullException">When message is null</exception>
    /// <exception cref="ArgumentException">When message has invalid structure</exception>
    IMessage ConvertMessage(Message message);

    /// <summary>
    ///     Converts AG-UI message history to LmCore messages.
    ///     Groups consecutive tool result messages into ToolsCallResultMessage.
    /// </summary>
    /// <param name="messages">The AG-UI messages to convert</param>
    /// <returns>List of LmCore messages</returns>
    /// <exception cref="ArgumentNullException">When messages is null</exception>
    ImmutableList<IMessage> ConvertMessageHistory(ImmutableList<Message> messages);

    /// <summary>
    ///     Converts AG-UI ToolCall to LmCore ToolCall.
    ///     Serializes JsonElement arguments to JSON string.
    /// </summary>
    /// <param name="toolCall">The AG-UI tool call to convert</param>
    /// <returns>LmCore tool call</returns>
    /// <exception cref="ArgumentNullException">When toolCall is null</exception>
    ToolCall ConvertToolCall(DataObjects.DTOs.ToolCall toolCall);

    /// <summary>
    ///     Converts RunAgentInput to LmCore agent invocation parameters.
    ///     Merges history with new user message, applies context to metadata,
    ///     and maps configuration to GenerateReplyOptions.
    /// </summary>
    /// <param name="input">The RunAgentInput to convert</param>
    /// <param name="availableFunctions">Optional list of available functions for tool filtering</param>
    /// <returns>Tuple of (messages for agent, generation options)</returns>
    /// <exception cref="ArgumentNullException">When input is null</exception>
    (IEnumerable<IMessage> messages, GenerateReplyOptions options) ConvertRunAgentInput(
        RunAgentInput input,
        IEnumerable<FunctionContract>? availableFunctions = null
    );
}
