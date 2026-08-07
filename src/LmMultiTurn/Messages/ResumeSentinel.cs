namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Internal sentinel placed onto the input channel by <c>MultiTurnAgentLoop</c> when all
/// deferred tool calls from a previous assistant turn have been resolved. Triggers the
/// loop to start a new run with no fresh user input — the resolved tool_results already
/// in history feed the LLM the data it was waiting for.
/// </summary>
/// <param name="ResumeForRunId">The run that ended with deferred tool calls; recorded for telemetry only.</param>
/// <param name="ResumeForGenerationId">The assistant generation whose deferrals have all been resolved.</param>
public sealed record ResumeSentinel(string ResumeForRunId, string ResumeForGenerationId)
{
    /// <summary>
    /// Set when this resume was caused by a recoverable provider-stream interruption rather than by
    /// deferred tool calls resolving. Recovery reuses this sentinel deliberately: both cases continue
    /// an existing conversation with no new user input, and routing them through one carrier keeps a
    /// single answer to "why is the loop taking another turn without the user asking for one".
    /// Internal because its payload is loop-private; the sentinel itself stays public because
    /// deferral resolution constructs it from outside.
    /// </summary>
    internal InterruptedTurnResume? InterruptedTurn { get; init; }
}
