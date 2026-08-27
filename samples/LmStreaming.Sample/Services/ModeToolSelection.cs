namespace LmStreaming.Sample.Services;

/// <summary>
///     A parsed view of a mode's <c>EnabledCapabilityTools</c> list — the qualified
///     <c>sandbox:</c>/<c>subagents:</c>/<c>workflow:</c> selections.
/// </summary>
/// <remarks>
///     <para>
///         <b>Null is not empty.</b> A null list means the mode predates capability selection (or has
///         never been saved through the Modes editor), and must keep the behaviour it had before this
///         field existed. An empty list means the user opened the editor and unchecked everything —
///         an explicit "none". Collapsing the two would silently strip sub-agents from every mode that
///         already exists, so <see cref="IsLegacy" /> keeps them apart and every consumer must branch
///         on it.
///     </para>
/// </remarks>
public sealed class ModeToolSelection
{
    private readonly Dictionary<string, HashSet<string>> _byGroup;
    private readonly HashSet<string> _wildcardGroups;

    private ModeToolSelection(
        bool isLegacy,
        Dictionary<string, HashSet<string>> byGroup,
        HashSet<string> wildcardGroups)
    {
        IsLegacy = isLegacy;
        _byGroup = byGroup;
        _wildcardGroups = wildcardGroups;
    }

    /// <summary>
    ///     True when the mode declared no capability selection at all, so callers must fall back to
    ///     the pre-capability defaults rather than reading this as "nothing selected".
    /// </summary>
    public bool IsLegacy { get; }

    /// <summary>The legacy selection: no explicit choice recorded.</summary>
    public static ModeToolSelection Legacy { get; } = new(true, [], []);

    /// <summary>
    ///     Parses <paramref name="capabilityTools" />. Bare (unqualified) entries are ignored — they
    ///     belong to <c>EnabledTools</c> and are filtered elsewhere — so a caller that accidentally
    ///     hands over the wrong list gets an explicit empty selection rather than silent garbage.
    /// </summary>
    public static ModeToolSelection Parse(IReadOnlyList<string>? capabilityTools)
    {
        if (capabilityTools is null)
        {
            return Legacy;
        }

        var byGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);

        foreach (var id in capabilityTools)
        {
            if (!ToolGroups.TrySplit(id, out var group, out var toolName))
            {
                continue;
            }

            if (string.Equals(toolName, ToolGroups.WildcardTool, StringComparison.Ordinal))
            {
                _ = wildcards.Add(group);
                continue;
            }

            if (!byGroup.TryGetValue(group, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                byGroup[group] = names;
            }

            _ = names.Add(toolName);
        }

        return new ModeToolSelection(isLegacy: false, byGroup, wildcards);
    }

    /// <summary>Whether <paramref name="group" /> was selected wholesale via <c>group:*</c>.</summary>
    public bool HasWildcard(string group) => _wildcardGroups.Contains(group);

    /// <summary>
    ///     Whether the mode asked for anything at all from <paramref name="group" /> — a wildcard or at
    ///     least one named tool.
    /// </summary>
    public bool IsEnabled(string group) =>
        HasWildcard(group) || (_byGroup.TryGetValue(group, out var names) && names.Count > 0);

    /// <summary>
    ///     The allow-list for <paramref name="group" />: <c>null</c> when the whole group was selected
    ///     (so no filtering should be applied, and tools added later — e.g. by a marketplace plugin —
    ///     still flow through), otherwise the explicitly chosen bare tool names.
    /// </summary>
    public IReadOnlySet<string>? AllowListFor(string group)
    {
        if (HasWildcard(group))
        {
            return null;
        }

        return _byGroup.TryGetValue(group, out var names)
            ? names
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Whether any of <paramref name="toolNames" /> is selected in <paramref name="group" /> —
    ///     used where a capability turns on for a SUBSET of a group (e.g. the collaboration tools
    ///     inside <c>subagents</c>, or the launch tools inside <c>workflow</c>).
    /// </summary>
    public bool AnySelected(string group, IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        if (HasWildcard(group))
        {
            return true;
        }

        return _byGroup.TryGetValue(group, out var names) && toolNames.Any(names.Contains);
    }
}
