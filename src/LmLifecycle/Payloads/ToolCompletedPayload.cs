using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// What the approval pipeline decided about a tool call.
/// </summary>
/// <remarks>
/// Present on every host-executed call once approval is configured, whether or not a gate was
/// actually opened — a call refused by provider-native or host policy reports its refusal here and
/// never reached an approver.
/// </remarks>
public sealed record ToolApprovalSummary
{
    /// <summary>
    /// The stable decision code. See <see cref="ToolApprovalOutcomes"/>. Only
    /// <see cref="ToolApprovalOutcomes.Allowed"/> means the handler ran; every other value —
    /// including one this build does not recognize — means it did not.
    /// </summary>
    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the exact argument string that was approved, over its UTF-8 bytes, in lowercase
    /// hex.
    /// </summary>
    /// <remarks>
    /// <b>This is not a hash of canonicalized JSON.</b> Nothing in the pipeline sorts, normalizes,
    /// or re-serializes tool arguments, so "canonical" here means precisely the string that would
    /// be handed to the handler. Those bytes are frozen when the gate opens and are the bytes that
    /// execute, which is what closes the gap between what an approver saw and what ran.
    /// </remarks>
    [JsonPropertyName("arguments_hash")]
    public string ArgumentsHash { get; set; } = string.Empty;

    /// <summary>
    /// An opaque identifier for the approver that decided, when one did. Absent for decisions the
    /// host derived itself, such as a timeout.
    /// </summary>
    [JsonPropertyName("decided_by")]
    public string? DecidedBy { get; set; }

    /// <summary>How long the call waited for a decision.</summary>
    [JsonPropertyName("wait_ms")]
    public long? WaitMilliseconds { get; set; }
}

/// <summary>
/// Payload for <see cref="LifecycleEventTypes.ToolCompleted"/>.
/// </summary>
/// <remarks>
/// Emitted when a tool call reaches its final state, including when a delayed result resolves after
/// the run that requested it has already ended. In that case the event precedes the child run the
/// result causes. See ADR 0004.
/// </remarks>
public sealed record ToolCompletedPayload
{
    /// <summary>The run that requested the call.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// The turn that requested the call. Absent when the requesting turn can no longer be
    /// attributed, which can happen for a result resolving long after its run ended.
    /// </summary>
    [JsonPropertyName("generation_id")]
    public string? GenerationId { get; set; }

    /// <summary>The tool call that completed.</summary>
    [JsonPropertyName("tool_call_id")]
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>The tool's registered name.</summary>
    [JsonPropertyName("tool_name")]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Where the call executed. See <see cref="LifecycleToolKinds"/>. Open vocabulary.
    /// </summary>
    [JsonPropertyName("tool_kind")]
    public string ToolKind { get; set; } = LifecycleToolKinds.Host;

    /// <summary>
    /// How the call ended. See <see cref="LifecycleToolOutcomes"/>. Open vocabulary.
    /// </summary>
    /// <remarks>
    /// <b><see cref="LifecycleToolOutcomes.Succeeded"/> is a transport claim, not an accomplishment
    /// claim.</b> It means the call completed without the handler signalling failure — nothing more. A
    /// sandbox read of a missing path returns a SUCCESSFUL tool result whose text says the file is not
    /// there, and it is reported here as <c>Succeeded</c> with no <see cref="Error"/> attached. That is
    /// not a rare edge: 289 such results were emitted on this stream in a single night, and a query for
    /// them over <see cref="Outcome"/> alone comes back clean.
    /// <para>
    /// So do NOT read this field as "the tool did what was asked". <see cref="ResultClass"/> is the field
    /// that answers that question, and the two are deliberately separate: this one describes the call,
    /// that one describes what came back.
    /// </para>
    /// </remarks>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = LifecycleToolOutcomes.Succeeded;

    /// <summary>
    /// What the result TEXT reports, independently of whether the handler signalled failure:
    /// <c>ok</c>, <c>empty</c>, <c>not-found</c>, <c>denied</c>, <c>error</c>, <c>unclassified</c> or
    /// <c>deferred</c>. Open vocabulary. <c>null</c> means not recorded — an event from a build that
    /// predates this field, never a positive claim that nothing went wrong.
    /// </summary>
    /// <remarks>
    /// Added ALONGSIDE <see cref="Outcome"/> rather than changing what <c>Succeeded</c> means, because
    /// that value is a defensible transport-level statement with consumers that cannot be enumerated
    /// from this repository. Widening an existing field's meaning would silently change what every
    /// existing reader is told; adding one leaves them exactly as correct as they were.
    /// <para>
    /// This is the field to group by when the question is "what did the tools actually return" — the
    /// one that <see cref="Outcome"/> cannot answer, and the reason a tool-failure count built on the
    /// error flag would have reported zero for a population of 289 real failures and looked healthy.
    /// </para>
    /// </remarks>
    [JsonPropertyName("result_class")]
    public string? ResultClass { get; set; }

    /// <summary>
    /// Whether the call was deferred and resolved after its requesting run reached a terminal
    /// boundary.
    /// </summary>
    [JsonPropertyName("was_deferred")]
    public bool WasDeferred { get; set; }

    /// <summary>How long the call took, measured from dispatch to final state.</summary>
    [JsonPropertyName("duration_ms")]
    public long? DurationMilliseconds { get; set; }

    /// <summary>
    /// The approval decision, when approval was configured for this call. Absent when no policy and
    /// no gate were configured, which is the default.
    /// </summary>
    [JsonPropertyName("approval")]
    public ToolApprovalSummary? Approval { get; set; }

    /// <summary>
    /// The failure, when <see cref="Outcome"/> is not
    /// <see cref="LifecycleToolOutcomes.Succeeded"/>. Absent on success.
    /// </summary>
    [JsonPropertyName("error")]
    public LifecycleError? Error { get; set; }
}
