using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds the live <see cref="IMultiTurnAgent"/> loop a daemon agent (Review / Judge / Knowledge / B
/// variant) drives for one collect-only run. This is the single seam between the declarative
/// <see cref="AgentProfile"/> (built by <see cref="DaemonAgentFactory"/>) and the concrete provider
/// loop, so the stage executor's agent logic stays verifiable against a fake while the real provider
/// wiring lives in <see cref="LiveReviewAgentLoopFactory"/>.
/// </summary>
internal interface IReviewAgentLoopFactory
{
    /// <summary>
    /// Creates a fresh agent loop for <paramref name="profile"/> on its own conversation
    /// <paramref name="threadId"/>, overriding the model with <paramref name="modelId"/> when supplied.
    /// <paramref name="reasoningEffort"/> sets the adaptive-thinking effort (<c>output_config.effort</c>)
    /// for this loop; <c>null</c> uses the daemon's configured default and an empty value omits it
    /// entirely (required for non-adaptive models — e.g. Copilot's haiku rejects an effort it does not
    /// support). <paramref name="toolContext"/> is <c>null</c> on the diff-only path (today's behavior:
    /// empty tool registry, no sub-agents); when supplied it connects the gateway MCP client filtered to
    /// its read-only allow-list and attaches any configured sub-agents. The caller owns the returned
    /// loop's lifetime (it is <see cref="IAsyncDisposable"/>).
    /// </summary>
    IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null);
}
