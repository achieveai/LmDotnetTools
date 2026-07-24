using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     A deliberately flat, LLM-friendly surface for authoring a workflow. Every step shares ONE uniform
///     shape — <c>id</c> / <c>title</c> / <c>kind</c> plus a couple of kind-specific fields — so a model
///     never has to remember which fields belong to which node type (the exact failure that made the
///     internal <see cref="WorkflowDefinition"/> hard to author directly: it emitted <c>label</c>,
///     <c>prompt</c>, <c>type:"action"</c> and was rejected). <see cref="ToDefinition"/> translates this
///     into the internal, engine-facing <see cref="WorkflowDefinition"/>; the internal model is unchanged.
/// </summary>
/// <remarks>
///     Field-to-node mapping:
///     <list type="bullet">
///         <item><c>kind:"start"</c> → <see cref="StartNode"/> (uses <c>next</c>).</item>
///         <item><c>kind:"agent"</c> → a one-task <see cref="ProceduralNode"/> (<c>agent</c> →
///             <c>subagent_type</c>, <c>prompt</c> → <c>promptTemplate</c>, optional <c>saveAs</c> →
///             a <c>state.&lt;saveAs&gt;</c> write; optional <c>forEach</c> fans the SAME agent out over a
///             collection — sequentially in V1; uses <c>next</c>).</item>
///         <item><c>kind:"parallel"</c> → a multi-task <see cref="ProceduralNode"/> that runs every entry in
///             <c>agents</c> concurrently and joins them all (uses <c>next</c>).</item>
///         <item><c>kind:"branch"</c> → <see cref="ConditionalNode"/> (uses <c>branches</c> + <c>else</c>).</item>
///         <item><c>kind:"end"</c> → <see cref="TerminalNode"/>.</item>
///     </list>
///     A loop is just a <c>next</c>/<c>goto</c> that points BACK to an earlier step; add <c>maxVisits</c> +
///     <c>onMaxVisits</c> to a step for a hard iteration cap and escape.
/// </remarks>
public sealed record SimpleWorkflow
{
    /// <summary>Case-insensitive options so a model may use camelCase (<c>saveAs</c>, <c>onMaxVisits</c>, …).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Output options: camelCase field names and no null/absent fields, so a read-back matches the authoring shape.</summary>
    public static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The high-level objective the workflow pursues.</summary>
    public string Objective { get; init; } = string.Empty;

    /// <summary>The ordered steps of the workflow. Exactly one <c>start</c> and at least one <c>end</c>.</summary>
    public IReadOnlyList<SimpleStep> Steps { get; init; } = [];

    /// <summary>Parses a workflow from its flat-DSL JSON form (case-insensitive property names).</summary>
    /// <exception cref="WorkflowValidationException">The JSON is not a valid <see cref="SimpleWorkflow"/> object.</exception>
    public static SimpleWorkflow Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SimpleWorkflow>(json, JsonOptions)
                ?? throw new WorkflowValidationException(["The workflow is empty."]);
        }
        catch (JsonException ex)
        {
            throw new WorkflowValidationException([$"The workflow is not valid JSON: {ex.Message}"]);
        }
    }

    /// <summary>Translates this authoring surface into the internal engine definition.</summary>
    /// <exception cref="WorkflowValidationException">The simple workflow is missing required fields for a step's kind.</exception>
    public WorkflowDefinition ToDefinition() => SimpleWorkflowTranslator.ToDefinition(this);
}

/// <summary>One uniform step in a <see cref="SimpleWorkflow"/>. Which optional fields apply depends on <see cref="Kind"/>.</summary>
public sealed record SimpleStep
{
    /// <summary>Unique step id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable title. Defaults to <see cref="Id"/> when omitted.</summary>
    public string? Title { get; init; }

