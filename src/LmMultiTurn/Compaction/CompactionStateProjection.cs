using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     Reads and advances the per-thread <see cref="CompactionState" /> under
///     <c>ThreadMetadata.Properties["compaction.state"]</c> (#680; spec 679 §3.5).
/// </summary>
/// <remarks>
///     <para>
///         THE GUARD. A checkpoint captures the thread's message watermark when it is prepared. It may
///         commit only while that watermark is still current, and it may activate only when its own row
///         landed at exactly <c>watermark + 1</c>. Between them those two checks close the window the
///         commit's read leaves open: a row appended after <see cref="TryCommitAsync" /> read the watermark
///         but before the checkpoint row was appended pushes the checkpoint row to <c>watermark + 2</c>,
///         and <see cref="ActivateAsync" /> refuses it. Canonical history is the tie-breaker every time;
///         a rejected checkpoint never touches the active pointer, so the execution view is unchanged.
///     </para>
///     <para>
///         IDEMPOTENCE. Every transition is a compare-and-set on the entry's current status: repeating
///         <see cref="TryCommitAsync" /> on a Committed entry, or <see cref="ActivateAsync" /> on an Active
///         one, returns the state unchanged. A retry after a crash therefore neither re-runs the guard
///         (which could now fail for a checkpoint that already legitimately committed) nor double-writes.
///     </para>
///     <para>
///         ROLLBACK SEMANTICS. Every method here returns <c>null</c> when the persisted state carries a
///         schema version newer than <see cref="CompactionState.CurrentSchemaVersion" />: an older binary
///         reads such a thread as having no compaction state and leaves the record exactly as it found
///         it, for the newer binary to pick up again.
///     </para>
/// </remarks>
public static class CompactionStateProjection
{
    /// <summary>The metadata property the state lives under.</summary>
    public const string PropertyKey = "compaction.state";

