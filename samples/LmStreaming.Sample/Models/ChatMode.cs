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
    /// List of enabled tool names. If null, all tools are enabled.
    /// </summary>
    public IReadOnlyList<string>? EnabledTools { get; init; }

    /// <summary>
    /// Server-side built-in tool names (e.g. <c>web_search</c>). See
    /// <see cref="ChatMode.EnabledBuiltInTools"/>.
    /// </summary>
    /// <remarks>
    /// Absent from this DTO until now, which meant every save through the Modes editor silently
    /// dropped the mode's built-in selection — a copy of Workspace Agent kept <c>web_search</c> only
    /// until its first edit.
    /// </remarks>
    public IReadOnlyList<string>? EnabledBuiltInTools { get; init; }

    /// <summary>
    /// Qualified sandbox/sub-agent/workflow selections. See
    /// <see cref="ChatMode.EnabledCapabilityTools"/>.
    /// </summary>
    public IReadOnlyList<string>? EnabledCapabilityTools { get; init; }

    /// <summary>
    /// Per-mode sub-agent prompt fragment. See <see cref="ChatMode.SubAgentPrompt"/>.
    /// </summary>
    public string? SubAgentPrompt { get; init; }

    /// <summary>
    /// Fragment placement, <c>"prepend"</c> or <c>"append"</c> (null = append). See
    /// <see cref="ChatMode.SubAgentPromptPlacement"/>. An invalid value is refused with 400 at the
    /// CRUD boundary so it can never be persisted.
    /// </summary>
    public string? SubAgentPromptPlacement { get; init; }
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
