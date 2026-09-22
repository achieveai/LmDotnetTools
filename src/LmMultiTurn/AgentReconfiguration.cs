using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Everything a host must re-supply for a live agent to serve the NEXT turn under a new mode or a new
/// model. It is the mode/provider-dependent half of what the agent's constructor took; everything the
/// conversation owns — history, sub-agents, armed Waits, the input queue, subscribers, the usage ledger
/// — is deliberately absent because a reconfiguration keeps it.
/// </summary>
/// <param name="ProviderAgent">
///     The bare provider agent for the new model. The agent rebuilds its own middleware stack over it;
///     it must NOT arrive wrapped.
/// </param>
/// <param name="FunctionRegistry">
///     The host tools for the new mode ONLY. The agent re-adds its own built-ins (client tools,
///     sub-agent tools, Wait, RecallConversation) to this registry exactly as its constructor did, so a
///     host neither has to know that list nor keep it in step.
/// </param>
/// <param name="SystemPrompt">
///     The raw host prompt for the new mode. The agent re-applies the identity preamble and the
///     compaction note, so the composed prompt is built the one way it is built at construction.
/// </param>
/// <param name="DefaultOptions">The new per-turn options template: model id, budget, caching, reasoning.</param>
/// <param name="IncludeAskUserQuestionTool">Whether the new mode exposes <c>AskUserQuestion</c>.</param>
/// <param name="IncludeNotifyClientTool">Whether the new mode exposes <c>NotifyClient</c>.</param>
/// <param name="SubAgentOptions">
///     The new mode's sub-agent configuration, or null to hide the sub-agent tools. Null leaves the
///     existing <see cref="MultiTurnAgentLoop.SubAgentManager"/> alive but dormant: its children keep
///     running and reappear when a later switch exposes the tools again.
/// </param>
/// <param name="LoggerFactory">
///     Optional factory for the rebuilt pipeline middlewares' category loggers, as at construction.
/// </param>
public sealed record AgentReconfiguration(
    IStreamingAgent ProviderAgent,
    FunctionRegistry FunctionRegistry,
    string? SystemPrompt,
    GenerateReplyOptions DefaultOptions,
    bool IncludeAskUserQuestionTool,
    bool IncludeNotifyClientTool,
    SubAgentOptions? SubAgentOptions,
    ILoggerFactory? LoggerFactory = null
);

/// <summary>What <see cref="IReconfigurableAgent.Reconfigure"/> did.</summary>
public enum ReconfigureOutcome
{
    /// <summary>The whole reconfiguration was applied; the next turn uses it.</summary>
    Applied,

    /// <summary>
    /// A run was in progress, so nothing was changed. The caller decides what to do about it — the
    /// sample's host turns this into a refused switch rather than a silent recreate.
    /// </summary>
    RefusedBusy,
}

/// <summary>
/// An agent whose mode/model-dependent configuration can be replaced in place, so a host can switch a
/// live conversation's model or tool surface without disposing the agent and everything it owns.
/// </summary>
public interface IReconfigurableAgent
{
    /// <summary>
    /// Replaces this agent's provider, tool surface, system prompt and per-turn options with
    /// <paramref name="spec"/>, from the next turn onwards.
    /// </summary>
    /// <param name="spec">The new mode/model-dependent configuration.</param>
    /// <returns>
    /// <see cref="ReconfigureOutcome.Applied"/>, or <see cref="ReconfigureOutcome.RefusedBusy"/> when a
    /// run is in progress. An input that has been accepted but not yet started is NOT busy: it survives
    /// and runs under the new configuration.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Synchronous by design, because the host seam that calls it — an agent-pool factory — is a
    /// synchronous <see cref="Func{T, TResult}"/>. Nothing here needs to await: every part is built from
    /// values the caller already has.
    /// </para>
    /// <para>
    /// Build-then-assign. Every new part is constructed into a local first and only then assigned, so a
    /// spec the agent cannot build from throws with the agent still fully on its old configuration
    /// rather than half-switched.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is null.</exception>
    ReconfigureOutcome Reconfigure(AgentReconfiguration spec);
}
