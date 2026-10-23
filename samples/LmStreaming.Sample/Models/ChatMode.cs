using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Models;

/// <summary>
/// Represents a chat mode that defines a persona, system prompt, and available tools.
/// </summary>
public record ChatMode
{
    /// <summary>
    /// Unique identifier for the mode.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name of the mode.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of what this mode does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The system prompt used when this mode is active.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// List of enabled (function/MCP) tool names. If null, all tools are enabled.
    /// </summary>
    public IReadOnlyList<string>? EnabledTools { get; init; }

    /// <summary>
    /// List of enabled server-side built-in tool names (e.g. <c>web_search</c>). Kept separate from
    /// <see cref="EnabledTools"/> so a mode can curate its function tools independently (e.g.
    /// Workspace Agent resolves file/shell tools via the sandbox MCP gateway and lists only the
    /// TaskManager todo-board family in <see cref="EnabledTools"/>) while still declaring which
    /// server-side built-ins it wants. When null, built-in selection falls back to
    /// <see cref="EnabledTools"/> for backward compatibility.
    /// </summary>
    public IReadOnlyList<string>? EnabledBuiltInTools { get; init; }

    /// <summary>
    /// Selection of the tool families that are wired per-mode at agent-construction time rather than
    /// drawn from a static registry: the sandbox/workspace tools, the sub-agent tools, and the
    /// workflow tools. Stored as qualified <c>group:tool</c> ids (<c>sandbox:Bash</c>,
    /// <c>subagents:Agent</c>, <c>workflow:SetWorkflow</c>), with <c>group:*</c> selecting the whole
    /// group — including tools that appear later, which matters for the sandbox because a marketplace
    /// plugin can add tools to the gateway at runtime.
    /// <para>
    /// Kept separate from <see cref="EnabledTools"/> because these three families cannot be filtered
    /// after the fact: the host must know BEFORE building the agent whether to establish a sandbox
    /// session or a workflow runtime at all. <see cref="Services.ModeCapabilities"/> turns this list
    /// into those decisions.
    /// </para>
    /// <para>
    /// <c>null</c> means the mode predates capability selection, and
    /// <see cref="Services.ModeCapabilities.LegacyDefaults"/> applies; an EMPTY list is an explicit
    /// "none". Collapsing the two would strip sub-agents from every mode that already exists.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? EnabledCapabilityTools { get; init; }

    /// <summary>
    /// Optional prompt fragment folded into the system prompt of EVERY sub-agent spawned under a
    /// conversation in this mode (#610), so the mode sets expectations for all sub-agents, not
    /// just the primary agent. Null means no fragment and today's sub-agent prompts unchanged.
    /// </summary>
    public string? SubAgentPrompt { get; init; }

    /// <summary>
    /// Where <see cref="SubAgentPrompt"/> lands relative to each sub-agent template's own prompt:
    /// <c>"prepend"</c> or <c>"append"</c> (see <see cref="Services.ModeSubAgentPrompt"/>). Null
    /// defaults to append when a fragment is present. Validated at the boundaries that write it —
    /// yaml load for system modes, the chat-modes CRUD API for user modes.
    /// </summary>
    public string? SubAgentPromptPlacement { get; init; }

    /// <summary>
    /// Tool ids / group patterns every sub-agent spawned in this mode is GUARANTEED to carry, even
    /// when its agent template declares a restricted <c>tools:</c> list (#623). Uses a SUPERSET of
    /// the mode's tool-selection id language — bare tool names (<c>claim-task</c>), qualified
    /// ids (<c>subagents:SendMessage</c>), and <c>group:*</c> wildcards (<c>tasks:*</c>); the
    /// wildcard expansion over unqualified groups is specific to this field and does NOT work in
    /// <see cref="EnabledTools"/>, which matches exact names only. Resolved
    /// names are unioned into each spawn's toolset AFTER the template filter, then intersected with
    /// what the mode itself exposes, at every spawn depth. Null/empty = no enforcement — restricted
    /// templates keep stripping exactly as before (opt-in).
    /// </summary>
    public IReadOnlyList<string>? SubAgentRequiredTools { get; init; }

