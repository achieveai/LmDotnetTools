using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Ingest;

/// <summary>
///     Validates a <see cref="WorkflowDefinition"/> against the narrow V1 rule set. Every rule is checked
///     and all errors are accumulated (the validator never stops at the first failure) so an authoring
///     LLM can fix everything in a single pass.
/// </summary>
public sealed class WorkflowValidator
{
    private const string StatePrefix = "state.";
    private const string DefsRefPrefix = "#/$defs/";
    private const string WorkflowSource = "workflow";

    /// <summary>Validates the definition and returns the collected result.</summary>
    public ValidationResult Validate(WorkflowDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        var errors = new List<string>();
        var nodes = def.Nodes ?? [];

        ValidateNodeIds(nodes, errors);
        ValidateStartNode(nodes, errors);
        ValidateTerminalExists(nodes, errors);
        ValidateNodeStructure(nodes, errors);
        ValidateTaskIds(nodes, errors);
        ValidateV1Restrictions(nodes, errors);
        ValidateAgentTasks(nodes, errors);
        ValidateWrites(nodes, errors);
        ValidateBudgets(def, nodes, errors);
        ValidateRefs(def, nodes, errors);
        ValidateConditionOps(nodes, errors);
        ValidateEdgesAndReachability(def, nodes, errors);

        return new ValidationResult(errors.Count == 0, errors);
    }

    /// <summary>Validates the definition and throws when invalid.</summary>
    /// <exception cref="WorkflowValidationException">The definition failed one or more rules.</exception>
    public void ValidateAndThrow(WorkflowDefinition def)
    {
        var result = Validate(def);
        if (!result.IsValid)
        {
            throw new WorkflowValidationException(result.Errors);
        }
    }

