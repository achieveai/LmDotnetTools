namespace AchieveAi.LmDotnetTools.LmAgentInfra;

/// <summary>
/// Provider-neutral description of an agent's identity, system prompt, and tool gating.
/// This is the slice of an application's "chat mode" / agent configuration that the shared
/// <see cref="Agents.MultiTurnAgentPool"/> and its agent-creation callback actually consume.
/// UI-only concerns (descriptions, timestamps, system-defined flags) stay in the host app.
/// </summary>
/// <param name="Id">Stable identifier for the profile (used for logging and recreation).</param>
/// <param name="Name">Human-readable name (used for logging/diagnostics).</param>
/// <param name="SystemPrompt">The system prompt the agent loop is created with.</param>
/// <param name="EnabledTools">
/// Allow-list of MCP/function tool names the agent may use, or <c>null</c> for "all".
/// </param>
/// <param name="EnabledBuiltInTools">
/// Allow-list of provider built-in tool names (e.g. web_search) the agent may use, or
/// <c>null</c> for "all".
/// </param>
public sealed record AgentProfile(
    string Id,
    string Name,
    string SystemPrompt,
    IReadOnlyList<string>? EnabledTools = null,
    IReadOnlyList<string>? EnabledBuiltInTools = null);
