using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Configuration record for named sub-agent templates.
/// Each template defines how to create and configure a sub-agent instance.
/// </summary>
public record SubAgentTemplate
{
    /// <summary>
    /// Optional display name for the template.
    /// The template is identified by its key in the Templates dictionary, not this field.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Independent system prompt for the sub-agent.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Factory that creates the LLM provider agent for this template.
    /// </summary>
    public required Func<IStreamingAgent> AgentFactory { get; init; }

    /// <summary>
    /// Default options for model, temperature, etc.
    /// </summary>
    public GenerateReplyOptions? DefaultOptions { get; init; }

    /// <summary>
    /// Tool filter: null = inherit ALL parent tools.
    /// If specified, only the listed tool names are available to the sub-agent.
    /// </summary>
    public IReadOnlyList<string>? EnabledTools { get; init; }

    /// <summary>
    /// Maximum number of agentic turns per run before stopping.
    /// </summary>
    public int MaxTurnsPerRun { get; init; } = 50;

    /// <summary>
    /// Factory that creates a persistence store for sub-agent conversations.
    /// Null = use the parent's default or no persistence.
    /// </summary>
    public Func<string, IConversationStore>? ConversationStoreFactory { get; init; }
}
