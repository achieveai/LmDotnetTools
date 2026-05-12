using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

/// <summary>
///     Provider-neutral bundle of runtime inputs supplied by a client application:
///     system prompt, sub-agents, skills, and MCP servers.
/// </summary>
/// <remarks>
///     Each provider projects whatever subset its underlying runtime supports.
///     Providers MUST log a single warning per loop construction for any
///     non-empty field they cannot honor, then ignore it. See each provider's
///     <c>Options.Profile</c> XML doc for the per-provider support matrix.
/// </remarks>
public sealed record AgentRuntimeProfile
{
    /// <summary>
    ///     System prompt text. When set, supported providers use this in place of
    ///     their own system prompt / developer instructions.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    ///     Skills to expose to the spawned agent. Optional; providers that do not
    ///     support skills log once and ignore.
    /// </summary>
    public IReadOnlyList<AgentSkill> Skills { get; init; } = [];

    /// <summary>
    ///     Sub-agents to expose to the spawned agent. Optional; providers that do
    ///     not support sub-agents log once and ignore.
    /// </summary>
    public IReadOnlyList<SubAgentDefinition> SubAgents { get; init; } = [];

    /// <summary>
    ///     MCP servers to make available. Optional; providers that do not support
    ///     MCP log once and ignore. Supporting providers MUST merge with host-loaded
    ///     MCP config, with profile entries winning on key collision.
    /// </summary>
    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; }
        = ImmutableDictionary<string, McpServerConfig>.Empty;
}
