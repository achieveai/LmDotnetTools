namespace LmStreaming.Sample.Services;

/// <summary>
///     The groups the Modes editor buckets selectable tools into, and the id scheme a mode uses to
///     name a tool inside one.
/// </summary>
/// <remarks>
///     <para>
///         Two id shapes exist, and the split is deliberate rather than cosmetic.
///     </para>
///     <para>
///         <b>Bare ids</b> (<see cref="BuiltIn" />, <see cref="Sample" />, <see cref="Tasks" />,
///         <see cref="Web" />) are the names modes have always stored in
///         <c>ChatMode.EnabledTools</c> / <c>ChatMode.EnabledBuiltInTools</c> — <c>get_weather</c>,
///         <c>WebSearch</c>, <c>add-task</c>, <c>web_search</c>. They keep that exact form so no
///         persisted mode needs migrating.
///     </para>
///     <para>
///         <b>Qualified ids</b> (<see cref="Sandbox" />, <see cref="SubAgents" />,
///         <see cref="Workflow" />) carry a <c>group:</c> prefix — <c>sandbox:Bash</c>,
///         <c>subagents:Agent</c>, <c>workflow:SetWorkflow</c>. These three families were never
///         selectable before, so the prefix costs nothing in compatibility and buys disambiguation:
///         a sandbox <c>Read</c> and a hypothetical sample <c>Read</c> are different tools and must
///         not share a selection id.
///     </para>
///     <para>
///         <b>The prefix is a SELECTION id only.</b> It exists in the catalog and in
///         <c>ChatMode.EnabledCapabilityTools</c>; it is stripped before the tool is registered on an
///         agent. The model always sees the bare name (<c>Bash</c>, <c>Agent</c>,
///         <c>SetWorkflow</c>) — which is already how the sandbox MCP wiring behaves
///         (<c>omitServerPrefix: true</c>). No prefixed name may ever reach a tool schema, a tool
///         call, or a system prompt.
///     </para>
/// </remarks>
public static class ToolGroups
{
    /// <summary>Server-side provider built-ins (e.g. <c>web_search</c>). Bare ids.</summary>
    public const string BuiltIn = "builtin";

    /// <summary>The sample's own demo function tools. Bare ids.</summary>
    public const string Sample = "sample";

    /// <summary>The per-conversation todo list (<c>TaskManager</c>). Bare ids.</summary>
    public const string Tasks = "tasks";

    /// <summary>Jina-backed <c>WebSearch</c>/<c>WebFetch</c> fallbacks. Bare ids.</summary>
    public const string Web = "web";

    /// <summary>Book-search MCP tools, present only when the LlmQuery MCP is configured. Bare ids.</summary>
    public const string Knowledge = "knowledge";

    /// <summary>Sandbox gateway file/shell tools. Qualified ids.</summary>
    public const string Sandbox = "sandbox";

    /// <summary>Sub-agent delegation and collaboration tools. Qualified ids.</summary>
    public const string SubAgents = "subagents";

    /// <summary>Workflow authoring and workflow-launch tools. Qualified ids.</summary>
    public const string Workflow = "workflow";

    /// <summary>
    ///     The groups whose ids carry a <c>group:</c> prefix and which a mode opts into through
    ///     <c>ChatMode.EnabledCapabilityTools</c>. Everything else is addressed by bare name through
    ///     the pre-existing <c>EnabledTools</c>/<c>EnabledBuiltInTools</c> lists.
    /// </summary>
    public static readonly IReadOnlyList<string> Qualified = [Sandbox, SubAgents, Workflow];

    /// <summary>
    ///     Every catalog group, bare-id and qualified alike. This is the group vocabulary of the
    ///     mode's <c>SubAgentRequiredTools</c> pattern language (#623), where <c>tasks:*</c> is a
    ///     meaningful pattern even though the <c>tasks</c> group stores bare ids in
    ///     <c>EnabledTools</c> — unlike <see cref="Qualified"/>, which governs only the
    ///     <c>EnabledCapabilityTools</c> selection ids.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        BuiltIn,
        Sample,
        Tasks,
        Web,
        Knowledge,
        Sandbox,
        SubAgents,
        Workflow,
    ];

    /// <summary>The token that selects every tool in a qualified group, now and in future.</summary>
    public const string WildcardTool = "*";

    /// <summary>Human-readable section heading for <paramref name="group" />.</summary>
    public static string LabelFor(string group) =>
        group switch
        {
            BuiltIn => "Built-in (server-side)",
            Sample => "Sample tools",
            Tasks => "Tasks",
            Web => "Web",
            Knowledge => "Knowledge base",
            Sandbox => "Workspace (sandbox)",
            SubAgents => "Sub-agents",
            Workflow => "Workflow",
            _ => group,
        };

    /// <summary>Whether <paramref name="group" /> uses qualified <c>group:tool</c> ids.</summary>
    public static bool IsQualified(string group) => Qualified.Contains(group, StringComparer.Ordinal);

    /// <summary>Builds the selection id for <paramref name="toolName" /> inside <paramref name="group" />.</summary>
    public static string Qualify(string group, string toolName) => $"{group}:{toolName}";

    /// <summary>Builds the "everything in this group" token for <paramref name="group" />.</summary>
    public static string Wildcard(string group) => Qualify(group, WildcardTool);

    /// <summary>
    ///     Splits a qualified selection id into its group and bare tool name. Returns <c>false</c> for
    ///     a bare id (no <c>:</c>) or an unknown group, so a caller can never mistake a bare
    ///     <c>web_search</c> for a qualified one.
    /// </summary>
    public static bool TrySplit(string id, out string group, out string toolName) =>
        TrySplitCore(id, IsQualified, out group, out toolName);

    /// <summary>
    ///     Like <see cref="TrySplit"/>, but recognizes EVERY catalog group (<see cref="All"/>) rather
    ///     than only the qualified ones — the split for the <c>SubAgentRequiredTools</c> pattern
    ///     language (#623). Still returns <c>false</c> for a bare id or an unknown prefix, so a bare
    ///     tool name whose value happens to contain a colon is passed through as a name, never
    ///     misread as a group.
    /// </summary>
    public static bool TrySplitAnyKnown(string id, out string group, out string toolName) =>
        TrySplitCore(id, g => All.Contains(g, StringComparer.Ordinal), out group, out toolName);

    private static bool TrySplitCore(string id, Func<string, bool> isKnownGroup, out string group, out string toolName)
    {
        group = string.Empty;
        toolName = string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var separator = id.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == id.Length - 1)
        {
            return false;
        }

        var candidateGroup = id[..separator];
        if (!isKnownGroup(candidateGroup))
        {
            return false;
        }

        group = candidateGroup;
        toolName = id[(separator + 1)..];
        return true;
    }
}