    /// <summary>The step kind: <c>start</c>, <c>agent</c>, <c>parallel</c>, <c>branch</c>, or <c>end</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The next step id (for <c>start</c>, <c>agent</c>, and <c>parallel</c> steps). May point BACK to an earlier step to form a loop.</summary>
    public string? Next { get; init; }

    /// <summary><c>agent</c> steps: the sub-agent type to delegate to.</summary>
    public string? Agent { get; init; }

    /// <summary><c>agent</c> steps: the prompt handed to the sub-agent. Use <c>{{item}}</c> inside a <c>forEach</c> step.</summary>
    public string? Prompt { get; init; }

    /// <summary>
    ///     <c>agent</c> steps (optional): fan the SAME agent out over each element of a state array, e.g.
    ///     <c>"state.files"</c> — one sub-agent spawn per element, with <c>{{item}}</c> bound to it. NOTE:
    ///     V1 runs the fan-out SEQUENTIALLY; use a <c>parallel</c> step for concurrent (different) agents.
    /// </summary>
    public string? ForEach { get; init; }

    /// <summary>
    ///     Optional: capture the agent's output. For a plain <c>agent</c> step it sets <c>state.&lt;saveAs&gt;</c>;
    ///     for a <c>forEach</c> step it APPENDS each element's output into the <c>state.&lt;saveAs&gt;</c> array.
    /// </summary>
    public string? SaveAs { get; init; }

    /// <summary><c>parallel</c> steps: the sub-agents to run concurrently; the step joins when all finish.</summary>
    public IReadOnlyList<SimpleAgent>? Agents { get; init; }

    /// <summary><c>branch</c> steps: the ordered conditions; the first that holds wins.</summary>
    public IReadOnlyList<SimpleBranch>? Branches { get; init; }

    /// <summary><c>branch</c> steps: the fallback step id when no branch holds.</summary>
    public string? Else { get; init; }

    /// <summary>Optional loop guard (<c>agent</c>/<c>parallel</c>/<c>branch</c> steps): the maximum times this step may be entered.</summary>
    public int? MaxVisits { get; init; }

    /// <summary>Optional loop guard: the step id to go to once <see cref="MaxVisits"/> is exceeded (the loop's escape hatch).</summary>
    public string? OnMaxVisits { get; init; }
}

/// <summary>One sub-agent within a <c>parallel</c> step.</summary>
public sealed record SimpleAgent
{
    /// <summary>The sub-agent type to delegate to.</summary>
    public string Agent { get; init; } = string.Empty;

    /// <summary>The prompt handed to the sub-agent.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Optional: capture this agent's output into <c>state.&lt;saveAs&gt;</c> so a later step can use it.</summary>
    public string? SaveAs { get; init; }
}

/// <summary>One condition of a <c>branch</c> step.</summary>
public sealed record SimpleBranch
{
    /// <summary>The (prose) condition that selects this branch.</summary>
    public string When { get; init; } = string.Empty;

    /// <summary>The step id to go to when <see cref="When"/> holds.</summary>
    public string Goto { get; init; } = string.Empty;
}

