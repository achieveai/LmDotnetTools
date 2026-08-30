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
///         The pattern language is the mode's existing tool-id language, split with
///         <see cref="ToolGroups.TrySplitAnyKnown"/>: an entry is either a <c>group:*</c> wildcard
///         (every tool of that group), a qualified <c>group:tool</c> id (the bare name, prefix
///         stripped — the model never sees prefixes), or a bare tool name passed through verbatim.
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
    public static IReadOnlyList<string>? Resolve(IReadOnlyList<string>? patterns)
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
                // silently different tool).
                Add(pattern.Trim());
                continue;
            }

            if (!string.Equals(toolName, ToolGroups.WildcardTool, StringComparison.Ordinal))
            {
                Add(toolName);
                continue;
            }

            foreach (var name in WildcardMembers(group))
            {
                Add(name);
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
