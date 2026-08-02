using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>
///     A single ready-to-spawn sub-agent action composed by the runtime for the active node. The
///     controller LLM addresses the spawn by passing <see cref="Name"/> as the <c>name</c> argument of the
///     <c>Agent</c> tool, which is how the runtime correlates the eventual tool result back to the
///     originating task (see <see cref="WorkflowRuntime.RegisterSpawn"/>).
/// </summary>
public sealed record SpawnUnit
{
    /// <summary>The correlation name, formatted <c>nodeId:visit:taskId</c> (no index in the no-forEach case).</summary>
    public required string Name { get; init; }

    /// <summary>The sub-agent template key to spawn (mirrors the task's <c>subagent_type</c>).</summary>
    public required string SubagentType { get; init; }

    /// <summary>
    ///     The task's model-intelligence tier (mirrors the task's <c>modelIntelligence</c>), surfaced to the
    ///     controller so it forwards the same tier as the <c>modelIntelligence</c> argument of the Agent tool.
    ///     Null leaves model selection to the spawned agent's own default.
    /// </summary>
    public int? ModelIntelligence { get; init; }

    /// <summary>The fully-composed prompt (shared context + rendered template + schema-return directive).</summary>
    public required string Prompt { get; init; }

    /// <summary>The task's output schema fragment the spawned result is validated against, if any.</summary>
    public JsonNode? OutputSchema { get; init; }

    /// <summary>
    ///     The delegate's trusted collaboration role, derived from the authored task's label (see
    ///     <see cref="Collaboration.WorkflowCollaboration.DeriveDelegateRole"/>). Deliberately NOT part of the
    ///     controller-facing projection: it describes the delegation to other agents, and re-deriving it from
    ///     the controller's own tool arguments would let the model relabel what the workflow author defined.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>
    ///     The delegate's trusted collaboration description, derived from the owning node's title and the
    ///     task's label (see <see cref="Collaboration.WorkflowCollaboration.DeriveDelegateDescription"/>).
    /// </summary>
    public string? Description { get; init; }
}
