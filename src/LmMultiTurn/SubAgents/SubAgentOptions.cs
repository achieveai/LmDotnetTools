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
}
