namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Stable codes for the spawn-time capability mismatches a spawn receipt reports (#671). Each code is
/// also the receipt field that carries its data, so a dispatcher reads one name, not two.
/// </summary>
/// <remarks>
/// These live beside the record that produces them rather than in a shared registry: a code belongs
/// to its emitter, and a central file would let an unrelated domain silently reuse a spelling. The
/// two spawn-unavailability reasons a receipt can also carry — <c>depth_limit</c> and
/// <c>spawn_suppressed</c> — are NOT redefined here; they are read from
/// <see cref="SubAgentCollaborationFailureCodes.DepthLimit"/> and
/// <see cref="SubAgentToolProvider.SpawnSuppressedCode"/> so no alias can drift from the live spelling.
/// </remarks>
internal static class SpawnCapabilityCodes
{
    /// <summary><c>add_tools</c> entries naming a tool the parent does not expose. They were ignored.</summary>
    public const string UnmatchedAddTools = "unmatched_add_tools";

    /// <summary>
    /// <c>remove_tools</c> entries naming a tool the sub-agent holds but did not inherit from the
    /// parent — a host tool built per agent, or one the child loop registers for itself. The removal
    /// filter only ever narrows what is INHERITED, so it cannot reach these.
    /// </summary>
    public const string UnremovableTools = "unremovable_tools";

    /// <summary><c>remove_tools</c> entries that were not in the toolset to begin with (#638 F-001).</summary>
    public const string RemoveToolsWithheldNothing = "remove_tools_withheld_nothing";

    /// <summary><c>remove_tools</c> entries the mode's required-tools union put straight back (#623).</summary>
    public const string RemoveToolsRestoredByPolicy = "remove_tools_restored_by_policy";

    /// <summary>The filters together left the sub-agent no inherited tool at all.</summary>
    public const string EmptyInheritedToolset = "empty_inherited_toolset";

    /// <summary>
    /// The <c>add_tools</c> grammar, stated once and used by BOTH the warning line and the receipt's
    /// next action so a caller reading either is told the same thing.
    /// </summary>
    internal const string AddToolsGrammar = "add_tools matches exact tool names, or '*' for every tool the parent has.";

    /// <summary>The <c>remove_tools</c> grammar. Shared for the same reason as <see cref="AddToolsGrammar"/>.</summary>
    internal const string RemoveToolsGrammar =
        "remove_tools matches exact tool names and has NO wildcard or group language, so an entry like "
        + "'*' or 'tasks:*' withholds nothing - name each tool to remove it.";

    internal const string UnremovableNextAction =
        "The sub-agent is given these tools directly rather than inheriting them, so remove_tools cannot "
        + "withhold them; spawn from a template that does not carry them if it must not have them.";

    internal const string RestoredNextAction =
        "This mode requires every sub-agent to carry these tools, so the removal was undone; the "
        + "sub-agent still has them.";

    internal const string EmptyToolsetNextAction =
        "This sub-agent inherited no parent tool at all; widen the template's tools list or add_tools, "
        + "or do the work without delegating.";
}

/// <summary>
/// What a spawned sub-agent can actually do, resolved ONCE at spawn time and used for both the
/// operator warning lines and the spawn receipt handed back to the dispatching model (#671).
/// </summary>
/// <remarks>
/// <para>
/// The defect this exists for: a receipt of <c>{agent_id, name, template, status}</c> tells a
/// dispatcher nothing about the delegate's capability, so an <c>add_tools</c> that matched nothing —
/// or a template that resolved its whole toolset away — produced a delegate that could not do the
/// work, with the mismatch visible only in a log line nobody reads mid-conversation. The obligation
/// then became silent work debt: the sender believed it had delegated.
/// </para>
/// <para>
/// One record backs both consumers deliberately (#641's "one resolution, one description" rule): a
/// diagnostic and a receipt computed independently can disagree, and the disagreement is invisible.
/// </para>
/// <para>
/// Every classification is plain tool-name set membership. Nothing here inspects the dispatch prompt,
/// a template marker, or a tool's purpose, so it stays correct for any host's tool vocabulary.
/// </para>
/// </remarks>
/// <param name="Tools">The sub-agent's effective tool names, ordered for a stable receipt.</param>
/// <param name="ToolsSource">
/// <see cref="RegisteredSource"/> when <paramref name="Tools"/> is the child loop's registered
/// surface, <see cref="ProjectedSource"/> when the spawn is still queued and no loop exists yet.
/// </param>
/// <param name="UnmatchedAddTools">See <see cref="SpawnCapabilityCodes.UnmatchedAddTools"/>.</param>
/// <param name="UnremovableTools">See <see cref="SpawnCapabilityCodes.UnremovableTools"/>.</param>
/// <param name="RemoveToolsWithheldNothing">See <see cref="SpawnCapabilityCodes.RemoveToolsWithheldNothing"/>.</param>
/// <param name="RestoredByRequiredTools">See <see cref="SpawnCapabilityCodes.RemoveToolsRestoredByPolicy"/>.</param>
/// <param name="EmptyInheritedToolset">See <see cref="SpawnCapabilityCodes.EmptyInheritedToolset"/>.</param>
internal sealed record SpawnCapabilityRecord(
    IReadOnlyList<string> Tools,
    string ToolsSource,
    IReadOnlyList<string> UnmatchedAddTools,
    IReadOnlyList<string> UnremovableTools,
    IReadOnlyList<string> RemoveToolsWithheldNothing,
    IReadOnlyList<string> RestoredByRequiredTools,
    bool EmptyInheritedToolset
)
{
    /// <summary><see cref="Tools"/> came from the child loop's own registered handlers.</summary>
    internal const string RegisteredSource = "registered";

    /// <summary>
    /// <see cref="Tools"/> is what the filters resolve to; the loop does not exist yet. A queued spawn
    /// reporting an EMPTY toolset instead would read as "this delegate has no tools", which is a
    /// different — and false — statement from "not built yet".
    /// </summary>
    internal const string ProjectedSource = "projected";

    /// <summary>True when at least one requested capability did not land as the caller wrote it.</summary>
    internal bool HasMismatch =>
        UnmatchedAddTools.Count > 0
        || UnremovableTools.Count > 0
        || RemoveToolsWithheldNothing.Count > 0
        || RestoredByRequiredTools.Count > 0
        || EmptyInheritedToolset;

    /// <summary>
    /// The next valid action for each mismatch present, or null when there is none. A code names what
    /// went wrong; this names what to do instead, which is the half a caller can act on.
    /// </summary>
    internal string? NextAction
    {
        get
        {
            if (!HasMismatch)
            {
                return null;
            }

            List<string> parts = [];
            if (UnmatchedAddTools.Count > 0)
            {
                parts.Add(SpawnCapabilityCodes.AddToolsGrammar);
            }

            if (RemoveToolsWithheldNothing.Count > 0)
            {
                parts.Add(SpawnCapabilityCodes.RemoveToolsGrammar);
            }

            if (UnremovableTools.Count > 0)
            {
                parts.Add(SpawnCapabilityCodes.UnremovableNextAction);
            }

            if (RestoredByRequiredTools.Count > 0)
            {
                parts.Add(SpawnCapabilityCodes.RestoredNextAction);
            }

            if (EmptyInheritedToolset)
            {
                parts.Add(SpawnCapabilityCodes.EmptyToolsetNextAction);
            }

            return string.Join(" ", parts);
        }
    }
}
