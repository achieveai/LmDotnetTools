using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Top-level configuration for sub-agent orchestration.
/// </summary>
public record SubAgentOptions
{
    /// <summary>
    /// Named templates available for spawning sub-agents.
    /// </summary>
    public required IReadOnlyDictionary<string, SubAgentTemplate> Templates { get; init; }

    /// <summary>
    /// Maximum number of sub-agents that can run concurrently.
    /// </summary>
    public int MaxConcurrentSubAgents { get; init; } = 5;

    /// <summary>
    /// Fallback conversation store factory when a template doesn't specify one.
    /// Null = no persistence for sub-agents.
    /// </summary>
    public Func<string, IConversationStore>? DefaultConversationStoreFactory { get; init; }

    /// <summary>
    /// Tool names that a spawned sub-agent must NOT inherit from the parent, even when its
    /// template sets <c>EnabledTools = null</c> ("inherit everything"). The parent keeps these
    /// tools; only the snapshot handed to sub-agents excludes them. This is the general seam that
    /// keeps a launch/orchestration tool (e.g. <c>StartWorkflow</c>/<c>CheckWorkflow</c>/
    /// <c>WaitWorkflow</c>) — registered on the parent's own registry before the loop is built, so
    /// it lands in the inherit-all snapshot — from leaking into every sub-agent. The
    /// <c>Agent</c>/<c>SendMessage</c>/<c>CheckAgent</c> tools are already excluded structurally
    /// (registered AFTER the snapshot), so they need not be listed here. Null/empty = no extra
    /// exclusions.
    /// </summary>
    public IReadOnlyCollection<string>? NonInheritedToolNames { get; init; }
}
