using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>Where one checkpoint is in its life (spec 679 §3.5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckpointStatus
{
    /// <summary>The cut is chosen and the watermark captured; the summarizer is running.</summary>
    Prepared,

    /// <summary>The manifest passed validation (V1–V9); nothing durable has changed yet.</summary>
    Validated,

    /// <summary>The watermark was still current at commit; the checkpoint row is being appended.</summary>
    Committed,

    /// <summary>The row exists and the execution view is built on this checkpoint.</summary>
    Active,

    /// <summary>Refused before activation; <see cref="CheckpointEntry.Reason" /> says why.</summary>
    Rejected,

    /// <summary>Was active; a newer checkpoint now covers it.</summary>
    Superseded,

    /// <summary>Was active; deactivated after the fact. The row stays.</summary>
    RolledBack,
}

/// <summary>
///     Typed reasons the persistence layer itself records on a <see cref="CheckpointEntry" />. The policy's
///     wider vocabulary (spec 679 §5.6) is a superset; these are the values this assembly writes.
/// </summary>
public static class CheckpointReasons
{
    /// <summary>A row was appended between prepare and commit, or between commit and activation.</summary>
    public const string StaleWatermark = "stale_watermark";

    /// <summary>A committed checkpoint had no row on restart; canonical history wins.</summary>
    public const string RowMissing = "row_missing";

    /// <summary>A newer prepare superseded an in-flight checkpoint that never reached a terminal state.</summary>
    public const string Abandoned = "abandoned";
}

/// <summary>A queued manual compaction request (<see cref="CompactionState.PendingManual" />).</summary>
public sealed record PendingManualCompaction
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    /// <summary>What the summary should keep in mind; at most <see cref="ManualCompaction.MaxFocusChars" />.</summary>
    [JsonPropertyName("focus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Focus { get; init; }

    [JsonPropertyName("requested_at")]
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
///     The fit check's last resort (<see cref="CompactionState.ToolResultsTightened" />): tool results with <c>Seq</c> at
///     or below <see cref="ThroughSeq" /> are shown trimmed to <see cref="PartsPerMillion" /> of the length the view cap
///     would otherwise show, never below the cap's minimum. Persisted so every later request rebuilds the same bytes.
/// </summary>
public sealed record ToolResultTightening
{
    [JsonPropertyName("through_seq")]
    public required long ThroughSeq { get; init; }

    /// <summary>The share of each result's shown length kept, in millionths.</summary>
    [JsonPropertyName("parts_per_million")]
    public required int PartsPerMillion { get; init; }
}

/// <summary>One checkpoint's durable record in the state machine (spec 679 §3.5).</summary>
public sealed record CheckpointEntry
{
    /// <summary>The checkpoint id; the key of the entry.</summary>
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    /// <summary>Where the checkpoint is in its life.</summary>
    [JsonPropertyName("status")]
    public required CheckpointStatus Status { get; init; }

    /// <summary>The last canonical row the checkpoint covers.</summary>
    [JsonPropertyName("boundary_seq")]
    public required long BoundarySeq { get; init; }

    /// <summary>The thread's message watermark when the checkpoint was prepared: the guard's reference.</summary>
    [JsonPropertyName("watermark_at_prepare")]
    public required long WatermarkAtPrepare { get; init; }

    /// <summary>The <c>Seq</c> of the checkpoint row, once appended.</summary>
    [JsonPropertyName("row_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RowSeq { get; init; }

    /// <summary>What caused the compaction.</summary>
    [JsonPropertyName("trigger")]
    public required CompactionTrigger Trigger { get; init; }

    /// <summary>The typed reason for a Rejected or RolledBack status.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>
    ///     The summary failure a fallback checkpoint was built instead of (<see cref="CheckpointStats.SummaryFallback" />);
    ///     null for a summarised checkpoint. Kept through every later status.
    /// </summary>
    [JsonPropertyName("summary_fallback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SummaryFallback { get; init; }

    /// <summary>When the entry last changed status.</summary>
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>
    ///     When the attempt was prepared; never rewritten by a later status change. The thread rate limit counts
    ///     attempts by it. Null on entries written before it existed, which fall back to <see cref="At" />.
    /// </summary>
    [JsonPropertyName("prepared_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PreparedAt { get; init; }

    /// <summary>True for the states a checkpoint can still move out of on the happy path.</summary>
    [JsonIgnore]
    public bool IsInFlight =>
        Status is CheckpointStatus.Prepared or CheckpointStatus.Validated or CheckpointStatus.Committed;
}

/// <summary>
///     The per-thread compaction state machine and active pointer, persisted under
///     <c>ThreadMetadata.Properties["compaction.state"]</c> (spec 679 §3.5).
/// </summary>
public sealed record CompactionState
{
    /// <summary>The persisted schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>How many entries <see cref="History" /> keeps; the active and last-known-good ones are never trimmed.</summary>
    public const int HistoryLength = 20;

    /// <summary>Schema version of this record.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The checkpoint the execution view is built on, or null for raw history.</summary>
    [JsonPropertyName("active_checkpoint_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveCheckpointId { get; init; }

    /// <summary>The active checkpoint's boundary, duplicated here so the projection needs no history walk.</summary>
    [JsonPropertyName("active_boundary_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ActiveBoundarySeq { get; init; }

    /// <summary>
    ///     The newest checkpoint that reached Active and was not rolled back. Survives a rollback of the
    ///     active one, which is what the next request falls back to (§3.5).
    /// </summary>
    [JsonPropertyName("last_known_good_checkpoint_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastKnownGoodCheckpointId { get; init; }

    /// <summary>Every checkpoint this thread has recorded, oldest first, bounded to <see cref="HistoryLength" />.</summary>
    [JsonPropertyName("history")]
    public IReadOnlyList<CheckpointEntry> History { get; init; } = [];

    /// <summary>The generation ordinal of the last activation, for cooldown arithmetic (#684).</summary>
    [JsonPropertyName("last_checkpoint_generation_ordinal")]
    public long LastCheckpointGenerationOrdinal { get; init; }

    /// <summary>The generation ordinal a cooldown runs until, when one is active (#684).</summary>
    [JsonPropertyName("cooldown_until_generation_ordinal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CooldownUntilGenerationOrdinal { get; init; }

    /// <summary>
    ///     Tool results with <c>Seq</c> at or below this are shown to the model as a short placeholder. It
    ///     only ever advances, and only when compaction acts, so the view stays byte-stable between turns.
    /// </summary>
    [JsonPropertyName("tool_results_cleared_through_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ToolResultsClearedThroughSeq { get; init; }

    /// <summary>
    ///     Tool results still shown whole that the fit check trimmed harder because nothing else made the request
    ///     fit; null when it never had to. Clearing past <see cref="ToolResultTightening.ThroughSeq" /> makes it moot.
    /// </summary>
    [JsonPropertyName("tool_results_tightened")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolResultTightening? ToolResultsTightened { get; init; }

    /// <summary>How many compactions have failed in a row since the last activation.</summary>
    [JsonPropertyName("consecutive_failures")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ConsecutiveFailures { get; init; }

    /// <summary>The generation ordinal a failure backoff runs until (exclusive), when one is active.</summary>
    [JsonPropertyName("failure_backoff_until_generation_ordinal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FailureBackoffUntilGenerationOrdinal { get; init; }

    /// <summary>
    ///     A manual compaction an operator asked for that has not run yet. Persisted so eviction or a restart
    ///     does not lose it; the runtime clears it in the same write that claims it, so it runs once.
    /// </summary>
    [JsonPropertyName("pending_manual")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PendingManualCompaction? PendingManual { get; init; }

    /// <summary>How many <see cref="SizeRefusedRunIds" /> are kept, newest last.</summary>
    public const int SizeRefusedRunIdsLength = 10;

    /// <summary>
    ///     Runs that ended because no view fit the window (<c>view_exceeds_window</c> or
    ///     <c>overflow_after_compaction</c>). R4 does not keep the run after one of these whole: that would
    ///     make the retry uncuttable for the very reason its predecessor failed.
    /// </summary>
    [JsonPropertyName("size_refused_run_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SizeRefusedRunIds { get; init; }

    /// <summary>The entry for <paramref name="checkpointId" />, or null when the thread never recorded it.</summary>
    public CheckpointEntry? Find(string checkpointId) =>
        History.FirstOrDefault(e => string.Equals(e.CheckpointId, checkpointId, StringComparison.Ordinal));

    /// <summary>The entry the execution view is built on, or null for raw history.</summary>
    [JsonIgnore]
    public CheckpointEntry? Active => ActiveCheckpointId is null ? null : Find(ActiveCheckpointId);

    /// <summary>The entries that have not reached a terminal state.</summary>
    [JsonIgnore]
    public IEnumerable<CheckpointEntry> InFlight => History.Where(e => e.IsInFlight);
}
