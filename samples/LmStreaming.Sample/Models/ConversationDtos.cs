using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Models;

/// <summary>
/// Summary of a conversation for listing purposes.
/// </summary>
public record ConversationSummary
{
    public required string ThreadId { get; init; }
    public required string Title { get; init; }
    public string? Preview { get; init; }
    public required long LastUpdated { get; init; }

    /// <summary>
    /// Provider id this thread is locked to. Set on first agent creation and persisted
    /// in <c>ThreadMetadata.Properties["provider"]</c>. Null for legacy threads predating
    /// the per-conversation provider feature.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Workspace id this thread is locked to. Set on first agent creation and persisted
    /// in <c>ThreadMetadata.Properties["workspace"]</c>. Null for legacy threads predating
    /// the per-conversation workspace feature.
    /// </summary>
    public string? Workspace { get; init; }

    /// <summary>
    /// Chat mode id this thread is bound to. Seeded on first agent creation and updated on a
    /// deliberate mode switch, persisted in <c>ThreadMetadata.Properties["mode"]</c>. Lets the client
    /// restore the conversation's bound mode after a refresh instead of falling back to the default.
    /// Null for legacy threads predating per-conversation mode persistence.
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
/// In-memory run state for a conversation. Lets a reconnecting client decide whether to resume
/// the live stream: after switching conversations or refreshing, the backend run keeps running
/// (the agent is pooled), so a client returning to a conversation with <see cref="IsInProgress"/>
/// re-opens the WebSocket to resume the in-flight stream instead of showing a frozen partial.
/// </summary>
public record ConversationRunState
{
    public required string ThreadId { get; init; }
    public required bool IsInProgress { get; init; }
    public string? CurrentRunId { get; init; }
}

/// <summary>
/// DTO for updating conversation metadata (title, preview).
/// </summary>
public record ConversationMetadataUpdate
{
    public string? Title { get; init; }
    public string? Preview { get; init; }
}

/// <summary>
/// DTO for switching a conversation's chat mode.
/// </summary>
public record SwitchModeRequest
{
    public required string ModeId { get; init; }
}

/// <summary>
/// Response to a successful mode switch. <see cref="Warning"/> is populated (otherwise null) when the
/// switched-away agent had an armed <c>Wait</c> that the recreate discarded, so a headless caller can
/// surface that a pending park-and-wake was dropped.
/// </summary>
public record SwitchModeResponse
{
    [JsonPropertyName("modeId")]
    public required string ModeId { get; init; }

    [JsonPropertyName("modeName")]
    public required string ModeName { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}

/// <summary>
/// DTO for switching a conversation's provider. Unlike workspace (bound for life), a conversation's
/// provider is mutable once its run is idle — this carries the target provider id.
/// </summary>
public record SwitchProviderRequest
{
    public required string ProviderId { get; init; }
}

/// <summary>
/// Response to a successful provider switch. <see cref="Warning"/> mirrors <see cref="SwitchModeResponse.Warning"/>:
/// non-null when the recreate discarded an armed <c>Wait</c> on the thread.
/// </summary>
public record SwitchProviderResponse
{
    [JsonPropertyName("providerId")]
    public required string ProviderId { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}

/// <summary>
/// Request to reserve a new conversation thread and lock its workspace/provider/mode as metadata,
/// without starting a live agent/sandbox session or sending a first message. Enables headless
/// callers (e.g. a REST-only integration) to provision a conversation ahead of time.
/// </summary>
public record ProvisionConversationRequest
{
    public required string WorkspaceId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModeId { get; init; }

    /// <summary>
    /// Optional webhook URL the sample forwards auth-required/completed/denied notifications to
    /// for this thread's provider sign-ins. Persisted alongside the registration time so the
    /// forwarder can apply a first-wins tie-break among a session's attached threads.
    /// </summary>
    public string? AuthWebhookUrl { get; init; }

