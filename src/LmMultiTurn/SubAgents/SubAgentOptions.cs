using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
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
    /// keeps a launch/orchestration tool (e.g. <c>StartWorkflowAgent</c>/<c>CheckWorkflow</c>/
    /// <c>WaitWorkflow</c>) — registered on the parent's own registry before the loop is built, so
    /// it lands in the inherit-all snapshot — from leaking into every sub-agent. The
    /// <c>Agent</c>/<c>SendMessage</c>/<c>CheckAgent</c> tools are already excluded structurally
    /// (registered AFTER the snapshot), so they need not be listed here. Null/empty = no extra
    /// exclusions.
    /// </summary>
    public IReadOnlyCollection<string>? NonInheritedToolNames { get; init; }

    /// <summary>
    /// Extra tools, sourced from a non-WorkflowAgent ancestor, to merge into the snapshot handed to
    /// this loop's sub-agents — over and above the tools inherited from this loop's own registry.
    /// Null (default) = no external tools; every ordinary sub-agent path leaves this unset, so it has
    /// no effect there. It exists so a WorkflowAgent controller — which runs on an isolated,
    /// workflow-only registry — can be <em>transparent</em>: its delegate sub-agents inherit the
    /// launching conversation's tools even though the controller's own registry does not carry them.
    /// The merge is applied in <c>MultiTurnAgentLoop</c>'s ctor and skips any name present in
    /// <see cref="NonInheritedToolNames"/> or already exposed by this loop, so it can never shadow a
    /// control-plane tool. See <see cref="InheritableToolSnapshot"/>.
    /// </summary>
    public InheritableToolSnapshot? ExternalInheritableTools { get; init; }

    /// <summary>
    /// Reasoning-effort floor a spawned sub-agent inherits from the parent conversation when its template
    /// pins no <see cref="SubAgentTemplate.Effort"/> and it makes no model choice of its own (parent-model
    /// reuse). The host sets this to the parent's effort (e.g. <c>High</c>) so an ordinary sub-agent thinks
    /// like the launching conversation instead of falling back to the model's un-nudged default. It is
    /// applied ONLY on the parent-model path: a template that lowers its own <c>Effort</c>, or that pins /
    /// tier-resolves a model, overrides the floor ("less thinking or a different model" wins). Null (default)
    /// = no inherited floor, so every non-host consumer keeps the previous behavior. This is the
    /// characteristics-path counterpart to <see cref="InheritedReasoning"/> (which serves the plain path).
    /// </summary>
    public ReasoningEffort? InheritedEffort { get; init; }

    /// <summary>
    /// Pre-shaped reasoning metadata a plain-path delegate inherits when its template carries no
    /// <see cref="SubAgentTemplate.CharacteristicsAgentFactory"/> (e.g. a WorkflowAgent controller's
    /// transparent delegate) and no reasoning of its own. Unlike <see cref="InheritedEffort"/> — an abstract
    /// effort re-shaped per child model — this is a concrete, already-transport-shaped dictionary because a
    /// plain delegate runs on the SAME model/transport as its controller, so the host can shape it once. It
    /// is seeded onto the delegate's <c>GenerateReplyOptions.ExtraProperties</c> only when the delegate made
    /// no model override (a different model may use a different transport). Null/empty (default) = no
    /// inherited reasoning, so ordinary sub-agent paths (which leave this unset) are unaffected.
    /// </summary>
    public ImmutableDictionary<string, object?>? InheritedReasoning { get; init; }

    /// <summary>
    /// Host-supplied resolver that maps a spawn's model-intelligence tier (the <c>modelIntelligence</c>
    /// argument of the <c>Agent</c> tool, or a workflow task's tier) to a concrete model id, or null to
    /// leave the sub-agent on its parent-inherited model. The library is model-catalog-agnostic, so the
    /// host owns the tier ladder and passes this delegate in; the manager only calls it (with the raw
    /// tier) when a spawn requested a tier AND set no explicit model override. A non-null return is treated
    /// as a tier-resolved model (<see cref="SubAgentCharacteristics.IsModelTierResolved"/>), so the
    /// characteristics factory builds a real provider for it rather than handing back the parent. Null
    /// (default) disables tier resolution, so every non-host consumer keeps the previous behavior.
    /// </summary>
    public Func<int, string?>? TierModelResolver { get; init; }

    /// <summary>
    /// Host-supplied factory that builds a provider agent for a tier-resolved model on the PLAIN path (a
    /// template with no <see cref="SubAgentTemplate.CharacteristicsAgentFactory"/> — e.g. a WorkflowAgent
    /// controller's transparent delegate). When <see cref="TierModelResolver"/> maps a spawn's tier to a
    /// concrete model, that model may use a different transport than the controller's own provider, so the
    /// plain <see cref="SubAgentTemplate.AgentFactory"/> (which builds the controller's transport) would send
    /// the request to the wrong endpoint. This factory builds a transport-correct provider for the resolved
    /// model id instead; the manager owns and disposes it. It is consulted ONLY on the plain path and ONLY
    /// when a tier resolved to a model — the characteristics path builds its own transport-correct provider,
    /// so hosts that use it need not set this. Null (default) = fall back to the template's provider, so
    /// same-transport tiers and every non-host consumer keep the previous behavior.
    /// </summary>
    public Func<string, IStreamingAgent>? TierAgentFactory { get; init; }
}