/// <summary>Translates the flat <see cref="SimpleWorkflow"/> authoring surface into internal engine types.</summary>
public static class SimpleWorkflowTranslator
{
    /// <summary>
    ///     Builds a <see cref="WorkflowDefinition"/> from <paramref name="workflow"/>. Kind names are matched
    ///     case-insensitively and a few friendly aliases are accepted (<c>task</c>=agent, <c>fanout</c>=parallel,
    ///     <c>if</c>=branch, <c>terminal</c>=end). Shape errors are accumulated and thrown together so the model
    ///     can fix them in one pass; graph-level errors (dangling/unreachable edges) are left to
    ///     <see cref="WorkflowValidator"/> on the resulting definition.
    /// </summary>
    /// <exception cref="WorkflowValidationException">One or more steps are missing fields required for their kind.</exception>
    public static WorkflowDefinition ToDefinition(SimpleWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var errors = new List<string>();
        var steps = workflow.Steps ?? [];

        if (string.IsNullOrWhiteSpace(workflow.Objective))
        {
            errors.Add("The workflow needs a non-empty 'objective'.");
        }

        if (steps.Count == 0)
        {
            errors.Add("The workflow needs at least one step.");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id))
            {
                errors.Add("A step is missing its 'id'.");
            }
            else if (!seenIds.Add(step.Id))
            {
                errors.Add($"Duplicate step id '{step.Id}'.");
            }
        }

        var nodes = new List<WorkflowNode>(steps.Count);
        foreach (var step in steps)
        {
            var node = BuildNode(step, errors);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowValidationException(errors);
        }

        return new WorkflowDefinition
        {
            SchemaVersion = 1,
            Objective = workflow.Objective,
            Nodes = nodes,
        };
    }

    /// <summary>
    ///     Translates a SINGLE step into its internal node (for the <c>AddNode</c> graph-editing tool). The
    ///     caller wires the node into the graph (splicing edges); this only maps the step's own fields.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The step is missing fields required for its kind, or has an unknown kind.</exception>
    public static WorkflowNode ToNode(SimpleStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(step.Id))
        {
            errors.Add("The step is missing its 'id'.");
        }

        var node = BuildNode(step, errors);
        if (errors.Count > 0 || node is null)
        {
            throw new WorkflowValidationException(
                errors.Count > 0 ? errors : ["The step could not be translated to a node."]
            );
        }

        return node;
    }

    /// <summary>
    ///     Parses a tool's <c>workflow</c>/<c>definition</c> argument into a <see cref="WorkflowDefinition"/>.
    ///     The model authors in the flat DSL (a <c>{ "steps": [...] }</c> object, the only shape advertised on
    ///     the tool schema); a legacy internal-shaped definition (<c>{ "nodes": [...] }</c>) is still accepted
    ///     for backward compatibility.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The DSL is malformed or missing required fields.</exception>
    /// <exception cref="JsonException">A legacy internal-shaped definition is not valid JSON.</exception>
    public static WorkflowDefinition FromToolArgument(JsonElement element) =>
        element.TryGetProperty("steps", out _)
            ? SimpleWorkflow.Deserialize(element.GetRawText()).ToDefinition()
            : WorkflowJson.DeserializeStrict(element.GetRawText());

    /// <summary>
    ///     Parses a tool's <c>node</c> argument into a <see cref="WorkflowNode"/>. A flat DSL step (a
    ///     <c>{ "kind": ... }</c> object) is translated; a legacy internal node (<c>{ "type": ... }</c>) is
    ///     accepted for backward compatibility.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The DSL step is malformed.</exception>
    /// <exception cref="JsonException">A legacy internal node is not valid JSON.</exception>
    public static WorkflowNode NodeFromToolArgument(JsonElement element)
    {
        if (element.TryGetProperty("kind", out _))
        {
            var step =
                JsonSerializer.Deserialize<SimpleStep>(element.GetRawText(), SimpleWorkflow.JsonOptions)
                ?? throw new WorkflowValidationException(["The step is empty."]);
            return ToNode(step);
        }

        return JsonSerializer.Deserialize<WorkflowNode>(element.GetRawText(), WorkflowJson.StrictOptions)
            ?? throw new JsonException("'node' deserialized to null.");
    }

    /// <summary>Maps one step's own fields to its internal node; accumulates shape errors, returns null for an unknown kind.</summary>
    private static WorkflowNode? BuildNode(SimpleStep step, List<string> errors)
    {
        var id = step.Id;
        var title = string.IsNullOrWhiteSpace(step.Title) ? id : step.Title.Trim();
        var kind = (step.Kind ?? string.Empty).Trim().ToLowerInvariant();

        switch (kind)
        {
            case "start":
                RequireField(errors, id, "next", step.Next);
                return new StartNode { Id = id, Title = title, Next = ToList(step.Next) };

            case "agent":
            case "task":
                RequireField(errors, id, "next", step.Next);
                RequireField(errors, id, "agent", step.Agent);
                RequireField(errors, id, "prompt", step.Prompt);
                var fanOut = !string.IsNullOrWhiteSpace(step.ForEach);
                return new ProceduralNode
                {
                    Id = id,
                    Title = title,
                    Next = ToList(step.Next),
                    MaxVisits = step.MaxVisits,
                    OnMaxVisits = NullIfBlank(step.OnMaxVisits),
                    TaskList =
                    [
                        new WorkflowTask
                        {
                            Id = $"{id}:task",
                            SubagentType = step.Agent,
                            PromptTemplate = step.Prompt ?? string.Empty,
                            ForEach = NullIfBlank(step.ForEach),
                            // V1 runs a forEach fan-out SEQUENTIALLY (the validator rejects parallel=true).
                            // True concurrency is the 'parallel' step kind.
                            Parallel = false,
                            Writes = MakeWrites(step.SaveAs, fanOut),
                        },
                    ],
                };

            case "parallel":
            case "fanout":
                RequireField(errors, id, "next", step.Next);
                var agents = step.Agents ?? [];
                if (agents.Count == 0)
                {
                    errors.Add($"Parallel step '{id}' needs at least one entry in 'agents'.");
                }

                var tasks = new List<WorkflowTask>(agents.Count);
                for (var ai = 0; ai < agents.Count; ai++)
                {
                    var member = agents[ai];
                    if (string.IsNullOrWhiteSpace(member.Agent))
                    {
                        errors.Add($"Parallel step '{id}' agent #{ai + 1} needs an 'agent'.");
                    }

                    if (string.IsNullOrWhiteSpace(member.Prompt))
                    {
                        errors.Add($"Parallel step '{id}' agent #{ai + 1} needs a 'prompt'.");
                    }

                    tasks.Add(
                        new WorkflowTask
                        {
                            Id = $"{id}:task{ai + 1}",
                            SubagentType = member.Agent,
                            PromptTemplate = member.Prompt,
                            Writes = MakeWrites(member.SaveAs, fanOut: false),
                        }
                    );
                }

                return new ProceduralNode
                {
                    Id = id,
                    Title = title,
                    Next = ToList(step.Next),
                    MaxVisits = step.MaxVisits,
                    OnMaxVisits = NullIfBlank(step.OnMaxVisits),
                    TaskList = tasks,
                };

            case "branch":
            case "if":
                var branches = step.Branches ?? [];
                if (branches.Count == 0)
                {
                    errors.Add($"Branch step '{id}' needs at least one 'branches' entry.");
                }

                RequireField(errors, id, "else", step.Else);
                return new ConditionalNode
                {
                    Id = id,
                    Title = title,
                    Branches =
                    [
                        .. branches.Select(b => new Branch
                        {
                            When = JsonValue.Create(b.When ?? string.Empty),
                            To = b.Goto ?? string.Empty,
                        }),
                    ],
                    Else = step.Else ?? string.Empty,
                    MaxVisits = step.MaxVisits,
                    OnMaxVisits = NullIfBlank(step.OnMaxVisits),
                };

            case "end":
            case "terminal":
                return new TerminalNode { Id = id, Title = title };

            default:
                errors.Add(
                    $"Step '{id}' has unknown kind '{step.Kind}'. Use one of: start, agent, parallel, branch, end."
                );
                return null;
        }
    }

    /// <summary>
    ///     Renders an internal <see cref="WorkflowDefinition"/> back into the flat <see cref="SimpleWorkflow"/>
    ///     authoring shape, so a tool (e.g. <c>GetWorkflow</c>) can read a workflow back in the SAME shape it
    ///     was written. For a DSL-authored workflow this is a faithful round-trip; a definition that uses
    ///     internal-only features (multiple <c>next</c> edges, per-task failure routes, output schemas, join
    ///     policies) is rendered best-effort — those extras aren't part of the flat surface.
    /// </summary>
    public static SimpleWorkflow FromDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new SimpleWorkflow
        {
            Objective = definition.Objective,
            Steps = [.. (definition.Nodes ?? []).Select(FromNode)],
        };
    }

    private static SimpleStep FromNode(WorkflowNode node)
    {
        // Only surface a title when it differs from the id (the DSL defaults title→id).
        var title = string.Equals(node.Title, node.Id, StringComparison.Ordinal) ? null : node.Title;

        switch (node)
        {
            case StartNode start:
                return new SimpleStep { Id = start.Id, Title = title, Kind = "start", Next = start.Next.FirstOrDefault() };

            case ProceduralNode { TaskList.Count: > 1 } parallel:
                return new SimpleStep
                {
                    Id = parallel.Id,
                    Title = title,
                    Kind = "parallel",
                    Next = parallel.Next.FirstOrDefault(),
                    Agents =
                    [
                        .. parallel.TaskList.Select(t => new SimpleAgent
                        {
                            Agent = t.SubagentType ?? string.Empty,
                            Prompt = t.PromptTemplate,
                            SaveAs = SaveAsOf(t),
                        }),
                    ],
                    MaxVisits = parallel.MaxVisits,
                    OnMaxVisits = parallel.OnMaxVisits,
                };

            case ProceduralNode agent:
                var task = agent.TaskList is { Count: 1 } list ? list[0] : null;
                return new SimpleStep
                {
                    Id = agent.Id,
                    Title = title,
                    Kind = "agent",
                    Next = agent.Next.FirstOrDefault(),
                    Agent = task?.SubagentType,
                    Prompt = task?.PromptTemplate,
                    ForEach = task?.ForEach,
                    SaveAs = task is null ? null : SaveAsOf(task),
                    MaxVisits = agent.MaxVisits,
                    OnMaxVisits = agent.OnMaxVisits,
                };

            case ConditionalNode conditional:
                return new SimpleStep
                {
                    Id = conditional.Id,
                    Title = title,
                    Kind = "branch",
                    Branches =
                    [
                        .. (conditional.Branches ?? []).Select(b => new SimpleBranch
                        {
                            When = WhenText(b.When),
                            Goto = b.To,
                        }),
                    ],
                    Else = conditional.Else,
                    MaxVisits = conditional.MaxVisits,
                    OnMaxVisits = conditional.OnMaxVisits,
                };

            case TerminalNode terminal:
                return new SimpleStep { Id = terminal.Id, Title = title, Kind = "end" };

            default:
                return new SimpleStep { Id = node.Id, Title = title, Kind = node.Type.ToString().ToLowerInvariant() };
        }
    }

    /// <summary>The <c>saveAs</c> name recovered from a task write into <c>state.&lt;name&gt;</c>, or null.</summary>
    private static string? SaveAsOf(WorkflowTask task) =>
        task.Writes?.To is { } to && to.StartsWith("state.", StringComparison.Ordinal)
            ? to["state.".Length..]
            : null;

    /// <summary>A branch condition as a prose string (unwrapping a JSON string; serializing a structured one).</summary>
    private static string WhenText(JsonNode? when) =>
        when switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            null => string.Empty,
            _ => when.ToJsonString(),
        };

    private static void RequireField(List<string> errors, string stepId, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Step '{stepId}' needs a '{field}'.");
        }
    }

    private static IReadOnlyList<string> ToList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static WriteSpec? MakeWrites(string? saveAs, bool fanOut) =>
        string.IsNullOrWhiteSpace(saveAs)
            ? null
            // A forEach step produces one output per element, so it APPENDS into the state array; a plain
            // agent produces a single value, so it SETs the state key.
            : new WriteSpec { To = $"state.{saveAs}", Mode = fanOut ? WriteMode.Append : WriteMode.Set };
}
