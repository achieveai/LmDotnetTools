namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The typed reasons the cut selector, validator and checkpoint pipeline record (spec 679 §5.6).
///     Every skip and every failure carries one of these, never free text, so a state entry, an
///     observation and a lifecycle event can be compared by equality. <see cref="CheckpointReasons" />
///     holds the three the persistence layer itself writes.
/// </summary>
public static class CompactionReasons
{
    /// <summary>A cut-blocking state exists (deferred call, parked wait, owed continuation, interrupted turn): R6.</summary>
    public const string UnsafeState = "unsafe_state";

    /// <summary>No generation boundary satisfies R1–R4 before the start of the thread.</summary>
    public const string NoSafeBoundary = "no_safe_boundary";

    /// <summary>The in-memory view and the store disagree on the last row; nothing was prepared.</summary>
    public const string WatermarkDrift = "watermark_drift";

    /// <summary>The summarizer threw or returned nothing usable.</summary>
    public const string SummaryCallFailed = "summary_call_failed";

    /// <summary>The boundary row's persisted id is unknown or does not match the cut.</summary>
    public const string BoundaryMismatch = "boundary_mismatch";

    /// <summary>The checkpoint row could not be appended.</summary>
    public const string PersistFailed = "persist_failed";

    /// <summary>Prefix of every validation failure; the suffix names the rule, e.g. <c>validation_failed:V3</c>.</summary>
    public const string ValidationFailedPrefix = "validation_failed:";

    /// <summary>The reason recorded when validation rule <paramref name="rule" /> failed.</summary>
    public static string ValidationFailed(string rule) => ValidationFailedPrefix + rule;
}
