namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A read-through <see cref="IRunLedgerStore"/> view that performs a DRAIN — write the run row, then delete
/// the accepted-input marker, in exactly that order, as <c>MultiTurnAgentBase.StartRunAsync</c> does —
/// immediately after the FIRST accepted/run lookup any caller makes, and only once.
/// <para>
/// It exists to make the interleave a live status poll can hit deterministic: whichever set the resolver
/// consults first is answered from BEFORE the drain, and whatever it consults second is answered from after
/// it. A resolver that reads the run ledger first therefore sees no run row and then no acceptance, and
/// reports live work as unknown — which a caller answers by re-sending. Snapshotting the acceptance first
/// leaves the second read on the far side of the drain, where the run row now is.
/// </para>
/// </summary>
/// <param name="inner">The real ledger every call is forwarded to.</param>
/// <param name="drainedThreadId">The thread whose input is drained.</param>
/// <param name="drainedInputId">The input the drain folds into <paramref name="drainedRunId"/>.</param>
/// <param name="drainedRunId">The run the drained input is folded into.</param>
internal sealed class DrainingLedgerStore(
    IRunLedgerStore inner,
    string drainedThreadId,
    string drainedInputId,
    string drainedRunId) : IRunLedgerStore
{
    private int _lookups;

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        var accepted = await inner.ListAcceptedInputIdsAsync(threadId, ct);
        await DrainOnFirstLookupAsync(ct);
        return accepted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
        string threadId,
        CancellationToken ct = default)
    {
        var entries = await inner.ListRunLedgerAsync(threadId, ct);
        await DrainOnFirstLookupAsync(ct);
        return entries;
    }

    /// <summary>
    /// Runs the drain once, AFTER the lookup that triggered it has already been answered — so the caller
    /// leaves that lookup holding a pre-drain snapshot and meets post-drain state on its next one.
    /// </summary>
    private async Task DrainOnFirstLookupAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _lookups) != 1)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await inner.UpsertRunLedgerAsync(
            new RunLedgerEntry(drainedThreadId, drainedRunId, RunStatus.InProgress, [drainedInputId], now, now),
            ct);
        await inner.RemoveAcceptedInputAsync(drainedThreadId, drainedInputId, ct);
    }

    /// <inheritdoc />
    public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
        inner.UpsertRunLedgerAsync(entry, ct);

    /// <inheritdoc />
    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
        inner.LoadRunLedgerAsync(runId, ct);

    /// <inheritdoc />
    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default) =>
        inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

    /// <inheritdoc />
    public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
        inner.RemoveAcceptedInputAsync(threadId, inputId, ct);
}
