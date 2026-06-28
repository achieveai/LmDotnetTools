using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>
///     Renders the controller-facing projection of runtime state as a <see cref="JsonObject"/>. This is a
///     PURE, read-only function of its <see cref="ProjectionInputs"/>: the runtime composes the
///     next-expected-action unit(s) first (that side-effecting step stays with the runtime/task coordinator),
///     captures the read state under its lock, and hands everything here to be rendered. Holding no state and
///     taking no lock makes the rendering trivially unit-testable in isolation.
/// </summary>
internal static class WorkflowProjectionBuilder
{
    /// <summary>
    ///     Builds the projection object in the exact key order the runtime emitted previously: the always-on
    ///     header (current node / completion / step / visits / task statuses / next expected action), then the
    ///     active-node surfaces (procedural join + onFailure, or conditional routing), the safety-rail surfaces
    ///     (visit ceiling, step budget), per-task errors, any requested channel includes, and the unmatched
    ///     diagnostics.
    /// </summary>
    public static JsonObject Build(ProjectionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var result = new JsonObject
        {
            ["currentNodeId"] = inputs.CurrentNodeId,
            ["isComplete"] = inputs.IsComplete,
            ["step"] = inputs.Step,
            ["visits"] = VisitsToJson(inputs.Visits),
            ["tasks"] = StatusToJson(inputs.Statuses),
            ["nextExpectedAction"] = NextActionsToJson(inputs.NextActions),
        };

        // For the active procedural node, surface the join progress (a SURFACE for the controller — the
        // runtime never auto-advances) and, if a unit has terminally failed, the node's onFailure route.
        if (inputs.ActiveNode is ProceduralNode procedural)
        {
            result["join"] = BuildJoin(procedural, inputs.ActiveUnits, inputs.Statuses);
            if (OnFailureRoute(procedural, inputs.ActiveUnits, inputs.Statuses) is { } route)
            {
                result["onFailure"] = route;
            }
        }

        // For the active conditional node, surface the deterministic recommended branch plus a per-branch
        // evaluation view (read-only hints; AdvanceTo still accepts any declared edge).
        if (inputs.ActiveNode is ConditionalNode conditional)
        {
            AddConditionalRouting(result, conditional, inputs.ContextFactory);
        }

        // Safety-rail surfaces for the active node: a reached visit ceiling (route to onMaxVisits) and a
        // global step-budget exhaustion (route to onBudgetExhausted).
        AddVisitCeiling(result, inputs);
        AddBudgetSurface(result, inputs);

        // Surface per-unit validation/parse errors so the controller can re-author a re-surfaced unit.
        if (inputs.LastErrors.Count > 0)
        {
            var errors = new JsonObject();
            foreach (var (name, error) in inputs.LastErrors)
            {
                errors[name] = error;
            }

            result["taskErrors"] = errors;
        }

        if (IncludesChannel(inputs.Projection, "state"))
        {
            result["state"] = inputs.State.DeepClone();
        }

        if (IncludesChannel(inputs.Projection, "outputs"))
        {
            result["outputs"] = inputs.Outputs.DeepClone();
        }

        if (IncludesChannel(inputs.Projection, "notes"))
        {
            result["notes"] = inputs.Notes.DeepClone();
        }

        if (inputs.Unmatched.Count > 0)
        {
            var unmatched = new JsonArray();
            foreach (var id in inputs.Unmatched)
            {
                unmatched.Add(id);
            }

            result["unmatched"] = unmatched;
        }

        return result;
    }

    private static JsonObject VisitsToJson(IReadOnlyDictionary<string, int> visits)
    {
        var json = new JsonObject();
        foreach (var (key, value) in visits)
        {
            json[key] = value;
        }

        return json;
    }

    private static JsonObject StatusToJson(IReadOnlyDictionary<string, WorkflowTaskStatus> statuses)
    {
        var json = new JsonObject();
        foreach (var (key, value) in statuses)
        {
            json[key] = Wire(value);
        }

        return json;
    }

    private static JsonArray NextActionsToJson(IReadOnlyList<SpawnUnit> units)
    {
        var array = new JsonArray();
        foreach (var unit in units)
        {
            array.Add(
                new JsonObject
                {
                    ["name"] = unit.Name,
                    ["subagentType"] = unit.SubagentType,
                    ["prompt"] = unit.Prompt,
                    ["outputSchema"] = unit.OutputSchema?.DeepClone(),
                }
            );
        }

        return array;
    }

