using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     One canonical row as compaction sees it: the message plus the three facts the store owns and the
///     in-memory <see cref="IMessage" /> does not carry — its <see cref="PersistedMessage.Seq" />, its
///     persisted id, and the run it was persisted under (#683; spec 679 §2.2).
/// </summary>
/// <remarks>
///     <para>
///         Every cut, validation and projection decision is addressed by <c>Seq</c>, and the boundary
///         tie-check (V2) needs the persisted id. Neither lives on <see cref="IMessage" />, so the rows
///         reach compaction through this pairing rather than through the message list alone.
///     </para>
///     <para>
///         <see cref="MessageId" /> and <see cref="RunId" /> are null when the row came from the
///         in-memory snapshot (<see cref="SequencedHistory.FromSnapshot" />), which knows neither; they
///         are set when the row came from the store (<see cref="SequencedHistory.FromPersisted" />).
///         Validation requires the id, so a checkpoint is validated over store-derived rows.
///     </para>
/// </remarks>
/// <param name="Seq">The row's position in its thread (1-based, dense).</param>
/// <param name="MessageId">The persisted row id, or null when unknown.</param>
/// <param name="RunId">The run the row was persisted under, or null when unknown.</param>
/// <param name="Message">The row.</param>
public sealed record SequencedMessage(long Seq, string? MessageId, string? RunId, IMessage Message)
{
    /// <summary>
    ///     The run this row belongs to: the store's answer when known, else what the message itself
    ///     carries. Null for a row nobody stamped.
    /// </summary>
    public string? EffectiveRunId => RunId ?? Message.RunId;

    /// <summary>The row's text when it has any (<see cref="ICanGetText" />), else null.</summary>
    public string? Text => (Message as ICanGetText)?.GetText();

    /// <summary>
    ///     Human input (spec 679 §2.4): a <see cref="Role.User" /> row that is not a notification, not a
    ///     checkpoint, and not a tool result (which is user-role on the wire but authored by the loop).
    /// </summary>
    public bool IsHumanRow =>
        Message.Role == Role.User
        && Message
            is not (NotifyMessage or CompactionCheckpointMessage or ToolCallResultMessage or ToolsCallResultMessage);

    /// <summary>True for a <see cref="CompactionCheckpointMessage" /> row, which the projection never dispatches.</summary>
    public bool IsCheckpointRow => Message is CompactionCheckpointMessage;
}

/// <summary>Builds <see cref="SequencedMessage" /> lists from the two places rows come from.</summary>
public static class SequencedHistory
{
    /// <summary>
    ///     Numbers an in-memory history snapshot positionally: row <c>i</c> is <c>Seq i+1</c>. Correct
    ///     exactly when the snapshot is the complete canonical row list in append order — which is what
    ///     <c>GetHistorySnapshot()</c> holds while a single loop owns the thread and no restore dropped a
    ///     row. A caller that cannot assert that must use <see cref="FromPersisted" />.
    /// </summary>
    public static IReadOnlyList<SequencedMessage> FromSnapshot(IReadOnlyList<IMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var rows = new SequencedMessage[history.Count];
        for (var i = 0; i < history.Count; i++)
        {
            rows[i] = new SequencedMessage(i + 1, MessageId: null, RunId: null, history[i]);
        }

        return rows;
    }

    /// <summary>
    ///     Carries the store's own <c>Seq</c>, id and run id onto each readable row. A row the current
    ///     build cannot read is reported through <paramref name="onSkipped" /> and left out; a row with no
    ///     <c>Seq</c> (a legacy thread before its backfill, spec 679 §8.3) is left out silently because it
    ///     has no position to be addressed by. No pairing sweep runs here: R1 keeps tool pairs on one side
    ///     of a cut, and rehydration keeps reading the raw rows (I4).
    /// </summary>
    public static IReadOnlyList<SequencedMessage> FromPersisted(
        IReadOnlyList<PersistedMessage> rows,
        Action<PersistedMessage, Exception>? onSkipped = null
    )
    {
        ArgumentNullException.ThrowIfNull(rows);

        var result = new List<SequencedMessage>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Seq is not { } seq)
            {
                continue;
            }

            IMessage message;
            try
            {
                message = MessagePersistenceConverter.FromPersistedMessage(row);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onSkipped?.Invoke(row, ex);
                continue;
            }

            result.Add(new SequencedMessage(seq, row.Id, row.RunId, message));
        }

        result.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        return result;
    }
}
