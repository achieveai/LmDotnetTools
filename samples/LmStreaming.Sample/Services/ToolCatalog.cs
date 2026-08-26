using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>Builds the list of tools the Modes editor can offer.</summary>
public interface IToolCatalog
{
    /// <summary>
    ///     Every selectable tool, grouped. Includes a synthetic <c>group:*</c> wildcard row for each
    ///     qualified group.
    /// </summary>
    Task<IReadOnlyList<ToolDefinition>> GetAsync(CancellationToken ct = default);
}

/// <summary>
///     Assembles the selectable tool catalog from the places tools actually come from.
/// </summary>
/// <remarks>
///     <para>
///         Before this existed, <c>/api/tools</c> returned only the server-side built-ins plus the
///         singleton <see cref="FunctionRegistry" /> — which holds nothing but the sample's demo
///         tools. Everything else a conversation gets (the per-conversation task list, the sandbox
///         gateway's file/shell tools, the sub-agent tools, the workflow tools, the Jina web
///         fallbacks) is wired inside the agent factory at construction time and so never reached the
///         editor. That is why cloning Workspace Agent showed none of its actual tools.
///     </para>
///     <para>
///         Each group is enumerated from the same source the runtime wiring uses — the provider's own
///         <c>ToolNames</c> constants, a scratch registry over the real <c>TaskManager</c>, the
///         gateway's live <c>tools/list</c> — so the editor cannot drift from what a conversation
///         will actually receive.
///     </para>
/// </remarks>
public sealed class ToolCatalog(
    FunctionRegistry sampleRegistry,
    IReadOnlyList<ToolDefinition> builtInToolDefinitions,
    ISandboxToolCatalogProbe sandboxProbe,
    TimeProvider timeProvider) : IToolCatalog
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolDefinition>> GetAsync(CancellationToken ct = default)
    {
        var catalog = new List<ToolDefinition>();

        // 1. Server-side provider built-ins (web_search, ...). Bare ids; selected via
        //    ChatMode.EnabledBuiltInTools.
        catalog.AddRange(
            builtInToolDefinitions.Select(t =>
                t with
                {
                    Id = t.Name,
                    Group = ToolGroups.BuiltIn,
                    GroupLabel = ToolGroups.LabelFor(ToolGroups.BuiltIn),
                }
            )
        );

        // 2. The sample's demo function tools, from the shared singleton registry.
        var (sampleContracts, _) = sampleRegistry.Build();
        catalog.AddRange(
            sampleContracts.Select(c => Bare(ToolGroups.Sample, c.Name, c.Description))
        );

        // 3. The per-conversation todo list. Enumerated from a throwaway TaskManager rather than a
        //    hand-written name list, so adding a task tool cannot silently skip the editor.
        var taskRegistry = new FunctionRegistry();
        _ = taskRegistry.AddFunctionsFromObject(new TaskManager(), providerName: "TaskManager");
        var (taskContracts, _) = taskRegistry.Build();
        catalog.AddRange(
            taskContracts.Select(c => Bare(ToolGroups.Tasks, c.Name, c.Description))
        );

        // 4. Jina web fallbacks. Bare ids — Research Assistant has always listed these in
        //    EnabledTools, and WebToolRegistrationPolicy still reads them from there.
        catalog.Add(
            Bare(ToolGroups.Web, WebSearchTool.ToolName, "Search the web (fallback for providers with no native web search).")
        );
        catalog.Add(
            Bare(ToolGroups.Web, WebFetchTool.ToolName, "Fetch and read a web page as text.")
        );

        // 5. Sub-agents. The union of both surface shapes — a conversation sees the legacy set or the
        //    collaboration set, never all of these at once (see SubAgentToolProvider.AllToolNames).
        catalog.Add(Wildcard(ToolGroups.SubAgents, "All sub-agent tools", requiresSandbox: false));
        catalog.AddRange(
            SubAgentToolProvider.AllToolNames.Select(name =>
                Qualified(ToolGroups.SubAgents, name, SubAgentToolDescription(name), requiresSandbox: false)
                with
                {
                    // The legacy surface, marked so the editor can pre-select exactly what a mode
                    // with no capability selection already gets. Derived from
                    // ModeCapabilities.LegacyDefaults (SubAgents on, Collaboration off) rather than
                    // from a second hand-written name list, so the two cannot drift apart.
                    IsLegacyDefault =
                        ModeCapabilities.LegacyDefaults.SubAgents
                        && !ModeCapabilities.CollaborationToolNames.Contains(name, StringComparer.Ordinal),
                }
            )
        );

        // 6. Workflow: the authoring/mutation surface and the launch family.
        catalog.Add(Wildcard(ToolGroups.Workflow, "All workflow tools", requiresSandbox: false));
        catalog.AddRange(
            WorkflowToolProvider.AllToolNames.Select(name =>
                Qualified(
                    ToolGroups.Workflow,
                    name,
                    "Author or drive a workflow graph directly in this conversation.",
                    requiresSandbox: false
                )
            )
        );
        catalog.AddRange(
            StartWorkflowToolProvider.ToolNames.Select(name =>
                Qualified(
                    ToolGroups.Workflow,
                    name,
                    "Launch and track workflows that run in their own controller loop.",
                    requiresSandbox: false
                )
            )
        );

        // 7. Sandbox/workspace tools, listed live from the gateway when it is reachable. The wildcard
        //    row goes first and is the only entry that can cover marketplace-provided tools installed
        //    after this listing was taken.
        var sandbox = await sandboxProbe.GetAsync(timeProvider, ct).ConfigureAwait(false);
        catalog.Add(
            Wildcard(ToolGroups.Sandbox, "All workspace tools", requiresSandbox: true) with
            {
                Description = "Every file and shell tool the workspace sandbox offers, including "
                    + "tools added later by marketplace plugins.",
                CatalogWarning = sandbox.Warning,
            }
        );
        catalog.AddRange(
            sandbox.Tools.Select(t =>
                Qualified(ToolGroups.Sandbox, t.Name, t.Description, requiresSandbox: true) with
                {
                    CatalogWarning = sandbox.Warning,
                }
            )
        );

        return catalog;
    }

    private static ToolDefinition Bare(string group, string name, string? description) =>
        new()
        {
            Name = name,
            Id = name,
            Description = description,
            Group = group,
            GroupLabel = ToolGroups.LabelFor(group),
        };

    private static ToolDefinition Qualified(
        string group,
        string name,
        string? description,
        bool requiresSandbox) =>
        new()
        {
            Name = name,
            Id = ToolGroups.Qualify(group, name),
            Description = description,
            Group = group,
            GroupLabel = ToolGroups.LabelFor(group),
            RequiresSandbox = requiresSandbox,
        };

    private static ToolDefinition Wildcard(string group, string displayName, bool requiresSandbox) =>
        new()
        {
            Name = displayName,
            Id = ToolGroups.Wildcard(group),
            Description = "Selects every tool in this group, including ones added later.",
            Group = group,
            GroupLabel = ToolGroups.LabelFor(group),
            IsWildcard = true,
            RequiresSandbox = requiresSandbox,
        };

    private static string SubAgentToolDescription(string name) =>
        name switch
        {
            SubAgentToolProvider.SpawnToolName => "Start a sub-agent to work on a task.",
            SubAgentToolProvider.SendMessageToolName => "Send a message to another agent.",
            SubAgentToolProvider.CheckAgentToolName => "Check one sub-agent's status without blocking.",
            SubAgentToolProvider.WaitAgentToolName =>
                "Block until one sub-agent finishes. Offered only when the collaboration tools are OFF.",
            SubAgentToolProvider.CheckAgentsToolName =>
                "Check a whole fan-out at once. Selecting this (or any collaboration tool) turns on the "
                    + "hierarchy-wide collaboration surface.",
            SubAgentToolProvider.WaitForAgentsToolName =>
                "Block until a fan-out finishes. Turns on the collaboration surface.",
            SubAgentToolProvider.GetAgentsToolName =>
                "List the agents in this collaboration. Turns on the collaboration surface.",
            _ => "Sub-agent delegation tool.",
        };
}
