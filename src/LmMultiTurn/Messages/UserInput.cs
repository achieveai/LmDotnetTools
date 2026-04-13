using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Input to send to the multi-turn agent.
/// </summary>
/// <param name="Messages">The messages to submit (user messages, possibly with images)</param>
/// <param name="InputId">Client-provided correlation ID (optional) - echoed back in assignment</param>
/// <param name="ParentRunId">Parent run ID to fork from. If null, continues from latest run</param>
public record UserInput(
    List<IMessage> Messages,
    string? InputId = null,
    string? ParentRunId = null);