    /// <summary>
    ///     Computes the controller-facing join surface for the active node: counts by status plus a
    ///     <c>satisfied</c> flag (mode <c>all</c> → every unit validated; mode <c>any</c> → at least one).
    /// </summary>
    private static JsonObject BuildJoin(
        ProceduralNode node,
        IReadOnlyList<ProjectionActiveUnit> units,
        IReadOnlyDictionary<string, WorkflowTaskStatus> statuses
    )
    {
        var validated = units.Count(u => StatusOf(statuses, u.Name) == WorkflowTaskStatus.Validated);
        var failed = units.Count(u => StatusOf(statuses, u.Name) == WorkflowTaskStatus.Failed);
        var inFlight = units.Count(u => StatusOf(statuses, u.Name) == WorkflowTaskStatus.InFlight);

        var mode = node.JoinPolicy?.Mode ?? JoinMode.All;
        var total = units.Count;

        // An all-join is satisfied once every COMPOSED unit validated — including the vacuous total == 0
        // case: a node whose working set is empty (a forEach over an empty array, or a procedural node whose
        // TaskList yields no spawnable units) has nothing to spawn and is therefore vacuously satisfied, so
        // the controller can route on instead of livelocking until the budget rail trips. This is only ever
        // evaluated AFTER compose has run for the node — the runtime composes before rendering — so a
        // not-yet-composed node is never falsely reported satisfied.
        var satisfied = mode == JoinMode.Any ? validated >= 1 : validated == total;

        return new JsonObject
        {
            ["mode"] = mode == JoinMode.Any ? "any" : "all",
            ["total"] = total,
            ["validated"] = validated,
            ["failed"] = failed,
            ["inFlight"] = inFlight,
            ["satisfied"] = satisfied,
        };
    }

    /// <summary>
    ///     The node id the controller should route to when a unit of the active node has terminally failed:
    ///     the failed task's <see cref="ProjectionActiveUnit.OnFailure"/> if set, else the node's
    ///     <see cref="ProceduralNode.OnFailure"/>; <c>null</c> when nothing has failed or no route exists.
    /// </summary>
    private static string? OnFailureRoute(
        ProceduralNode node,
        IReadOnlyList<ProjectionActiveUnit> units,
        IReadOnlyDictionary<string, WorkflowTaskStatus> statuses
    )
    {
        var failed = units.FirstOrDefault(u => StatusOf(statuses, u.Name) == WorkflowTaskStatus.Failed);
        if (failed is null)
        {
            return null;
        }

        var route = failed.OnFailure ?? node.OnFailure;
        return string.IsNullOrEmpty(route) ? null : route;
    }

    /// <summary>
    ///     Surfaces the deterministic recommended branch for an active conditional node: the first branch
    ///     whose structured condition holds (prose <c>when</c> branches have no structured form and are
    ///     skipped), else the <c>else</c> target. A per-branch <c>{ to, matched }</c> view is surfaced too.
    /// </summary>
    private static void AddConditionalRouting(
        JsonObject result,
        ConditionalNode conditional,
        Func<BindingContext> contextFactory
    )
    {
        var context = contextFactory();
        var evaluations = new JsonArray();
        string? recommended = null;
        foreach (var branch in conditional.Branches)
        {
            var matched =
                branch.StructuredCondition is { } condition
                && ConditionEvaluator.Evaluate(condition, context);
            evaluations.Add(new JsonObject { ["to"] = branch.To, ["matched"] = matched });
            recommended ??= matched ? branch.To : null;
        }

        result["recommendedBranch"] = recommended ?? conditional.Else;
        result["branchEvaluations"] = evaluations;
    }

    /// <summary>
    ///     Surfaces <c>atVisitCeiling</c>/<c>onMaxVisits</c> when the active node has been entered its declared
    ///     <c>maxVisits</c> times, so the controller routes to its <c>onMaxVisits</c> instead of re-entering
    ///     it. The node's <c>maxVisits</c>/<c>onMaxVisits</c> are pre-resolved by the runtime (single home for
    ///     the node-policy lookup, shared with <c>AdvanceTo</c>).
    /// </summary>
    private static void AddVisitCeiling(JsonObject result, ProjectionInputs inputs)
    {
        if (inputs.CurrentNodeId is not { } nodeId)
        {
            return;
        }

        var entered = inputs.Visits.TryGetValue(nodeId, out var count) ? count : 0;
        if (inputs.ActiveNodeMaxVisits is { } maxVisits && entered >= maxVisits)
        {
            result["atVisitCeiling"] = true;
            if (inputs.ActiveNodeOnMaxVisits is { } onMaxVisits && !string.IsNullOrEmpty(onMaxVisits))
            {
                result["onMaxVisits"] = onMaxVisits;
            }
        }
    }

    /// <summary>
    ///     Surfaces <c>budgetExhausted</c>/<c>onBudgetExhausted</c> when the global step counter has reached
    ///     the definition's <c>maxStepBudget</c>, so the controller routes to the escape.
    /// </summary>
    private static void AddBudgetSurface(JsonObject result, ProjectionInputs inputs)
    {
        if (inputs.Definition is { } definition && inputs.Step >= definition.MaxStepBudget)
        {
            result["budgetExhausted"] = true;
            if (!string.IsNullOrEmpty(definition.OnBudgetExhausted))
            {
                result["onBudgetExhausted"] = definition.OnBudgetExhausted;
            }
        }
    }

