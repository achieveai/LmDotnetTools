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
    /// One-line summary of what this sub-agent does. Embedded in the Agent tool
    /// description's template catalog so the parent LLM can pick the right type.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Delegation guidance: when the parent should (and should not) delegate to
    /// this sub-agent. Embedded in the Agent tool description's template catalog.
    /// </summary>
    public string? WhenToUse { get; init; }

    /// <summary>
    /// Independent system prompt for the sub-agent.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// Factory that creates the LLM provider agent for this template.
    /// </summary>
    /// <remarks>
    /// Each invocation MUST return a fresh, independently disposable agent — never a shared, cached, or
    /// externally-owned instance. Callers that route an inherited-model sub-agent through this factory
    /// take ownership of the returned agent (<see cref="SubAgentProviderAgent.OwnsAgent"/> = true) and
    /// dispose it when the sub-agent's run completes, so returning a shared instance here would dispose
    /// a provider still in use elsewhere.
    /// </remarks>
    public required Func<IStreamingAgent> AgentFactory { get; init; }

    /// <summary>
    /// Optional factory that creates the provider agent from the resolved spawn characteristics.
    /// When null, <see cref="AgentFactory"/> is used.
    /// </summary>
    public Func<SubAgentCharacteristics, SubAgentProviderAgent>? CharacteristicsAgentFactory { get; init; }

    /// <summary>
    /// Default options for model, temperature, etc.
    /// </summary>
    public GenerateReplyOptions? DefaultOptions { get; init; }

    /// <summary>
    /// Whether <see cref="GenerateReplyOptions.ModelId"/> was explicitly selected by this template.
    /// </summary>
    public bool IsModelExplicitlySelected { get; init; }

    /// <summary>
    /// Whether the template model was selected from a model-intelligence tier rather than pinned
    /// directly by the template author.
    /// </summary>
    public bool IsModelTierResolved { get; init; }

    /// <summary>
    /// Optional reasoning effort requested for this sub-agent.
    /// </summary>
    public ReasoningEffort? Effort { get; init; }

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
