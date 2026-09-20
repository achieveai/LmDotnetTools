using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The rows the model currently sees, content-free, for the "what the agent sees" view (spec 679 §2.1).
/// </summary>
/// <param name="ActiveCheckpointId">The checkpoint the view is built on, or null for raw history.</param>
/// <param name="BoundarySeq">The last row the checkpoint stands in for, or null.</param>
/// <param name="RowsHidden">Canonical rows the checkpoint replaced (checkpoint rows themselves excluded).</param>
/// <param name="RowsInTail">Canonical rows dispatched verbatim after the envelope.</param>
/// <param name="EstimatedTokens">Estimated size of envelope plus tail.</param>
public sealed record ExecutionViewDescriptor(
    string? ActiveCheckpointId,
    long? BoundarySeq,
    int RowsHidden,
    int RowsInTail,
    long EstimatedTokens
);

/// <summary>
///     Builds the execution view — the message list a provider request is built from — as a pure
///     function of the canonical rows and the active checkpoint (#683; spec 679 §2.1, I3, I3a).
/// </summary>
/// <remarks>
///     <para>
///         1. Every <see cref="CompactionCheckpointMessage" /> row is dropped: active, superseded and
///         rolled back alike. The snapshot contains them — commit appends the row, and a restart restores
///         every row — and the store does not filter, so this is the one place the filter lives.
///     </para>
///     <para>
///         2. With an active checkpoint, rows with <c>Seq &lt;= Boundary.Seq</c> are dropped and one
///         synthetic <see cref="Role.User" /> <see cref="TextMessage" /> rendered from the checkpoint takes
///         their place. It is never added to history and never published.
///     </para>
///     <para>
///         3. Without one, the view is the system prompt followed by every non-checkpoint row: what
///         <c>GetMessagesWithSystemPrompt()</c> returned before compaction existed.
///     </para>
///     <para>
///         4. With <see cref="ToolResultViewOptions" />, tool results at or below the persisted clear
///         watermark become placeholders and results over the cap (or the persisted tightened share of it) are
///         trimmed to head + marker + tail; the rows themselves are never edited.
///     </para>
///     <para>
///         5. With an active checkpoint the cut is then swept for tool messages it orphaned
///         (<c>MessagePersistenceConverter.DropUnpairedToolMessages</c>): a call whose result the cut hid,
///         or a result whose call it hid, is dropped rather than sent as a half a provider rejects.
///     </para>
///     <para>
///         Replaying the store produces the same view (§8): nothing here reads a clock, a counter, or
///         anything but its arguments.
///     </para>
/// </remarks>
internal sealed class AgentContextProjection
{
    /// <summary>The stateless default instance.</summary>
    public static readonly AgentContextProjection Default = new();

    private readonly Func<IMessage, long> _estimator;

    public AgentContextProjection(Func<IMessage, long>? estimator = null)
    {
        _estimator = estimator ?? CompactionTokenEstimate.Default;
    }

    /// <summary>
    ///     The execution view over an in-memory history snapshot, numbered positionally
    ///     (<see cref="SequencedHistory.FromSnapshot" />).
    /// </summary>
    public IReadOnlyList<IMessage> Build(
        string? systemPrompt,
        IReadOnlyList<IMessage> history,
        CompactionCheckpointMessage? active,
        CheckpointRenderOptions? render = null,
        ToolResultViewOptions? toolResults = null
    ) => Build(systemPrompt, SequencedHistory.FromSnapshot(history), active, render, toolResults);

    /// <summary>
    ///     The execution view over sequenced canonical rows. <paramref name="toolResults" /> clears and trims
    ///     tool results in the view only (<see cref="ToolResultView" />); null dispatches every row as it is.
    /// </summary>
    public IReadOnlyList<IMessage> Build(
        string? systemPrompt,
        IReadOnlyList<SequencedMessage> history,
        CompactionCheckpointMessage? active,
        CheckpointRenderOptions? render = null,
        ToolResultViewOptions? toolResults = null
    )
    {
        ArgumentNullException.ThrowIfNull(history);

        var view = new List<IMessage>(history.Count + 2);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            view.Add(new TextMessage { Text = systemPrompt, Role = Role.System });
        }

        if (active is not null)
        {
            view.Add(
                new TextMessage
                {
                    Text = active.RenderEnvelope(render ?? CheckpointRenderOptions.Default),
                    Role = Role.User,
                }
            );
        }

        var boundary = active?.Boundary.Seq ?? 0;
        foreach (var row in history)
        {
            if (row.IsCheckpointRow || row.Seq <= boundary)
            {
                continue;
            }

            view.Add(toolResults is null ? row.Message : ToolResultView.Apply(row.Message, row.Seq, toolResults));
        }

        // The cut removes rows by seq alone, so it can hide a tool call while its result stays: a row
        // whose seq the recovery walk never matched is numbered long.MaxValue and rides into the tail
        // whatever its age. Every provider rejects the surviving half with a 400 — OpenAI as "No tool
        // call found for function call output" — and nothing downstream repairs it, so the half goes
        // with the one the cut took. Without a checkpoint nothing is removed and nothing can orphan.
        return active is null ? view : MessagePersistenceConverter.DropUnpairedToolMessages(view);
    }

    /// <summary>Counts and sizes of the view <see cref="Build(string?, IReadOnlyList{SequencedMessage}, CompactionCheckpointMessage?, CheckpointRenderOptions?, ToolResultViewOptions?)" /> would produce.</summary>
    public ExecutionViewDescriptor Describe(
        IReadOnlyList<SequencedMessage> history,
        CompactionCheckpointMessage? active,
        CheckpointRenderOptions? render = null
    )
    {
        ArgumentNullException.ThrowIfNull(history);

        var boundary = active?.Boundary.Seq ?? 0;
        var hidden = 0;
        foreach (var row in history)
        {
            if (!row.IsCheckpointRow && row.Seq <= boundary)
            {
                hidden++;
            }
        }

        // The tail is counted off the view rather than off the rows above the boundary, so a row the
        // orphan sweep dropped is not reported as dispatched. No system prompt is passed, so the only
        // non-row entry is the envelope.
        var view = Build(systemPrompt: null, history, active, render);
        return new ExecutionViewDescriptor(
            active?.CheckpointId,
            active?.Boundary.Seq,
            hidden,
            view.Count - (active is null ? 0 : 1),
            CompactionTokenEstimate.Estimate(view, _estimator)
        );
    }
}
