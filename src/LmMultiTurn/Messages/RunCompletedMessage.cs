using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Message published when a run completes.
/// </summary>
public record RunCompletedMessage : IMessage
{
    public required string CompletedRunId { get; init; }

    /// <summary>
    /// Surface flag — <c>true</c> when this run was initiated via
    /// <c>SendAsync(parentRunId: …)</c> (i.e. the batch carried at least one
    /// caller-supplied <c>ParentRunId</c>). The flag reflects caller INTENT to
    /// fork. It does NOT imply that the new run inherits the parent's provider
    /// context: the underlying agent process starts from a fresh model context,
    /// and any grounding the parent run accumulated (tool results, file reads,
    /// reasoning) is not re-shown to the model. Callers needing context
    /// continuity must restate the relevant inputs in the next prompt.
    /// </summary>
    /// <remarks>
    /// A cross-provider <c>TranscriptReplay</c> primitive that would seed the
    /// fresh provider context with the parent run's transcript is tracked
    /// separately (see <c>src/LmMultiTurn/README.md</c> §"Fork semantics"). The
    /// per-provider Claude/Copilot/Codex session-resume paths
    /// (<c>--resume</c> / <c>session/load</c> / <c>thread/resume</c>) are
    /// distinct from forking — resume attaches to an existing session, fork
    /// branches into a new run id without re-attaching.
    /// </remarks>
    public bool WasForked { get; init; }

    public string? ForkedToRunId { get; init; }

    /// <summary>
    /// Indicates there are pending messages queued that haven't been assigned to a run yet.
    /// When true, workflows should NOT transition state - another run will follow to process
    /// the pending messages. Only transition when HasPendingMessages is false.
    /// </summary>
    public bool HasPendingMessages { get; init; }

    /// <summary>
    /// Number of pending message batches waiting to be processed.
    /// </summary>
    public int PendingMessageCount { get; init; }

    /// <summary>
    /// Indicates the run completed due to an error (e.g. API call failure).
    /// When true, <see cref="ErrorMessage"/> contains the error details.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    /// Error message when <see cref="IsError"/> is true.
    /// </summary>
    public string? ErrorMessage { get; init; }

    public string? FromAgent { get; init; }
    public Role Role => Role.System;
    public ImmutableDictionary<string, object>? Metadata { get; init; }
    public string? RunId => CompletedRunId;
    public string? ParentRunId { get; init; }
    public string? ThreadId { get; init; }
    public string? GenerationId { get; init; }
    public int? MessageOrderIdx { get; init; }
}
