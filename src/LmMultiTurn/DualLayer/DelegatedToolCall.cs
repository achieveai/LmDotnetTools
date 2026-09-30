namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// One planner (layer 1) tool call on its way to the executor (layer 2): the tool the planner
/// asked for, its arguments with the rationale removed, and the rationale itself.
/// </summary>
/// <param name="ToolName">The tool name as the executor sees it.</param>
/// <param name="ArgumentsJson">The planner's arguments, minus the rationale, as a JSON object.</param>
/// <param name="Rationale">Why the planner wants this call and what it needs back.</param>
/// <param name="ToolCallId">The planner's tool_call_id, when the call path carries one.</param>
public sealed record DelegatedToolCall(string ToolName, string ArgumentsJson, string Rationale, string? ToolCallId);

/// <summary>
/// Carries out <see cref="DelegatedToolCall"/>s for the planner. The production implementation is
/// <see cref="AgentDelegatedToolExecutor"/>, which hands each call to a cheaper agent that owns
/// the real tools.
/// </summary>
public interface IDelegatedToolExecutor
{
    /// <summary>
    /// Carries out the call and returns what the planner should read as the tool result.
    /// Throws when the executor could not produce an answer.
    /// </summary>
    Task<string> ExecuteAsync(DelegatedToolCall call, CancellationToken cancellationToken);
}
