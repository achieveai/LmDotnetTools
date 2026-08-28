using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Input to send to the multi-turn agent.
/// </summary>
/// <param name="Messages">The messages to submit (user messages, possibly with images)</param>
/// <param name="InputId">Client-provided correlation ID (optional) - echoed back in assignment</param>
/// <param name="ParentRunId">Parent run ID to fork from. If null, continues from latest run</param>
/// <param name="SuppressSubAgentSpawning">
/// When true, the run that consumes this input must not be able to start NEW sub-agents: the spawn tool is
/// dropped from that run's contracts and its handler refuses, while reading from and following up with
/// already-running sub-agents stays available. Scoped to that one run and released afterwards, so the next
/// turn on the same thread regains the tool. Only agents declaring
/// <see cref="SubAgents.ISpawnSuppressingAgent"/> honour it — a caller that NEEDS the guarantee must refuse
/// any other agent rather than send the flag and hope.
/// </param>
public record UserInput(
    List<IMessage> Messages,
    string? InputId = null,
    string? ParentRunId = null,
    bool SuppressSubAgentSpawning = false
);