    // Rule 3 (node ids): globally unique, non-empty.
    private static void ValidateNodeIds(IReadOnlyList<WorkflowNode> nodes, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrEmpty(node.Id))
            {
                errors.Add("A node is missing its 'id'.");
            }
            else if (!seen.Add(node.Id))
            {
                errors.Add($"Duplicate node id '{node.Id}'.");
            }
        }
    }

    // Rule 1: exactly one start node.
    private static void ValidateStartNode(IReadOnlyList<WorkflowNode> nodes, List<string> errors)
    {
        var count = nodes.OfType<StartNode>().Count();
        if (count != 1)
        {
            errors.Add($"Workflow must have exactly one start node, but found {count}.");
        }
    }

    // Rule 2 (existence half): at least one terminal node. Reachability is checked in rule 6.
    private static void ValidateTerminalExists(
        IReadOnlyList<WorkflowNode> nodes,
        List<string> errors
    )
    {
        if (!nodes.OfType<TerminalNode>().Any())
        {
            errors.Add("Workflow must have at least one terminal node.");
        }
    }

    // Rule 4 + Rule 5: start has exactly one next; procedural has >= 1 next; conditional has non-empty else.
    private static void ValidateNodeStructure(
        IReadOnlyList<WorkflowNode> nodes,
        List<string> errors
    )
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case StartNode start when start.Next.Count != 1:
                    errors.Add(
                        $"Start node '{start.Id}' must have exactly one 'next' target, but found {start.Next.Count}."
                    );
                    break;
                case ProceduralNode procedural when procedural.Next.Count < 1:
                    errors.Add(
                        $"Procedural node '{procedural.Id}' must have at least one 'next' target."
                    );
                    break;
                case ConditionalNode conditional when string.IsNullOrEmpty(conditional.Else):
                    errors.Add(
                        $"Conditional node '{conditional.Id}' must declare a non-empty 'else' target."
                    );
                    break;
                default:
                    break;
            }
        }
    }

    // Rule 3 (task ids): unique within a node.
    private static void ValidateTaskIds(IReadOnlyList<WorkflowNode> nodes, List<string> errors)
    {
        foreach (var procedural in nodes.OfType<ProceduralNode>())
        {
            if (procedural.TaskList is null)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in procedural.TaskList)
            {
                if (!string.IsNullOrEmpty(task.Id) && !seen.Add(task.Id))
                {
                    errors.Add($"Duplicate task id '{task.Id}' in node '{procedural.Id}'.");
                }
            }
        }
    }

    // Rule 7: V1 restrictions (reduce nodes, runtime/hybrid tasks, quorum joins, non-agent delegates, upsert writes).
    private static void ValidateV1Restrictions(
        IReadOnlyList<WorkflowNode> nodes,
        List<string> errors
    )
    {
        foreach (var node in nodes)
        {
            if (node is UnknownNode unknown)
            {
                errors.Add(
                    $"Node '{unknown.Id}' has type '{unknown.RawType}' which is not supported in V1."
                );
                continue;
            }

            if (node is not ProceduralNode procedural)
            {
                continue;
            }

            if (procedural.TasksMode != TasksMode.Authored)
            {
                errors.Add(
                    $"Procedural node '{procedural.Id}' uses tasksMode '{Wire(procedural.TasksMode)}' "
                        + "which is not supported in V1 (only 'authored')."
                );
            }

            var joinMode = procedural.JoinPolicy?.Mode ?? JoinMode.All;
            if (joinMode is not (JoinMode.All or JoinMode.Any))
            {
                errors.Add(
                    $"Procedural node '{procedural.Id}' uses joinPolicy mode '{Wire(joinMode)}' "
                        + "which is not supported in V1 (only 'all' or 'any')."
                );
            }

            foreach (var task in procedural.TaskList ?? [])
            {
                if (task.Delegate != DelegateKind.Agent)
                {
                    errors.Add(
                        $"Task '{task.Id}' in node '{procedural.Id}' uses delegate '{Wire(task.Delegate)}' "
                            + "which is not supported in V1 (only 'agent')."
                    );
                }

                if (task.Writes is { Mode: WriteMode.Upsert })
                {
                    errors.Add(
                        $"Task '{task.Id}' in node '{procedural.Id}' uses writes mode 'upsert' "
                            + "which is not supported in V1."
                    );
                }
            }
        }
    }

    // Rule 8: agent-delegated tasks must declare a non-empty subagent_type.
    private static void ValidateAgentTasks(IReadOnlyList<WorkflowNode> nodes, List<string> errors)
    {
        foreach (var procedural in nodes.OfType<ProceduralNode>())
        {
            foreach (var task in procedural.TaskList ?? [])
            {
                if (task.Delegate == DelegateKind.Agent && string.IsNullOrEmpty(task.SubagentType))
                {
                    errors.Add(
                        $"Task '{task.Id}' in node '{procedural.Id}' delegates to an agent "
                            + "but has no 'subagent_type'."
                    );
                }
            }
        }
    }

    // Rule 11 (writes): writes.to must target a 'state.' path.
    private static void ValidateWrites(IReadOnlyList<WorkflowNode> nodes, List<string> errors)
    {
        foreach (var procedural in nodes.OfType<ProceduralNode>())
        {
            foreach (var task in procedural.TaskList ?? [])
            {
                if (
                    task.Writes is { } writes
                    && !writes.To.StartsWith(StatePrefix, StringComparison.Ordinal)
                )
                {
                    errors.Add(
                        $"Task '{task.Id}' in node '{procedural.Id}' writes to '{writes.To}' "
                            + "which must start with 'state.'."
                    );
                }
            }
        }
    }

    // Rule 11 (budgets): maxStepBudget > 0; maxVisits > 0 where present.
    private static void ValidateBudgets(
        WorkflowDefinition def,
        IReadOnlyList<WorkflowNode> nodes,
        List<string> errors
    )
    {
        if (def.MaxStepBudget <= 0)
        {
            errors.Add($"maxStepBudget must be greater than 0, but was {def.MaxStepBudget}.");
        }

        foreach (var node in nodes)
        {
            var maxVisits = node switch
            {
                ProceduralNode procedural => procedural.MaxVisits,
                ConditionalNode conditional => conditional.MaxVisits,
                _ => null,
            };

            if (maxVisits is <= 0)
            {
                errors.Add(
                    $"Node '{node.Id}' maxVisits must be greater than 0, but was {maxVisits}."
                );
            }
        }
    }

    // Rule 9: every '#/$defs/<name>' reference in a schema fragment must resolve to a known definition.
    private static void ValidateRefs(
        WorkflowDefinition def,
        IReadOnlyList<WorkflowNode> nodes,
        List<string> errors
    )
    {
        var defNames = def.Defs is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(def.Defs.Select(kv => kv.Key), StringComparer.Ordinal);

        CheckRefs(def.FinalOutputSchema, "definition finalOutputSchema", defNames, errors);

        foreach (var node in nodes)
        {
            switch (node)
            {
                case TerminalNode terminal:
                    CheckRefs(
                        terminal.FinalOutputSchema,
                        $"terminal node '{terminal.Id}' finalOutputSchema",
                        defNames,
                        errors
                    );
                    break;
                case ProceduralNode procedural:
                    foreach (var task in procedural.TaskList ?? [])
                    {
                        CheckRefs(
                            task.OutputSchema,
                            $"task '{task.Id}' outputSchema",
                            defNames,
                            errors
                        );
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static void CheckRefs(
        JsonNode? schema,
        string context,
        HashSet<string> defNames,
        List<string> errors
    )
    {
        foreach (var reference in CollectRefs(schema))
        {
            if (!reference.StartsWith(DefsRefPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = reference[DefsRefPrefix.Length..];
            if (!defNames.Contains(name))
            {
                errors.Add($"Unresolved $ref '{reference}' in {context}.");
            }
        }
    }

    private static IEnumerable<string> CollectRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (
                        pair.Key == "$ref"
                        && pair.Value is JsonValue value
                        && value.TryGetValue<string>(out var reference)
                    )
                    {
                        yield return reference;
                    }
                    else
                    {
                        foreach (var nested in CollectRefs(pair.Value))
                        {
                            yield return nested;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in CollectRefs(item))
                    {
                        yield return nested;
                    }
                }

                break;
            default:
                break;
        }
    }

    // Rule 10: condition operators must be in the closed ConditionOp set, and each condition node must use
    // exactly one shape (a leaf OR a single composite) so the evaluator routes on the authored predicate.
    private static void ValidateConditionOps(IReadOnlyList<WorkflowNode> nodes, List<string> errors)
    {
        foreach (var conditional in nodes.OfType<ConditionalNode>())
        {
            foreach (var branch in conditional.Branches ?? [])
            {
                ValidateCondition(branch.StructuredCondition, conditional.Id, errors);
            }
        }
    }

    private static void ValidateCondition(Condition? condition, string nodeId, List<string> errors)
    {
        if (condition is null)
        {
            return;
        }

        if (condition.UnknownOp is { } unknown)
        {
            errors.Add($"Conditional node '{nodeId}' uses unknown condition op '{unknown}'.");
        }

        // Shape check: a condition is exactly one of a leaf (op/path) or a single composite (all/any/not).
        // The evaluator checks composites first and silently drops a co-present leaf, so a mixed shape would
        // route on a different predicate than the author intended — reject it here.
        var composites =
            (condition.All is not null ? 1 : 0)
            + (condition.Any is not null ? 1 : 0)
            + (condition.Not is not null ? 1 : 0);
        var isLeaf =
            condition.Op is not null || condition.UnknownOp is not null || condition.Path is not null;
        if (composites > 1 || (composites == 1 && isLeaf))
        {
            errors.Add(
                $"Conditional node '{nodeId}' mixes leaf and composite condition forms; "
                    + "use exactly one of op/all/any/not."
            );
        }

        foreach (var child in condition.All ?? [])
        {
            ValidateCondition(child, nodeId, errors);
        }

        foreach (var child in condition.Any ?? [])
        {
            ValidateCondition(child, nodeId, errors);
        }

        ValidateCondition(condition.Not, nodeId, errors);
    }

    // Rule 6: every edge target resolves; from start, every node (and >= 1 terminal) is reachable over
    // the full edge set (next, branches, else, onFailure, onMaxVisits, onBudgetExhausted).
    private static void ValidateEdgesAndReachability(
        WorkflowDefinition def,
        IReadOnlyList<WorkflowNode> nodes,
        List<string> errors
    )
    {
        var ids = new HashSet<string>(
            nodes.Where(n => !string.IsNullOrEmpty(n.Id)).Select(n => n.Id),
            StringComparer.Ordinal
        );

        var edges = CollectEdges(def, nodes);
        foreach (var (source, kind, target) in edges)
        {
            if (!ids.Contains(target))
            {
                errors.Add(
                    $"Dangling edge: {kind} target '{target}' (from {source}) does not resolve to any node."
                );
            }
        }

        var starts = nodes.OfType<StartNode>().ToList();
        if (starts.Count != 1)
        {
            // Reachability is meaningless without a single entry point; rule 1 already reported this.
            return;
        }

        var adjacency = edges
            .Where(e => e.Source != WorkflowSource)
            .GroupBy(e => e.Source, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Where(e => ids.Contains(e.Target)).Select(e => e.Target).ToList(),
                StringComparer.Ordinal
            );

        var roots = new List<string> { starts[0].Id };
        if (!string.IsNullOrEmpty(def.OnBudgetExhausted) && ids.Contains(def.OnBudgetExhausted))
        {
            roots.Add(def.OnBudgetExhausted);
        }

        var visited = TraverseReachable(roots, adjacency);

        foreach (var node in nodes)
        {
            if (!string.IsNullOrEmpty(node.Id) && !visited.Contains(node.Id))
            {
                errors.Add($"Node '{node.Id}' is unreachable from the start node.");
            }
        }

        if (
            nodes.OfType<TerminalNode>().Any()
            && !nodes.OfType<TerminalNode>().Any(t => visited.Contains(t.Id))
        )
        {
            errors.Add("No terminal node is reachable from the start node.");
        }
    }

    private static List<(string Source, string Kind, string Target)> CollectEdges(
        WorkflowDefinition def,
        IReadOnlyList<WorkflowNode> nodes
    )
    {
        var edges = new List<(string, string, string)>();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case StartNode start:
                    foreach (var next in start.Next)
                    {
                        edges.Add((start.Id, "next", next));
                    }

                    break;
                case ProceduralNode procedural:
                    foreach (var next in procedural.Next)
                    {
                        edges.Add((procedural.Id, "next", next));
                    }

                    if (!string.IsNullOrEmpty(procedural.OnFailure))
                    {
                        edges.Add((procedural.Id, "onFailure", procedural.OnFailure));
                    }

                    if (!string.IsNullOrEmpty(procedural.OnMaxVisits))
                    {
                        edges.Add((procedural.Id, "onMaxVisits", procedural.OnMaxVisits));
                    }

                    foreach (var task in procedural.TaskList ?? [])
                    {
                        if (!string.IsNullOrEmpty(task.OnFailure))
                        {
                            edges.Add(
                                (procedural.Id, $"task '{task.Id}' onFailure", task.OnFailure)
                            );
                        }
                    }

                    break;
                case ConditionalNode conditional:
                    foreach (var branch in conditional.Branches ?? [])
                    {
                        if (!string.IsNullOrEmpty(branch.To))
                        {
                            edges.Add((conditional.Id, "branch", branch.To));
                        }
                    }

                    if (!string.IsNullOrEmpty(conditional.Else))
                    {
                        edges.Add((conditional.Id, "else", conditional.Else));
                    }

                    if (!string.IsNullOrEmpty(conditional.OnMaxVisits))
                    {
                        edges.Add((conditional.Id, "onMaxVisits", conditional.OnMaxVisits));
                    }

                    break;
                default:
                    break;
            }
        }

        if (!string.IsNullOrEmpty(def.OnBudgetExhausted))
        {
            edges.Add((WorkflowSource, "onBudgetExhausted", def.OnBudgetExhausted));
        }

        return edges;
    }

    private static HashSet<string> TraverseReachable(
        IEnumerable<string> roots,
        IReadOnlyDictionary<string, List<string>> adjacency
    )
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var root in roots)
        {
            if (visited.Add(root))
            {
                queue.Enqueue(root);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (visited.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return visited;
    }

    private static string Wire<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        var name = value.ToString();
        return string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }
}