    /// <summary>
    /// Optional extra system instructions for THIS conversation, appended to the mode's system prompt
    /// (after the workspace-path suffix and any discovered CLAUDE.md/AGENTS.md block) every time the
    /// thread's agent is built. Provision carries no model or tool overrides, so this is the only way a
    /// headless caller can give the hosted agent its actual task: a caller that drives a specialized run
    /// (e.g. the code-review daemon's review methodology + output contract) would otherwise send only its
    /// user turn and get a generic workspace agent that never follows the caller's protocol. Additive, not
    /// a replacement — the mode stays in charge of the workspace, tools and sub-agent catalog.
    /// </summary>
    public string? SystemPromptAppendix { get; init; }
}

/// <summary>
/// Response to a successful <see cref="ProvisionConversationRequest"/> — carries the
/// server-generated thread id the caller uses for subsequent send/status calls.
/// </summary>
public record ProvisionConversationResponse
{
    public required string ThreadId { get; init; }
}

/// <summary>
/// Request to enqueue a message onto a previously-provisioned conversation thread.
/// </summary>
public record SendMessageRequest
{
    public required string Text { get; init; }

    /// <summary>
    /// When true, the run this message drives must not be able to start NEW sub-agents (reading from and
    /// following up with already-running ones stays available). Used by a caller that has just waited on a
    /// sub-agent completion barrier and is now asking for the synthesis turn: a fresh fan-out there would
    /// reopen the barrier it just waited on.
    /// <para>
    /// The host REFUSES the send (400 <c>spawn_suppression_unsupported</c>) when the thread's agent cannot
    /// enforce this, and echoes <see cref="SendMessageResponse.SpawningSuppressed"/> when it can — so a
    /// caller talking to a host that predates this field sees no acknowledgement and can fail closed
    /// instead of assuming a guarantee it never got.
    /// </para>
    /// </summary>
    public bool SuppressSubAgentSpawning { get; init; }

    /// <summary>
    /// Makes this send safe to REPEAT. When supplied, the host records the input under an id derived from
    /// this key (plus the options that change what the turn does) and reconciles a repeat against its
    /// durable accepted-input ledger, so a caller whose response was lost — a socket reset, or a process
    /// that died between the host accepting and the answer arriving — recovers the SAME input instead of
    /// queueing a second (minutes-long, sub-agent-fanning) turn.
    /// <para>
    /// The key must identify one turn including its options; it is scoped to the thread, so callers only
    /// need it to be unique within a conversation. A blank key is refused (400
    /// <c>invalid_idempotency_key</c>) rather than treated as absent, and a honoured key is acknowledged
    /// via <see cref="SendMessageResponse.IdempotencyKeyHonored"/> so a caller talking to a host that
    /// predates this field can fail closed instead of retrying into a duplicate review.
    /// </para>
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Response to a queued <see cref="SendMessageRequest"/>. Carries only the input id the caller
/// polls status by — no run id, since an injected send may fold into a run already in flight.
/// </summary>
public record SendMessageResponse
{
    public required string InputId { get; init; }
    public required bool Queued { get; init; }

    /// <summary>
    /// Acknowledgement that <see cref="SendMessageRequest.SuppressSubAgentSpawning"/> was accepted AND is
    /// enforced for the run this input drives. Absent/false means no such guarantee was made — which is
    /// exactly what a host predating the field returns, so a caller that requires it can detect the
    /// unsupported host rather than silently proceeding without suppression.
    /// </summary>
    public bool SpawningSuppressed { get; init; }

    /// <summary>
    /// Acknowledgement that <see cref="SendMessageRequest.IdempotencyKey"/> was supplied AND applied — i.e.
    /// repeating the identical send returns this same <see cref="InputId"/> rather than queueing another
    /// turn. Absent/false is exactly what a host predating the field returns, so a caller that depends on
    /// safe retries can fail closed instead of retrying into a duplicate review.
    /// </summary>
    public bool IdempotencyKeyHonored { get; init; }
}

/// <summary>
/// Resolved status of a conversation run, polled by either <c>runId</c> or <c>inputId</c>.
/// </summary>
public record ConversationStatusResponse
{
    public required string ThreadId { get; init; }
    public string? RunId { get; init; }
    public required string Status { get; init; }
    public object? Response { get; init; }
}
