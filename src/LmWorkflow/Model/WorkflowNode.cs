using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     The abstract base for all workflow nodes. Concrete nodes are deserialized polymorphically by
///     <see cref="WorkflowNodeJsonConverter"/>, which dispatches on the <c>type</c> discriminator.
/// </summary>
public abstract record WorkflowNode
{
    /// <summary>Globally-unique node id.</summary>
    public required string Id { get; init; }

    /// <summary>
    ///     The node kind. Mirrors the JSON <c>type</c> discriminator and is set during deserialization;
    ///     structural validation keys off the concrete runtime type rather than this value.
    /// </summary>
    public NodeType Type { get; init; }

    /// <summary>Human-readable node title.</summary>
    public required string Title { get; init; }

    /// <summary>Optional instructions for the controller LLM driving this node.</summary>
    public string? ControllerInstructions { get; init; }
}

/// <summary>The single entry point of a workflow. Must declare exactly one <see cref="Next"/> target.</summary>
public sealed record StartNode : WorkflowNode
{
    /// <summary>The node ids this start transitions to (exactly one, enforced by the validator).</summary>
    public required IReadOnlyList<string> Next { get; init; }
}

/// <summary>
///     A node that runs an authored list of tasks (delegated to sub-agents), joins their results, and
///     transitions on.
/// </summary>
public sealed record ProceduralNode : WorkflowNode
{
    /// <summary>How the task list is sourced. V1 requires <see cref="TasksMode.Authored"/>.</summary>
    public TasksMode TasksMode { get; init; } = TasksMode.Authored;

    /// <summary>The authored tasks.</summary>
    public IReadOnlyList<WorkflowTask>? TaskList { get; init; }

    /// <summary>How task results are joined. Defaults to an all-join.</summary>
    public JoinPolicy JoinPolicy { get; init; } = new();

    /// <summary>An optional cap on concurrent task execution.</summary>
    public int? MaxParallel { get; init; }

    /// <summary>Optional node id to transition to when the node fails.</summary>
    public string? OnFailure { get; init; }

    /// <summary>An optional cap on how many times this node may be visited.</summary>
    public int? MaxVisits { get; init; }

    /// <summary>Optional node id to transition to when <see cref="MaxVisits"/> is exceeded.</summary>
    public string? OnMaxVisits { get; init; }

    /// <summary>The node ids this node transitions to (at least one, enforced by the validator).</summary>
    public required IReadOnlyList<string> Next { get; init; }
}

/// <summary>A node that selects a transition target from ordered <see cref="Branches"/>, falling back to <see cref="Else"/>.</summary>
public sealed record ConditionalNode : WorkflowNode
{
    /// <summary>The ordered branches; the first whose condition holds wins.</summary>
    public required IReadOnlyList<Branch> Branches { get; init; }

    /// <summary>The fallback node id used when no branch holds (must be non-empty).</summary>
    public string Else { get; init; } = string.Empty;

    /// <summary>An optional cap on how many times this node may be visited.</summary>
    public int? MaxVisits { get; init; }

    /// <summary>Optional node id to transition to when <see cref="MaxVisits"/> is exceeded.</summary>
    public string? OnMaxVisits { get; init; }
}

/// <summary>A node that ends the workflow and shapes the final output.</summary>
public sealed record TerminalNode : WorkflowNode
{
    /// <summary>An optional JSON-Schema fragment describing the final output.</summary>
    public JsonNode? FinalOutputSchema { get; init; }

    /// <summary>An optional template that shapes the final result from workflow state.</summary>
    public JsonNode? ResultTemplate { get; init; }
}

/// <summary>
///     Placeholder for a node whose <c>type</c> discriminator is not a V1-supported value (for example
///     <c>reduce</c>, or an absent/invalid type). It carries the raw type so the validator can emit a
///     clear "not supported in V1" error while still collecting every other validation error.
/// </summary>
internal sealed record UnknownNode : WorkflowNode
{
    /// <summary>The raw <c>type</c> discriminator value as it appeared in the JSON.</summary>
    public required string RawType { get; init; }
}
