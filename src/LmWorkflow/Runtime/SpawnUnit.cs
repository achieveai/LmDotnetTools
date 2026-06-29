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

    /// <summary>The fully-composed prompt (shared context + rendered template + schema-return directive).</summary>
    public required string Prompt { get; init; }

    /// <summary>The task's output schema fragment the spawned result is validated against, if any.</summary>
    public JsonNode? OutputSchema { get; init; }
}