    private static WorkflowTaskStatus StatusOf(
        IReadOnlyDictionary<string, WorkflowTaskStatus> statuses,
        string unitName
    ) => statuses.TryGetValue(unitName, out var status) ? status : WorkflowTaskStatus.Pending;

    private static bool IncludesChannel(string? projection, string channel) =>
        projection is not null
        && (
            projection.Contains("all", StringComparison.OrdinalIgnoreCase)
            || projection.Contains("full", StringComparison.OrdinalIgnoreCase)
            || projection.Contains(channel, StringComparison.OrdinalIgnoreCase)
        );

    private static string Wire(WorkflowTaskStatus status) =>
        status switch
        {
            WorkflowTaskStatus.Pending => "pending",
            WorkflowTaskStatus.InFlight => "in_flight",
            WorkflowTaskStatus.Validated => "validated",
            WorkflowTaskStatus.Failed => "failed",
            _ => status.ToString().ToLowerInvariant(),
        };
}

/// <summary>
///     The read-only snapshot of runtime + task state that <see cref="WorkflowProjectionBuilder.Build"/>
///     renders. The runtime assembles this under its lock (composing the next-expected-action units first),
///     so the builder itself never touches live state or the lock.
/// </summary>
internal sealed record ProjectionInputs
{
    /// <summary>The id of the node the controller is currently positioned on.</summary>
    public string? CurrentNodeId { get; init; }

    /// <summary>Whether the workflow has advanced into a terminal node.</summary>
    public bool IsComplete { get; init; }

    /// <summary>The global controller step counter.</summary>
    public int Step { get; init; }

    /// <summary>The loaded definition (for the step-budget surface), or <c>null</c> before load.</summary>
    public WorkflowDefinition? Definition { get; init; }

    /// <summary>The resolved active node (the node at <see cref="CurrentNodeId"/>), or <c>null</c>.</summary>
    public WorkflowNode? ActiveNode { get; init; }

    /// <summary>The active node's visit ceiling (pre-resolved by the runtime), or <c>null</c> when unbounded.</summary>
    public int? ActiveNodeMaxVisits { get; init; }

    /// <summary>The active node's <c>onMaxVisits</c> route (pre-resolved by the runtime), or <c>null</c>.</summary>
    public string? ActiveNodeOnMaxVisits { get; init; }

    /// <summary>A snapshot of the per-node visit counts.</summary>
    public required IReadOnlyDictionary<string, int> Visits { get; init; }

    /// <summary>The per-unit lifecycle statuses, keyed by unit name.</summary>
    public required IReadOnlyDictionary<string, WorkflowTaskStatus> Statuses { get; init; }

    /// <summary>The already-composed ready-to-spawn unit(s) for the active node.</summary>
    public required IReadOnlyList<SpawnUnit> NextActions { get; init; }

    /// <summary>The active node's working set (used for the procedural join / onFailure surfaces).</summary>
    public required IReadOnlyList<ProjectionActiveUnit> ActiveUnits { get; init; }

    /// <summary>Builds a binding context for evaluating conditional branches (invoked at most once).</summary>
    public required Func<BindingContext> ContextFactory { get; init; }

    /// <summary>The most recent failure reason per unit, surfaced as <c>taskErrors</c>.</summary>
    public required IReadOnlyDictionary<string, string> LastErrors { get; init; }

    /// <summary>The bounded list of uncorrelated tool-call / agent ids, surfaced as <c>unmatched</c>.</summary>
    public required IReadOnlyList<string> Unmatched { get; init; }

    /// <summary>The live state channel (cloned by the builder only when the projection includes it).</summary>
    public required JsonObject State { get; init; }

    /// <summary>The live outputs channel (cloned by the builder only when the projection includes it).</summary>
    public required JsonObject Outputs { get; init; }

    /// <summary>The live notes channel (cloned by the builder only when the projection includes it).</summary>
    public required JsonObject Notes { get; init; }

    /// <summary>The projection selector controlling which channels are included, or <c>null</c>.</summary>
    public string? Projection { get; init; }
}

/// <summary>
///     A minimal read-only view of one active task occurrence the projection needs for the join / onFailure
///     surfaces: its correlation <see cref="Name"/> (used to look up its status) and per-task
///     <see cref="OnFailure"/> route. Deliberately decoupled from the coordinator's richer internal record.
/// </summary>
internal sealed record ProjectionActiveUnit
{
    /// <summary>The unit correlation name, formatted <c>nodeId:visit:taskId[:index]</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The per-task failure route, if any.</summary>
    public string? OnFailure { get; init; }
}