    /// <summary>
    /// Optional conversation-wide reasoning effort for sub-agents. Null preserves existing behavior;
    /// supported values are validated where modes are loaded or written.
    /// </summary>
    public string? SubAgentReasoningEffort { get; init; }

    /// <summary>
    /// Optional display name for this mode's ROOT agent — the name sub-agents see in their identity
    /// preamble and use to address the top-level conversation. Null or blank means
    /// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration.AgentCollaborationSetup.DefaultRootName"/>.
    /// The <c>primary</c> alias keeps working either way.
    /// </summary>
    public string? RootAgentName { get; init; }

    /// <summary>
    /// Authoritative model-intelligence tier keyed by canonical registered <c>subagent_type</c>.
    /// Null means the mode declares no type-keyed policy.
    /// </summary>
    public IReadOnlyDictionary<string, int>? SubAgentModelIntelligenceByType { get; init; }

    /// <summary>
    /// Tier used for canonical <c>code-reviewer:*</c> types not present in
    /// <see cref="SubAgentModelIntelligenceByType"/>. Other type families retain their caller/template
    /// routing so a review-mode fallback cannot override unrelated children.
    /// </summary>
    public int? DefaultSubAgentModelIntelligence { get; init; }

    /// <summary>
    /// Mode-layer sandbox environment variables (spec §5): override the workspace's <see cref="Workspace.Env"/>
    /// and are themselves overridden by a conversation's provisioned env. <see langword="null"/> means the
    /// mode declares no env (the common case, and how every mode predating this field reads). Never logged.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Loaded-vs-available skill tiers for conversations in this mode: which skills each agent's
    /// <c>Skill</c> tool lists up front, with every other admitted skill found through
    /// <c>SearchSkill</c>. <see langword="null"/> (every mode predating this field) turns the feature off
    /// and keeps today's behaviour; it is not written, so an untiered mode's JSON is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModeTiers? Tiers { get; init; }

    /// <summary>
    /// Whether this mode is system-defined (read-only) or user-created.
    /// </summary>
    public bool IsSystemDefined { get; init; }

    /// <summary>
    /// Unix timestamp (milliseconds) when the mode was created.
    /// </summary>
    public long CreatedAt { get; init; }

    /// <summary>
    /// Unix timestamp (milliseconds) when the mode was last updated.
    /// </summary>
    public long UpdatedAt { get; init; }

    /// <summary>
    /// Projects this mode onto the provider-neutral <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.AgentProfile"/>
    /// the shared agent pool consumes. Only the fields the pool needs travel across; the
    /// sample-only metadata (description, system-defined flag, timestamps) stays behind.
    /// </summary>
    public AchieveAi.LmDotnetTools.LmAgentInfra.AgentProfile ToAgentProfile() =>
        new(Id, Name, SystemPrompt, EnabledTools, EnabledBuiltInTools, EnabledCapabilityTools)
        {
            SubAgentPrompt = SubAgentPrompt,
            SubAgentPromptPlacement = SubAgentPromptPlacement,
            SubAgentRequiredTools = SubAgentRequiredTools,
            SubAgentReasoningEffort = SubAgentReasoningEffort,
            RootAgentName = RootAgentName,
            SubAgentModelIntelligenceByType = SubAgentModelIntelligenceByType,
            DefaultSubAgentModelIntelligence = DefaultSubAgentModelIntelligence,
        };

    /// <summary>
    /// Implicit projection so existing call sites that hand a <see cref="ChatMode"/> to the pool
    /// (which now speaks <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.AgentProfile"/>) keep
    /// compiling unchanged.
    /// </summary>
    public static implicit operator AchieveAi.LmDotnetTools.LmAgentInfra.AgentProfile(ChatMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return mode.ToAgentProfile();
    }
}

/// <summary>
/// DTO for creating or updating a chat mode.
/// </summary>
public record ChatModeCreateUpdate
{
    private string? _description;
    private IReadOnlyList<string>? _enabledTools;
    private IReadOnlyList<string>? _enabledBuiltInTools;
    private IReadOnlyList<string>? _enabledCapabilityTools;
    private string? _subAgentPrompt;
    private string? _subAgentPromptPlacement;
    private IReadOnlyList<string>? _subAgentRequiredTools;
    private string? _subAgentReasoningEffort;
    private IReadOnlyDictionary<string, int>? _subAgentModelIntelligenceByType;
    private int? _defaultSubAgentModelIntelligence;
    private IReadOnlyDictionary<string, string>? _env;
    private ModeTiers? _tiers;