    /// <summary>The state for <paramref name="threadId" />, or null when absent, corrupt, or newer than this build.</summary>
    public static async Task<CompactionState?> LoadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return FromMetadata(await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false));
    }

    /// <summary>Projects the state out of already-loaded metadata; null when absent, corrupt, or newer than this build.</summary>
    public static CompactionState? FromMetadata(ThreadMetadata? metadata)
    {
        if (
            MetadataProjectionJson.SchemaVersion(metadata, PropertyKey, CompactionState.CurrentSchemaVersion)
            > CompactionState.CurrentSchemaVersion
        )
        {
            return null;
        }

        return MetadataProjectionJson.Deserialize<CompactionState>(
            MetadataProjectionJson.RawJson(metadata, PropertyKey)
        );
    }

    /// <summary>
    ///     Records a new checkpoint as Prepared with the watermark the guard will later compare against.
    ///     Any entry still in flight is rejected as <see cref="CheckpointReasons.Abandoned" /> first: at most
    ///     one checkpoint is ever in flight per thread.
    /// </summary>
    public static Task<CompactionState?> PrepareAsync(
        IConversationStore store,
        string threadId,
        string checkpointId,
        long boundarySeq,
        long watermarkAtPrepare,
        CompactionTrigger trigger,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(checkpointId);
        var stamp = at ?? DateTimeOffset.UtcNow;

        return UpdateAsync(
            store,
            threadId,
            state =>
            {
                var history = state
                    .History.Where(e => !string.Equals(e.CheckpointId, checkpointId, StringComparison.Ordinal))
                    .Select(e =>
                        e.IsInFlight
                            ? e with
                            {
                                Status = CheckpointStatus.Rejected,
                                Reason = CheckpointReasons.Abandoned,
                                At = stamp,
                            }
                            : e
                    )
                    .Append(
                        new CheckpointEntry
                        {
                            CheckpointId = checkpointId,
                            Status = CheckpointStatus.Prepared,
                            BoundarySeq = boundarySeq,
                            WatermarkAtPrepare = watermarkAtPrepare,
                            Trigger = trigger,
                            At = stamp,
                        }
                    )
                    .ToList();

                return state with
                {
                    History = history,
                };
            },
            ct
        );
    }

    /// <summary>Moves a Prepared entry to Validated. Any other status is left as it is.</summary>
    public static Task<CompactionState?> MarkValidatedAsync(
        IConversationStore store,
        string threadId,
        string checkpointId,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    ) =>
        Transition(
            store,
            threadId,
            checkpointId,
            entry =>
                entry.Status == CheckpointStatus.Prepared
                    ? entry with
                    {
                        Status = CheckpointStatus.Validated,
                        At = at ?? DateTimeOffset.UtcNow,
                    }
                    : entry,
            ct
        );

    /// <summary>
    ///     Moves a Validated entry to Committed if, and only if, the thread's watermark still equals the one
    ///     captured at prepare; otherwise to Rejected(<see cref="CheckpointReasons.StaleWatermark" />). A
    ///     Committed or Active entry is returned unchanged, so a retry is a no-op.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry is still Prepared: commit requires validation first.</exception>
    public static async Task<CompactionState?> TryCommitAsync(
        IConversationStore store,
        string threadId,
        string checkpointId,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        var stamp = at ?? DateTimeOffset.UtcNow;

        // Read the watermark OUTSIDE the metadata write: the update callback runs under the store's
        // write lock and must not re-enter the store. The activation check closes the gap this leaves.
        var watermark = await store.GetMessageWatermarkAsync(threadId, ct).ConfigureAwait(false);

        return await Transition(
                store,
                threadId,
                checkpointId,
                entry =>
                    entry.Status switch
                    {
                        CheckpointStatus.Prepared => throw new InvalidOperationException(
                            $"Checkpoint '{checkpointId}' has not been validated; it cannot be committed."
                        ),
                        CheckpointStatus.Validated when watermark == entry.WatermarkAtPrepare => entry with
                        {
                            Status = CheckpointStatus.Committed,
                            At = stamp,
                        },
                        CheckpointStatus.Validated => entry with
                        {
                            Status = CheckpointStatus.Rejected,
                            Reason = CheckpointReasons.StaleWatermark,
                            At = stamp,
                        },
                        _ => entry,
                    },
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Moves a Committed entry to Active - and makes it the execution view and the last known good -
    ///     if, and only if, its row landed at <c>watermark_at_prepare + 1</c>; otherwise to
    ///     Rejected(<see cref="CheckpointReasons.StaleWatermark" />). The previously active checkpoint
    ///     becomes Superseded. An Active entry is returned unchanged, so a retry is a no-op.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry has not been committed.</exception>
    public static Task<CompactionState?> ActivateAsync(
        IConversationStore store,
        string threadId,
        string checkpointId,
        long rowSeq,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    )
    {
        var stamp = at ?? DateTimeOffset.UtcNow;

        return UpdateAsync(
            store,
            threadId,
            state =>
            {
                var entry = state.Find(checkpointId);
                if (entry is null)
                {
                    return state;
                }

                return entry.Status switch
                {
                    CheckpointStatus.Prepared or CheckpointStatus.Validated => throw new InvalidOperationException(
                        $"Checkpoint '{checkpointId}' has not been committed; it cannot be activated."
                    ),
                    CheckpointStatus.Committed when rowSeq == entry.WatermarkAtPrepare + 1 => Activate(
                        state,
                        entry,
                        rowSeq,
                        stamp
                    ),
                    CheckpointStatus.Committed => Replace(
                        state,
                        entry with
                        {
                            Status = CheckpointStatus.Rejected,
                            Reason = CheckpointReasons.StaleWatermark,
                            RowSeq = rowSeq,
                            At = stamp,
                        }
                    ),
                    // Terminal or already active: a retry changes nothing.
                    CheckpointStatus.Active
                    or CheckpointStatus.Rejected
                    or CheckpointStatus.Superseded
                    or CheckpointStatus.RolledBack => state,
                    _ => state,
                };
            },
            ct
        );
    }

    /// <summary>Moves an in-flight entry to Rejected with a typed <paramref name="reason" />. Terminal entries are left alone.</summary>
    public static Task<CompactionState?> RejectAsync(
        IConversationStore store,
        string threadId,
        string checkpointId,
        string reason,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        return Transition(
            store,
            threadId,
            checkpointId,
            entry =>
                entry.IsInFlight
                    ? entry with
                    {
                        Status = CheckpointStatus.Rejected,
                        Reason = reason,
                        At = at ?? DateTimeOffset.UtcNow,
                    }
                    : entry,
            ct
        );
    }

    /// <summary>
    ///     Deactivates the active checkpoint (RolledBack, with <paramref name="reason" />) and falls back
    ///     to the newest Superseded one as the last known good, or to raw history when there is none. The
    ///     rows stay. Nothing active is a no-op.
    /// </summary>
    public static Task<CompactionState?> RollBackAsync(
        IConversationStore store,
        string threadId,
        string reason,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var stamp = at ?? DateTimeOffset.UtcNow;

        return UpdateAsync(
            store,
            threadId,
            state =>
            {
                var active = state.Active;
                if (active is null)
                {
                    return state;
                }

                var rolled = Replace(
                    state,
                    active with
                    {
                        Status = CheckpointStatus.RolledBack,
                        Reason = reason,
                        At = stamp,
                    }
                );

                var fallback = rolled.History.LastOrDefault(e => e.Status == CheckpointStatus.Superseded);
                if (fallback is null)
                {
                    return rolled with
                    {
                        ActiveCheckpointId = null,
                        ActiveBoundarySeq = null,
                        LastKnownGoodCheckpointId = null,
                    };
                }

                return Replace(rolled, fallback with { Status = CheckpointStatus.Active, At = stamp }) with
                {
                    ActiveCheckpointId = fallback.CheckpointId,
                    ActiveBoundarySeq = fallback.BoundarySeq,
                    LastKnownGoodCheckpointId = fallback.CheckpointId,
                };
            },
            ct
        );
    }

    /// <summary>
    ///     Restart recovery (spec 679 §8.1): a Committed entry whose row exists at
    ///     <c>watermark_at_prepare + 1</c> is activated; one whose row is missing - or is some other row -
    ///     is Rejected(<see cref="CheckpointReasons.RowMissing" />); a Prepared or Validated entry, whose
    ///     summarizer died with the process, is Rejected(<see cref="CheckpointReasons.Abandoned" />).
    ///     Canonical history decides; the metadata follows.
    /// </summary>
    public static async Task<CompactionState?> ReconcileAsync(
        IConversationStore store,
        string threadId,
        DateTimeOffset? at = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        var stamp = at ?? DateTimeOffset.UtcNow;

        var state = await LoadAsync(store, threadId, ct).ConfigureAwait(false);
        if (state is null || !state.InFlight.Any())
        {
            return state;
        }

        // All store reads happen here, before the single metadata write below.
        var rowFound = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in state.History.Where(e => e.Status == CheckpointStatus.Committed))
        {
            var expected = entry.WatermarkAtPrepare + 1;
            var rows = await store.LoadMessageRangeAsync(threadId, expected, expected, 1, ct).ConfigureAwait(false);
            rowFound[entry.CheckpointId] = rows.Count == 1 && IsCheckpointRow(rows[0], entry.CheckpointId);
        }

        return await UpdateAsync(
                store,
                threadId,
                current =>
                {
                    var next = current;
                    foreach (var entry in current.InFlight.ToList())
                    {
                        next = entry.Status switch
                        {
                            CheckpointStatus.Committed when rowFound.GetValueOrDefault(entry.CheckpointId) => Activate(
                                next,
                                entry,
                                entry.WatermarkAtPrepare + 1,
                                stamp
                            ),
                            CheckpointStatus.Committed => Replace(
                                next,
                                entry with
                                {
                                    Status = CheckpointStatus.Rejected,
                                    Reason = CheckpointReasons.RowMissing,
                                    At = stamp,
                                }
                            ),
                            _ => Replace(
                                next,
                                entry with
                                {
                                    Status = CheckpointStatus.Rejected,
                                    Reason = CheckpointReasons.Abandoned,
                                    At = stamp,
                                }
                            ),
                        };
                    }

                    return next;
                },
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The generic read-modify-write every transition is built on, for callers (the policy, #684)
    ///     that own fields this class does not interpret. <paramref name="mutate" /> runs under the
    ///     store's write lock and receives a fresh default state when none is persisted. Returns the
    ///     state as written, or null when a newer schema was found and nothing was written.
    /// </summary>
    public static async Task<CompactionState?> UpdateAsync(
        IConversationStore store,
        string threadId,
        Func<CompactionState, CompactionState> mutate,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(mutate);

        CompactionState? written = null;
        await store
            .UpdateMetadataAsync(
                threadId,
                existing =>
                {
                    if (
                        MetadataProjectionJson.SchemaVersion(
                            existing,
                            PropertyKey,
                            CompactionState.CurrentSchemaVersion
                        ) > CompactionState.CurrentSchemaVersion
                    )
                    {
                        // A newer build owns this record. Leave every byte of it alone.
                        written = null;
                        return existing!;
                    }

                    var current =
                        MetadataProjectionJson.Deserialize<CompactionState>(
                            MetadataProjectionJson.RawJson(existing, PropertyKey)
                        ) ?? new CompactionState();

                    var next = Trim(mutate(current) with { SchemaVersion = CompactionState.CurrentSchemaVersion });
                    written = next;

                    return MetadataProjectionJson.WithProperties(
                        existing,
                        threadId,
                        (PropertyKey, JsonSerializer.Serialize(next, MetadataProjectionJson.Options))
                    );
                },
                ct
            )
            .ConfigureAwait(false);

        return written;
    }

    private static Task<CompactionState?> Transition(
        IConversationStore store,
        string threadId,
        string checkpointId,
        Func<CheckpointEntry, CheckpointEntry> transition,
        CancellationToken ct
    ) =>
        UpdateAsync(
            store,
            threadId,
            state =>
            {
                var entry = state.Find(checkpointId);
                return entry is null ? state : Replace(state, transition(entry));
            },
            ct
        );

    private static CompactionState Activate(
        CompactionState state,
        CheckpointEntry entry,
        long rowSeq,
        DateTimeOffset at
    )
    {
        var history = state
            .History.Select(e =>
                string.Equals(e.CheckpointId, entry.CheckpointId, StringComparison.Ordinal)
                    ? entry with
                    {
                        Status = CheckpointStatus.Active,
                        RowSeq = rowSeq,
                        Reason = null,
                        At = at,
                    }
                : e.Status == CheckpointStatus.Active ? e with { Status = CheckpointStatus.Superseded, At = at }
                : e
            )
            .ToList();

        return state with
        {
            History = history,
            ActiveCheckpointId = entry.CheckpointId,
            ActiveBoundarySeq = entry.BoundarySeq,
            LastKnownGoodCheckpointId = entry.CheckpointId,
        };
    }

    private static CompactionState Replace(CompactionState state, CheckpointEntry entry) =>
        state with
        {
            History =
            [
                .. state.History.Select(e =>
                    string.Equals(e.CheckpointId, entry.CheckpointId, StringComparison.Ordinal) ? entry : e
                ),
            ],
        };

    /// <summary>
    ///     Drops the oldest terminal entries past <see cref="CompactionState.HistoryLength" />. The active
    ///     entry, the last known good, and anything in flight are never dropped: the pointers must always
    ///     resolve.
    /// </summary>
    private static CompactionState Trim(CompactionState state)
    {
        if (state.History.Count <= CompactionState.HistoryLength)
        {
            return state;
        }

        var kept = state.History.ToList();
        for (var i = 0; i < kept.Count && kept.Count > CompactionState.HistoryLength; )
        {
            var entry = kept[i];
            var pinned =
                entry.IsInFlight
                || string.Equals(entry.CheckpointId, state.ActiveCheckpointId, StringComparison.Ordinal)
                || string.Equals(entry.CheckpointId, state.LastKnownGoodCheckpointId, StringComparison.Ordinal);

            if (pinned)
            {
                i++;
            }
            else
            {
                kept.RemoveAt(i);
            }
        }

        return state with
        {
            History = kept,
        };
    }

    private static bool IsCheckpointRow(PersistedMessage row, string checkpointId)
    {
        if (!string.Equals(row.MessageType, nameof(CompactionCheckpointMessage), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(row.MessageJson);
            return document.RootElement.TryGetProperty("checkpoint_id", out var id)
                && string.Equals(id.GetString(), checkpointId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
