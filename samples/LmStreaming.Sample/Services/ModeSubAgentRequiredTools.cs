using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Resolves a mode's <c>SubAgentRequiredTools</c> patterns (#623) into the concrete bare tool
///     names <c>SubAgentOptions.RequiredToolNames</c> consumes.
/// </summary>
/// <remarks>
///     <para>
///         The pattern language is a SUPERSET of the mode's tool-selection id language, split with
///         <see cref="ToolGroups.TrySplitAnyKnown"/>: an entry is either a <c>group:*</c> wildcard
///         (every tool of that group), a qualified <c>group:tool</c> id (the bare name, prefix
///         stripped — the model never sees prefixes), or a bare tool name passed through verbatim.
///         Superset, not the same: <c>EnabledTools</c> matches exact names only, and
///         <c>EnabledCapabilityTools</c> wildcards exist only for the qualified groups — the
///         <c>tasks:*</c>/<c>web:*</c> style expansion is specific to this field. Do not expect a
///         wildcard to work in the other fields.
///     </para>
///     <para>
///         Wildcards expand only for the groups whose membership is statically enumerable —
///         <c>tasks</c>, <c>web</c>, <c>subagents</c>, <c>workflow</c>. The dynamic groups
///         (<c>sandbox</c>, whose tools come from a live gateway; <c>sample</c>/<c>knowledge</c>/
///         <c>builtin</c>) have no static roster here, so their wildcards resolve to nothing rather
///         than to a guess; name their tools individually instead. This is a RESOLUTION step only:
///         whether a resolved name actually reaches a sub-agent is still gated by what the mode's own
///         registry exposes (<c>SubAgentManager</c> intersects with the parent's contracts), so a
///         mode can never grant what it does not have.
///     </para>
/// </remarks>
public static class ModeSubAgentRequiredTools
{
    /// <summary>
    ///     The TaskManager (todo-board) tool family's bare names. Enumerated from a throwaway
    ///     <see cref="TaskManager"/> registry rather than a hand-written list — the same rule as
    ///     <see cref="ToolCatalog"/> — so adding a task tool cannot silently skip enforcement or the
    ///     #623 missing-board-tools warning.
    /// </summary>
    public static IReadOnlyList<string> TaskToolNames => LazyTaskToolNames.Value;

    private static readonly Lazy<IReadOnlyList<string>> LazyTaskToolNames = new(() =>
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunctionsFromObject(new TaskManager(), providerName: "TaskManager");
        var (contracts, _) = registry.Build();
        return [.. contracts.Select(c => c.Name)];
    });

    /// <summary>
    ///     Resolves <paramref name="patterns"/> to distinct concrete bare tool names, in first-seen
    ///     order. Null/empty in, null out — "not enforced" stays representable as the absence the
    ///     spawn path already treats as today's behavior.
    /// </summary>
    /// <param name="patterns">The mode's stored <c>SubAgentRequiredTools</c> entries.</param>
    /// <param name="onUnresolved">
    ///     Invoked once per entry that was clearly meant as a pattern but expanded to nothing an
    ///     operator could rely on: a wildcard over a group with no static roster (<c>sandbox:*</c>),
    ///     or an unsplittable entry containing <c>:</c>/<c>*</c> (a typo'd group like <c>taks:*</c>,
    ///     which passes through as an inert name). Resolution behavior is unchanged either way —
    ///     this exists so the host can log instead of staying silent (#623 review F-004).
    /// </param>
    public static IReadOnlyList<string>? Resolve(IReadOnlyList<string>? patterns, Action<string>? onUnresolved = null)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<string>();

        void Add(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                resolved.Add(name);
            }
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            if (!ToolGroups.TrySplitAnyKnown(pattern, out var group, out var toolName))
            {
                // A bare tool name (or an unknown prefix, which is treated as part of the name so a
                // typo'd group surfaces as an unmatched — and therefore inert — name, never as a
                // silently different tool). An entry that carries pattern syntax without splitting
                // was meant as a pattern, so it is also reported as unresolved.
                var trimmed = pattern.Trim();
                if (trimmed.Contains(':', StringComparison.Ordinal) || trimmed.Contains('*', StringComparison.Ordinal))
                {
                    onUnresolved?.Invoke(trimmed);
                }

                Add(trimmed);
                continue;
            }

            if (!string.Equals(toolName, ToolGroups.WildcardTool, StringComparison.Ordinal))
            {
                Add(toolName);
                continue;
            }

            var expanded = false;
            foreach (var name in WildcardMembers(group))
            {
                Add(name);
                expanded = true;
            }

            if (!expanded)
            {
                // A wildcard over a dynamic group (no static roster here) expands to nothing; say so
                // rather than letting the entry vanish silently.
                onUnresolved?.Invoke(pattern.Trim());
            }
        }

        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>The statically enumerable membership of <paramref name="group"/>; empty otherwise.</summary>
    private static IEnumerable<string> WildcardMembers(string group) =>
        group switch
        {
            ToolGroups.Tasks => TaskToolNames,
            ToolGroups.Web => [WebSearchTool.ToolName, WebFetchTool.ToolName],
            ToolGroups.SubAgents => SubAgentToolProvider.AllToolNames,
            ToolGroups.Workflow => [.. WorkflowToolProvider.AllToolNames, .. StartWorkflowToolProvider.ToolNames],
            _ => [],
        };
}