    /// <summary>
    /// Display name of the mode.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of what this mode does. Omission preserves the stored description on update;
    /// explicit JSON <c>null</c> clears it.
    /// </summary>
    public string? Description
    {
        get => _description;
        init
        {
            _description = value;
            DescriptionIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool DescriptionIsSet { get; private init; }

    /// <summary>
    /// The system prompt used when this mode is active.
    /// </summary>
    public required string SystemPrompt { get; init; }

    /// <summary>
    /// List of enabled tool names. If null, all tools are enabled. Omission preserves the stored allowlist
    /// on update; explicit JSON <c>null</c> enables all tools.
    /// </summary>
    public IReadOnlyList<string>? EnabledTools
    {
        get => _enabledTools;
        init
        {
            _enabledTools = value;
            EnabledToolsIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool EnabledToolsIsSet { get; private init; }

    /// <summary>
    /// Server-side built-in tool names (e.g. <c>web_search</c>). See
    /// <see cref="ChatMode.EnabledBuiltInTools"/>.
    /// </summary>
    /// <remarks>
    /// Presence-aware on update: omission preserves the stored selection, while explicit JSON
    /// <c>null</c> clears it. Create treats omission and explicit null alike.
    /// </remarks>
    public IReadOnlyList<string>? EnabledBuiltInTools
    {
        get => _enabledBuiltInTools;
        init
        {
            _enabledBuiltInTools = value;
            EnabledBuiltInToolsIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool EnabledBuiltInToolsIsSet { get; private init; }

    /// <summary>
    /// Qualified sandbox/sub-agent/workflow selections. See
    /// <see cref="ChatMode.EnabledCapabilityTools"/>. Omission preserves the stored selection on update.
    /// </summary>
    public IReadOnlyList<string>? EnabledCapabilityTools
    {
        get => _enabledCapabilityTools;
        init
        {
            _enabledCapabilityTools = value;
            EnabledCapabilityToolsIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool EnabledCapabilityToolsIsSet { get; private init; }

    /// <summary>
    /// Per-mode sub-agent prompt fragment. See <see cref="ChatMode.SubAgentPrompt"/>. Omission preserves
    /// the stored fragment on update; explicit JSON <c>null</c> clears it.
    /// </summary>
    public string? SubAgentPrompt
    {
        get => _subAgentPrompt;
        init
        {
            _subAgentPrompt = value;
            SubAgentPromptIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool SubAgentPromptIsSet { get; private init; }

    /// <summary>
    /// Fragment placement, <c>"prepend"</c> or <c>"append"</c> (null = append). See
    /// <see cref="ChatMode.SubAgentPromptPlacement"/>. An invalid value is refused with 400 at the
    /// CRUD boundary so it can never be persisted. Omission preserves the stored placement on update.
    /// </summary>
    public string? SubAgentPromptPlacement
    {
        get => _subAgentPromptPlacement;
        init
        {
            _subAgentPromptPlacement = value;
            SubAgentPromptPlacementIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool SubAgentPromptPlacementIsSet { get; private init; }

    /// <summary>
    /// Tools guaranteed to every sub-agent in this mode. See
    /// <see cref="ChatMode.SubAgentRequiredTools"/>. Omission preserves the stored selection on update;
    /// explicit JSON <c>null</c> disables enforcement.
    /// </summary>
    public IReadOnlyList<string>? SubAgentRequiredTools
    {
        get => _subAgentRequiredTools;
        init
        {
            _subAgentRequiredTools = value;
            SubAgentRequiredToolsIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool SubAgentRequiredToolsIsSet { get; private init; }

    /// <summary>Conversation-wide child reasoning effort. See <see cref="ChatMode.SubAgentReasoningEffort"/>.</summary>
    public string? SubAgentReasoningEffort
    {
        get => _subAgentReasoningEffort;
        init
        {
            _subAgentReasoningEffort = value;
            SubAgentReasoningEffortIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool SubAgentReasoningEffortIsSet { get; private init; }

    /// <summary>Canonical child-type routing map. See <see cref="ChatMode.SubAgentModelIntelligenceByType"/>.</summary>
    public IReadOnlyDictionary<string, int>? SubAgentModelIntelligenceByType
    {
        get => _subAgentModelIntelligenceByType;
        init
        {
            _subAgentModelIntelligenceByType = value;
            SubAgentModelIntelligenceByTypeIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool SubAgentModelIntelligenceByTypeIsSet { get; private init; }

    /// <summary>Fallback child tier. See <see cref="ChatMode.DefaultSubAgentModelIntelligence"/>.</summary>
    public int? DefaultSubAgentModelIntelligence
    {
        get => _defaultSubAgentModelIntelligence;
        init
        {
            _defaultSubAgentModelIntelligence = value;
            DefaultSubAgentModelIntelligenceIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool DefaultSubAgentModelIntelligenceIsSet { get; private init; }

    /// <summary>Mode-layer sandbox env. See <see cref="ChatMode.Env"/>. Omission preserves the stored
    /// map on update; explicit JSON <c>null</c> clears it.</summary>
    public IReadOnlyDictionary<string, string>? Env
    {
        get => _env;
        init
        {
            _env = value;
            EnvIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool EnvIsSet { get; private init; }

    /// <summary>Skill tiers. See <see cref="ChatMode.Tiers"/>. Omission preserves the stored tiers on
    /// update; explicit JSON <c>null</c> clears them (feature off).</summary>
    public ModeTiers? Tiers
    {
        get => _tiers;
        init
        {
            _tiers = value;
            TiersIsSet = true;
        }
    }

    [JsonIgnore]
    internal bool TiersIsSet { get; private init; }
}

/// <summary>
/// A mode's capability tiers (<see cref="ChatMode.Tiers"/>). Each entry is a selector
/// <c>&lt;kind&gt;:&lt;ref&gt;</c>, split at the first colon: <c>skill:&lt;gateway name or path&gt;</c>
/// (for example <c>skill:debugging:systematic-debugging</c>; a bare skill name only when it is unique),
/// <c>agent:&lt;template key&gt;</c> (for example <c>agent:debugging:logging-review</c>; root lists only, and only
/// with <see cref="ModeTierCaps.Agent"/> set), <c>tool:&lt;tool name&gt;</c> (for example <c>tool:Read</c>; only with
/// <see cref="ModeTierCaps.Tool"/> set) or <c>plugin:&lt;marketplace&gt;/&lt;plugin&gt;</c> (all of that
/// plugin's skills, and its sub-agents when agents are tiered). A selector that matches nothing or more than one
/// item is logged and ignored.
/// </summary>
public sealed record ModeTiers
{
    /// <summary>
    /// Skills the root agent starts with listed in its <c>Skill</c> tool, and, with <see cref="ModeTierCaps.Agent"/>,
    /// sub-agent templates listed in its <c>Agent</c> tool. Null = none.
    /// </summary>
    public IReadOnlyList<string>? Loaded { get; init; }

    /// <summary>Skills (and, with <see cref="ModeTierCaps.Agent"/>, templates) the root agent always has listed. Null = none.</summary>
    public IReadOnlyList<string>? Pinned { get; init; }

    /// <summary>The same two lists for sub-agents. Null = none.</summary>
    public ModeSubAgentTiers? SubAgent { get; init; }

    /// <summary>Per-kind limits. Null = defaults.</summary>
    public ModeTierCaps? Caps { get; init; }
}

/// <summary>Sub-agent skill and tool tiers. See <see cref="ModeTiers"/>.</summary>
public sealed record ModeSubAgentTiers
{
    /// <summary>
    /// Skills, and with <see cref="ModeTierCaps.Tool"/> sandbox tools, each sub-agent starts with listed: a tool only
    /// when the sub-agent inherited it. Null = none.
    /// </summary>
    public IReadOnlyList<string>? Loaded { get; init; }

    /// <summary>Skills, and with <see cref="ModeTierCaps.Tool"/> sandbox tools, each sub-agent always has listed. Null = none.</summary>
    public IReadOnlyList<string>? Pinned { get; init; }
}

/// <summary>Per-kind limits on non-pinned loaded items. See <see cref="ModeTiers"/>.</summary>
public sealed record ModeTierCaps
{
    /// <summary>Most non-pinned skills kept loaded per agent. Null = the default.</summary>
    public int? Skill { get; init; }

    /// <summary>
    /// Most non-pinned sub-agent templates the root keeps listed in its <c>Agent</c> tool. Null = templates are not
    /// tiered: <c>Agent</c> lists every template, and <c>agent:</c> selectors are ignored. Set = <c>Agent</c> lists
    /// the root's loaded and pinned templates, and <c>SearchAgent</c> and <c>SearchPlugin</c> find the rest.
    /// </summary>
    public int? Agent { get; init; }

    /// <summary>
    /// Most non-pinned sandbox tools an agent keeps in its tool list after its compaction, most recently used first.
    /// Null = sandbox tools are not tiered: every one is listed, and <c>tool:</c> selectors are ignored. Set = the
    /// root's tool list holds only its loaded and pinned sandbox tools; <c>SearchTool</c> finds the rest and
    /// <c>ExecuteTool</c> runs them. A sub-agent given its own <c>ExecuteTool</c> does the same over its own tools.
    /// </summary>
    public int? Tool { get; init; }
}

/// <summary>
/// DTO for copying a mode with a new name.
/// </summary>
public record ChatModeCopy
{
    /// <summary>
    /// The new name for the copied mode.
    /// </summary>
    public required string NewName { get; init; }
}

/// <summary>
/// Represents a tool definition for the frontend.
/// </summary>
public record ToolDefinition
{
    /// <summary>
    /// The function name of the tool — the BARE name the model sees in its tool schema.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what the tool does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The id a mode stores to select this tool. Equal to <see cref="Name"/> for the groups that have
    /// always been addressed by bare name; <c>group:Name</c> for the qualified groups
    /// (<c>sandbox</c>, <c>subagents</c>, <c>workflow</c>). Defaults to <see cref="Name"/> so a
    /// definition built before grouping existed still round-trips.
    /// </summary>
    public string Id
    {
        get => _id ?? Name;
        init => _id = value;
    }

    private readonly string? _id;

    /// <summary>
    /// Which <see cref="Services.ToolGroups"/> bucket this tool belongs to. Drives the section the
    /// Modes editor renders it under, and which mode field the selection is written to.
    /// </summary>
    public string Group { get; init; } = Services.ToolGroups.Sample;

    /// <summary>
    /// Human-readable heading for <see cref="Group"/>, so the client does not have to carry its own
    /// copy of the group-name table.
    /// </summary>
    public string GroupLabel { get; init; } = Services.ToolGroups.LabelFor(Services.ToolGroups.Sample);

    /// <summary>
    /// True for the synthetic "everything in this group" row (<c>group:*</c>) rather than a real
    /// tool. Selecting it also takes in tools that appear later — which is the only correct choice
    /// for the sandbox, whose tool set grows when a marketplace plugin is installed.
    /// </summary>
    public bool IsWildcard { get; init; }

    /// <summary>
    /// True when using this tool causes each conversation in the mode to establish a sandbox gateway
    /// session. Surfaced so the editor can warn before that cost is taken on.
    /// </summary>
    public bool RequiresSandbox { get; init; }

    /// <summary>
    /// Whether a mode that records NO capability selection still gets this tool.
    /// </summary>
    /// <remarks>
    /// Only meaningful for the qualified groups, and it exists so the editor does not have to
    /// re-implement <see cref="Services.ModeCapabilities.LegacyDefaults"/> in TypeScript. The editor
    /// pre-selects exactly these rows when it opens a mode whose
    /// <see cref="ChatMode.EnabledCapabilityTools"/> is null, so the first save records what the mode
    /// already had rather than silently narrowing it. Unqualified groups ignore this flag: their
    /// legacy default is "all", expressed by a null allow-list.
    /// </remarks>
    public bool IsLegacyDefault { get; init; }

    /// <summary>
    /// Set when the catalog could not enumerate this group live (e.g. the sandbox gateway was
    /// unreachable) and the listed tools are a static baseline that may be incomplete. Null when the
    /// listing is authoritative.
    /// </summary>
    public string? CatalogWarning { get; init; }
}
