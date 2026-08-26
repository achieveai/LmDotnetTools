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
/// <param name="EnabledCapabilityTools">
/// Selection of the tool families a host grants per-mode rather than per-tool — sandbox/workspace
/// tools, sub-agent tools, workflow tools — as qualified <c>group:tool</c> ids (e.g.
/// <c>sandbox:Bash</c>), with <c>group:*</c> meaning "every tool in that group, including ones added
/// later". Kept separate from <paramref name="EnabledTools"/> because those three families are not
/// registered from a static catalog: they are wired by the host at agent-construction time, so the
/// host needs to know whether to establish a sandbox session or a workflow runtime at all — a
/// question a flat tool allow-list cannot answer.
/// <para>
/// <c>null</c> means the profile predates this field and the host must apply its legacy defaults;
/// an EMPTY list is an explicit "none". The two are deliberately distinct.
/// </para>
/// <para>
/// The <c>group:</c> prefix is a SELECTION id only — it is stripped before a tool is registered, so
/// the model always sees the bare name (<c>Bash</c>, <c>Agent</c>, <c>SetWorkflow</c>).
/// </para>
/// </param>
public sealed record AgentProfile(
    string Id,
    string Name,
    string SystemPrompt,
    IReadOnlyList<string>? EnabledTools = null,
    IReadOnlyList<string>? EnabledBuiltInTools = null,
    IReadOnlyList<string>? EnabledCapabilityTools = null);
