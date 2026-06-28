using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tools;

/// <summary>
///     Renders the controller-facing runtime projection (the <see cref="JsonObject"/> produced by
///     <c>WorkflowRuntime.GetProjection</c>) into a compact, human-readable summary. It is a read-only
///     view used by <see cref="WorkflowToolProvider"/>'s <c>GetWorkflow</c> tool when the controller asks
///     for a <c>prose</c>/<c>text</c> projection; it never mutates the projection and tolerates a sparse
///     one (any surface that is absent is simply omitted).
/// </summary>
internal static class WorkflowProseRenderer
{
    /// <summary>Renders <paramref name="projection"/> into a multi-line prose summary.</summary>
    public static string Render(JsonObject projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var lines = new List<string>();
        AppendHeader(projection, lines);
        AppendNextActions(projection, lines);
        AppendJoin(projection, lines);
        AppendRecommendedBranch(projection, lines);
        AppendRails(projection, lines);
        AppendTaskErrors(projection, lines);
        return string.Join("\n", lines);
    }

    /// <summary>The current node, completion state and step counter.</summary>
    private static void AppendHeader(JsonObject projection, List<string> lines)
    {
        var node = StringOf(projection["currentNodeId"]) ?? "(none)";
        var step = IntOf(projection, "step");
        lines.Add(
            BoolOf(projection, "isComplete")
                ? $"Workflow COMPLETE at node '{node}' (step {step})."
                : $"At node '{node}' (step {step})."
        );
    }

    /// <summary>The ready-to-spawn unit(s) for the active node, listed by name + sub-agent type.</summary>
    private static void AppendNextActions(JsonObject projection, List<string> lines)
    {
        if (projection["nextExpectedAction"] is not JsonArray actions || actions.Count == 0)
        {
            if (!BoolOf(projection, "isComplete"))
            {
                lines.Add("No ready-to-spawn units; decide the next node with SetCurrentNode.");
            }

            return;
        }

        var units = actions
            .OfType<JsonObject>()
            .Select(a =>
            {
                var name = StringOf(a["name"]) ?? "(unnamed)";
                var subagent = StringOf(a["subagentType"]);
                return subagent is null ? name : $"{name} [{subagent}]";
            });
        lines.Add($"Ready to spawn {actions.Count} unit(s): {string.Join(", ", units)}.");
    }

    /// <summary>The join progress surface for an active procedural node.</summary>
    private static void AppendJoin(JsonObject projection, List<string> lines)
    {
        if (projection["join"] is not JsonObject join)
        {
            return;
        }

        var mode = StringOf(join["mode"]) ?? "all";
        var satisfied = BoolOf(join, "satisfied");
        lines.Add(
            $"Join ({mode}): {IntOf(join, "validated")}/{IntOf(join, "total")} validated, "
                + $"{IntOf(join, "inFlight")} in-flight, {IntOf(join, "failed")} failed — "
                + (satisfied ? "satisfied; route onward." : "not yet satisfied; keep polling.")
        );

        if (StringOf(projection["onFailure"]) is { } onFailure)
        {
            lines.Add($"A task failed; onFailure route available: '{onFailure}'.");
        }
    }

    /// <summary>The deterministic recommended branch for an active conditional node.</summary>
    private static void AppendRecommendedBranch(JsonObject projection, List<string> lines)
    {
        if (StringOf(projection["recommendedBranch"]) is { Length: > 0 } branch)
        {
            lines.Add($"Recommended branch: '{branch}' (route there with SetCurrentNode).");
        }
    }

    /// <summary>The active safety rails (visit ceiling / step budget) and their escape targets.</summary>
    private static void AppendRails(JsonObject projection, List<string> lines)
    {
        if (BoolOf(projection, "atVisitCeiling"))
        {
            lines.Add(RailLine("Visit ceiling reached", StringOf(projection["onMaxVisits"]), "onMaxVisits"));
        }

        if (BoolOf(projection, "budgetExhausted"))
        {
            lines.Add(
                RailLine("Step budget exhausted", StringOf(projection["onBudgetExhausted"]), "onBudgetExhausted")
            );
        }
    }

    /// <summary>Per-unit validation/parse errors the controller should re-author against.</summary>
    private static void AppendTaskErrors(JsonObject projection, List<string> lines)
    {
        if (projection["taskErrors"] is JsonObject errors && errors.Count > 0)
        {
            lines.Add($"Task errors on: {string.Join(", ", errors.Select(e => e.Key))} (re-spawn to retry).");
        }
    }

    private static string RailLine(string what, string? target, string routeName) =>
        target is null
            ? $"{what}; no {routeName} target is defined."
            : $"{what}; route to {routeName} '{target}'.";

    private static string? StringOf(JsonNode? node) => node?.GetValue<string>();

    private static bool BoolOf(JsonObject obj, string key) => obj[key]?.GetValue<bool>() ?? false;

    private static int IntOf(JsonObject obj, string key) => obj[key]?.GetValue<int>() ?? 0;
}
