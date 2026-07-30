namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A read-through <see cref="IRunLedgerStore"/> view that holds every caller inside the accepted-input lookup
/// until <paramref name="participants"/> of them have arrived. Wired as the STATUS RESOLVER's ledger (the
/// controller keeps reserving against the real store), it pins the check-then-write window open: both sends
/// of one key are guaranteed to have finished asking "is this input already accepted?" — and been told no —
/// before either tries to claim it. Acceptance therefore has to be atomic to survive the test, rather than
/// passing because the two calls happened not to overlap.
/// </summary>
/// <param name="inner">The real ledger every call is forwarded to.</param>
/// <param name="participants">How many callers must arrive before any of them is released.</param>
internal sealed class RendezvousLedgerStore(IRunLedgerStore inner, int participants) : IRunLedgerStore
{
    private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        var accepted = await inner.ListAcceptedInputIdsAsync(threadId, ct);

        if (Interlocked.Increment(ref _arrived) >= participants)
        {
            _ = _allArrived.TrySetResult();
        }

        // Bounded so a mis-wired fixture fails loudly instead of hanging the run. It is a guard, never a
        // sleep: the wait ends the moment the last participant arrives.
        await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return accepted;
    }

    /// <inheritdoc />
    public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
        inner.UpsertRunLedgerAsync(entry, ct);

    /// <inheritdoc />
    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
        inner.LoadRunLedgerAsync(runId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
        string threadId,
        CancellationToken ct = default) =>
        inner.ListRunLedgerAsync(threadId, ct);

    /// <inheritdoc />
    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default) =>
        inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

    /// <inheritdoc />
    public Task<bool> TryReserveAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default) =>
        inner.TryReserveAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

    /// <inheritdoc />
    public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
        inner.RemoveAcceptedInputAsync(threadId, inputId, ct);
}
