using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
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
    /// Maximum number of accepted spawns waiting for a concurrency slot. Once full, new over-capacity
    /// requests fail immediately with a recoverable <c>queue_full</c> tool result instead of retaining
    /// unbounded prompts and future billable work. Defaults to 100; set lower for constrained hosts.
    /// </summary>
    public int MaxQueuedSubAgents { get; init; } = 100;

    /// <summary>
    /// Capacity of each spawned sub-agent's per-subscriber output channel — the buffer that absorbs a
    /// viewer (a focused sub-agent tab) reading slower than the child publishes. A subscriber that
    /// fills it is dropped and told to resync rather than being allowed to stall the child's run, so
    /// this is the knob that decides how much lag is tolerated before that happens. Defaults to the
    /// same 1000 as a top-level loop, so hosts that leave it unset are unaffected.
    /// </summary>
    public int OutputChannelCapacity { get; init; } = 1000;

    /// <summary>
    /// Fallback conversation store factory when a template doesn't specify one.
    /// Null = no persistence for sub-agents.
    /// </summary>
    public Func<string, IConversationStore>? DefaultConversationStoreFactory { get; init; }

    /// <summary>
    /// Provenance-aware counterpart to <see cref="DefaultConversationStoreFactory"/> (#275). Where the
    /// plain factory is handed only the child thread id — so a host that wants to stamp a parent→child
    /// link has to capture the parent identity ONCE, at the level it configures, and every descendant
    /// then inherits that same captured identity — this variant is invoked by the SPAWNING manager and
    /// handed that manager's OWN identity: the child's actual parent thread id (this manager's parent
    /// thread) and a describe callback over THIS manager's live roster. A grandchild is therefore
    /// attributed to its real parent and its snapshot resolves against the manager that actually spawned
    /// it, instead of both collapsing to the root.
    /// <para>
    /// Like <see cref="ChildToolProviderFactory"/>, and UNLIKE the three spawn-authority hooks, this is
    /// safe to inherit verbatim through <see cref="ForChildLoop"/> precisely because it takes its
    /// identity from the invoking manager rather than from a value captured at one level. It is
    /// therefore deliberately NOT cleared there. Preferred over <see cref="DefaultConversationStoreFactory"/>
    /// for a spawned child when both are set; a template's own
    /// <see cref="SubAgentTemplate.ConversationStoreFactory"/> still wins over both. Null (default) =
    /// fall back to <see cref="DefaultConversationStoreFactory"/>, unchanged for every existing host.
    /// </para>
    /// </summary>
    public Func<string, string?, Func<string, SubAgentSnapshot?>, IConversationStore>?
        ProvenanceAwareConversationStoreFactory
    { get; init; }

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
    /// The sub-agent tool names this loop may expose, or null (default) for the whole surface its
    /// collaboration shape emits. A non-null set is an allow-list applied on top of that shape.
    /// </summary>
    /// <remarks>
    /// This narrows the PARENT's own delegation surface, which is what a host offering a per-tool
    /// choice (e.g. a chat mode whose editor lists <c>Agent</c>/<c>SendMessage</c>/<c>CheckAgent</c>
    /// separately) needs in order to grant exactly what was chosen. It is distinct from
    /// <see cref="NonInheritedToolNames"/>, which controls what CHILDREN inherit and leaves the
    /// parent untouched. Filtering can only remove: a name listed here that the shape does not emit
    /// stays absent, so an allow-list can never widen the surface.
    /// </remarks>
    public IReadOnlySet<string>? ExposedToolNames { get; init; }

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

    /// <summary>
    /// Host-supplied predicate that validates a spawn's explicit <c>model</c> override (the <c>Agent</c>
    /// tool's <c>model</c> argument) against the host's model catalog, returning <c>true</c> when the id
    /// names a model the host can actually build a provider for. The <c>model</c> argument is an
    /// unconstrained free-form string, so a parent/controller LLM can fill it with an invented id
    /// (e.g. <c>"gpt-5"</c>), a value that belongs in another field (a <c>subagent_type</c> like
    /// <c>"general-purpose"</c>, or a placeholder like <c>"none"</c>), or a plain typo. Passed straight
    /// through, such a value becomes the request model and hard-fails at the provider with a BadRequest —
    /// a wasted spawn plus its tokens and a retry storm. When this validator is set and REJECTS an
    /// override, the manager DROPS it (logs once) and falls through to tier/parent resolution exactly as
    /// if no override had been given. The library is catalog-agnostic, so the host owns the check (e.g.
    /// against the discovered Copilot catalog). Null (default) = no validation, so every non-host consumer
    /// keeps the previous pass-through behavior. Pairs with <see cref="AvailableModelIds"/>, which surfaces
    /// the same valid ids to the tool descriptor so the LLM is steered to a real id in the first place.
    /// </summary>
    public Func<string, bool>? ModelOverrideValidator { get; init; }

    /// <summary>
    /// The concrete model ids a spawn's <c>model</c> override may name, surfaced to the <c>Agent</c> tool
    /// descriptor so the parent/controller LLM picks a real id instead of inventing one. This is the
    /// descriptor-facing counterpart to <see cref="ModelOverrideValidator"/> (which enforces the same set
    /// at runtime); hosts should wire both from one source. Null/empty (default) = the tool descriptor
    /// keeps its generic "defaults to the template's configured model" wording and lists no ids.
    /// </summary>
    public IReadOnlyCollection<string>? AvailableModelIds { get; init; }

    /// <summary>
    /// Host-supplied model id every sub-agent spawned in this conversation runs on unless the SPAWN itself
    /// named a model or a tier. It is the operator's conversation-wide default — the knob that splits a
    /// cheap orchestrator from stronger workers — and so it sits at a specific rung of the ladder:
    /// <code>spawn-model &gt; spawn-tier &gt; DefaultSubAgentModelId &gt; template-model &gt; template-tier &gt; parent</code>
    /// <para>
    /// It deliberately outranks the TEMPLATE's own <c>model:</c>/<c>modelintelligence:</c> frontmatter. A
    /// template is authored wherever its markdown lives — for a review host, in a workspace the operator
    /// does not edit and the calling daemon cannot read — so a template-declared model would otherwise
    /// silently override the one the operator configured and pays for. A per-spawn choice still wins,
    /// because that is the parent agent making a deliberate, task-specific decision at dispatch time.
    /// </para>
    /// <para>
    /// Null/blank (default) = no conversation default, so every existing consumer keeps the previous
    /// ordering exactly. When it does apply, the selection is reported as <c>conversation-default</c> in
    /// <see cref="SubAgentModelRouting.SelectionSource"/>, which is the only way an operator can tell "the
    /// configured model won" from "nothing was configured and the child inherited the parent" — the two
    /// states this knob exists to distinguish and previously could not.
    /// </para>
    /// </summary>
    public string? DefaultSubAgentModelId { get; init; }

    /// <summary>
    /// Host-supplied gate consulted at the <c>Agent</c> tool boundary just before a spawn: given the spawn's
    /// <c>name</c> argument (null when omitted), it returns <c>null</c> to ALLOW the spawn or a corrective
    /// message to REJECT it as a recoverable tool error (surfaced to the caller like the other
    /// <c>Agent</c>-handler errors). It exists so a host that correlates spawn results by an EXACT <c>name</c>
    /// — a WorkflowAgent controller, whose delegate results are joined to workflow units by name only — can
    /// reject a mis-named spawn up front instead of letting it run and be silently discarded, then re-spawned
    /// in a loop. The gate is workflow-agnostic here: a plain <c>name → message?</c> function; the workflow
    /// layer supplies the closure (over its live runtime). Null (default) = no gate, so every ordinary
    /// sub-agent host keeps the previous pass-through behavior.
    /// </summary>
    public Func<string?, string?>? SpawnNameGate { get; init; }

    /// <summary>
    /// Optional host authority for a named spawn's model selection. When this resolver returns a
    /// selection, the Agent-tool boundary replaces the caller/LLM supplied optional <c>model</c> and
    /// <c>modelIntelligence</c> values with it — including authoritative nulls. Workflow hosts use this
    /// to prevent tool-calling models from filling omitted optional fields with placeholders such as
    /// <c>model=""</c> / <c>modelIntelligence=0</c>, which would otherwise override the authored unit or
    /// discovered template. Null (default), or a null resolver result, preserves ordinary Agent behavior.
    /// </summary>
    public Func<string?, SubAgentSpawnModelSelection?>? SpawnModelSelectionResolver { get; init; }

    /// <summary>
    /// Optional host authority for a named spawn's collaboration <c>role</c> and <c>description</c>. When
    /// this resolver returns metadata, admission uses it in place of the caller/LLM supplied values, so a
    /// host that already knows what it delegated — a workflow controller spawning an authored task —
    /// publishes directory metadata derived from its own trusted definition rather than from whatever the
    /// tool-calling model typed. A <see cref="SubAgentTemplate"/> whose role is
    /// <see cref="SubAgentRoleMode.Fixed"/> still wins over both: that template already owns a trusted role.
    /// Null (default), or a null resolver result, preserves ordinary Agent behavior.
    /// </summary>
    public Func<string?, SubAgentSpawnMetadata?>? SpawnMetadataResolver { get; init; }

    /// <summary>
    /// Host-supplied factory for a tool provider that must be built PER AGENT because it is bound to the
    /// agent it acts AS — the transcript read tool of #244 being the motivating case: an instance
    /// authorized for one reader can never be inherited, because an inherited copy hands every descendant
    /// its ancestor's reach. The manager calls this once per collaborating child, with that child's
    /// collaboration agent id, and registers the result on the child's own registry BEFORE the child loop
    /// snapshots its inheritable tools — so every participant in the hierarchy gets its OWN instance
    /// instead of one shared instance or none at all. Pair it with <see cref="NonInheritedToolNames"/> for
    /// the same tool names: the factory supplies each level and the exclusion stops the level below from
    /// ALSO inheriting the level above's instance, which is the leak. A null return builds no provider for
    /// that child. Null (default) = no per-agent providers, so every non-host consumer is unaffected.
    /// </summary>
    public Func<string, IFunctionProvider?>? ChildToolProviderFactory { get; init; }

    /// <summary>
    /// The options a spawned child's OWN loop runs on. Everything the host configured — templates,
    /// limits, model selection/validation, tool inheritance, per-agent providers — is preserved, but the
    /// three spawn-authority hooks are cleared, because they belong to ONE host at ONE level rather than
    /// to the whole subtree. A workflow controller closes them over its live runtime to gate spawns by
    /// authored unit name and to stamp trusted role/description onto them; inherited verbatim by a
    /// delegate, they would reject the delegate's ordinary sub-agents (whose names are not workflow
    /// units), silently overwrite their model selection, and publish the controller's authored metadata
    /// as if it described work the delegate invented. See #244.
    /// <para>
    /// <see cref="ProvenanceAwareConversationStoreFactory"/> and <see cref="ChildToolProviderFactory"/>
    /// are deliberately NOT among the cleared hooks: both take their identity from the invoking manager
    /// (a child's collaboration id, or the spawning manager's own parent thread and roster), so inheriting
    /// them verbatim resolves each descendant correctly rather than misattributing it to the root (#275).
    /// </para>
    /// </summary>
    internal SubAgentOptions ForChildLoop() =>
        this with
        {
            SpawnNameGate = null,
            SpawnModelSelectionResolver = null,
            SpawnMetadataResolver = null,
        };
}

/// <summary>An authoritative per-spawn model override/tier pair supplied by a host.</summary>
public sealed record SubAgentSpawnModelSelection(string? Model, int? ModelIntelligence);

/// <summary>
/// Authoritative per-spawn collaboration metadata supplied by a host that owns the delegation's meaning.
/// Both values are subject to the ordinary length validation in
/// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration.AgentCollaborationContext"/>; a host is expected to bound them itself so a long
/// authored label is shortened rather than failing the spawn.
/// </summary>
public sealed record SubAgentSpawnMetadata(string Role, string Description);
