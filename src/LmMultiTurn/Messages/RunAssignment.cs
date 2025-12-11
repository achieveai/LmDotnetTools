namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Assignment info returned when input is accepted by the multi-turn agent.
/// </summary>
/// <param name="RunId">The run ID assigned to this submission</param>
/// <param name="GenerationId">Server-assigned generation ID for all messages in this generation</param>
/// <param name="InputIds">Echoed back if client provided</param>
/// <param name="ParentRunId">The parent run ID if this was a fork</param>
/// <param name="WasInjected">Whether this was injected into an ongoing run</param>
public record RunAssignment(
    string RunId,
    string GenerationId,
    List<string>? InputIds = null,
    string? ParentRunId = null,
    bool WasInjected = false);
