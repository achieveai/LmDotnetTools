using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// The identifiers that let a subscriber stitch an event into the larger picture — which thread,
/// run, turn, tool call, sub-agent, and sandbox session it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Every member is optional because the correlations that exist depend on the event. A
/// <see cref="LifecycleEventTypes.SandboxCreated"/> event has a session and a workspace but no
/// turn; a <see cref="LifecycleEventTypes.RunStarted"/> event for a top-level run has no parent.
/// Absent means "this correlation does not apply", never "unknown".
/// </para>
/// <para>
/// <b>The owner key is deliberately not here.</b> Tenancy is resolved by the host and never
/// travels on an envelope, so an event body cannot disclose the tenancy of the stream it came
/// from, and a caller cannot assert an owner by populating a field. See ADR 0005.
/// </para>
/// </remarks>
public sealed record LifecycleCorrelation
{
    /// <summary>The conversation thread this event belongs to.</summary>
    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; set; }

    /// <summary>The run this event belongs to.</summary>
    [JsonPropertyName("run_id")]
    public string? RunId { get; set; }

    /// <summary>
    /// The run that caused this run, for a resumed, delayed-result, or sub-agent child.
    /// </summary>
    /// <remarks>
    /// Lineage only. Whether the child inherited provider context is a separate question answered
    /// by <see cref="Payloads.RunStartedPayload.WasForked"/>.
    /// </remarks>
    [JsonPropertyName("parent_run_id")]
    public string? ParentRunId { get; set; }

    /// <summary>The turn this event belongs to.</summary>
    [JsonPropertyName("generation_id")]
    public string? GenerationId { get; set; }

    /// <summary>The tool call this event belongs to.</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>The sub-agent that produced this event, when a sub-agent produced it.</summary>
    [JsonPropertyName("sub_agent_id")]
    public string? SubAgentId { get; set; }

    /// <summary>The thread of the agent that spawned this sub-agent.</summary>
    [JsonPropertyName("parent_thread_id")]
    public string? ParentThreadId { get; set; }

    /// <summary>
    /// The tool call that spawned this sub-agent, when the spawn came from one.
    /// </summary>
    /// <remarks>
    /// Nullable even for a sub-agent: a sub-agent created directly by a host, rather than by a
    /// model-requested tool call, has a parent but no spawning call.
    /// </remarks>
    [JsonPropertyName("spawning_tool_call_id")]
    public string? SpawningToolCallId { get; set; }

    /// <summary>The sandbox session this event belongs to.</summary>
    [JsonPropertyName("sandbox_session_id")]
    public string? SandboxSessionId { get; set; }

    /// <summary>The workspace the sandbox session belongs to.</summary>
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; set; }
}
