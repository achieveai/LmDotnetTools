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
    IReadOnlyList<string>? EnabledCapabilityTools = null
)
{
    /// <summary>
    /// Optional prompt fragment the host folds into the system prompt of every sub-agent spawned
    /// under a conversation running this profile, so the mode can set expectations for all
    /// sub-agents rather than only the primary agent. <c>null</c> (or whitespace) means the
    /// profile declares no fragment and sub-agent prompts are left exactly as they are today.
    /// Deliberately an init-only property rather than a positional parameter so every existing
    /// constructor call keeps compiling unchanged — this record is shared library surface.
    /// </summary>
    public string? SubAgentPrompt { get; init; }

    /// <summary>
    /// Where <see cref="SubAgentPrompt"/> is folded relative to each sub-agent's own prompt:
    /// <c>"prepend"</c> or <c>"append"</c>. <c>null</c> defaults to append when a fragment is
    /// present. This record only carries the value; hosts validate it at their own boundaries
    /// (e.g. refusing an invalid value at yaml load or with a 400 at a CRUD API) before it ever
    /// gets here.
    /// </summary>
    public string? SubAgentPromptPlacement { get; init; }

    /// <summary>
    /// Tool names / group patterns (the same id language as the mode's tool selection, e.g.
    /// <c>tasks:*</c>, <c>subagents:*</c>, or exact bare tool names) that every sub-agent spawned
    /// under a conversation running this profile must carry, unioned into each spawn's resolved
    /// toolset AFTER the sub-agent template's own <c>tools:</c> restriction is applied (#623). The
    /// host resolves the patterns to concrete names and intersects with what the mode itself
    /// exposes — a mode can never grant a sub-agent a tool it does not have. Null/empty means no
    /// enforcement: restricted templates keep stripping exactly as before. Init-only property (not a
    /// positional parameter) for the same shared-surface compatibility reason as
    /// <see cref="SubAgentPrompt"/>.
    /// </summary>
    public IReadOnlyList<string>? SubAgentRequiredTools { get; init; }
}
