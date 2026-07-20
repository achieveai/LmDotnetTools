using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// Non-owning decorator over the sample's shared conversation store. It forwards every
/// <see cref="IConversationStore"/> and <see cref="IRunLedgerStore"/> member to the wrapped instance
/// but deliberately implements NEITHER <see cref="IDisposable"/> NOR <see cref="IAsyncDisposable"/>.
/// </summary>
/// <remarks>
/// <para>
/// Spawned sub-agents receive their conversation store from
/// <c>SubAgentOptions.DefaultConversationStoreFactory</c>, and <c>SubAgentManager</c> treats a child
/// store that is <see cref="IAsyncDisposable"/> as child-owned — disposing it during spawn-cleanup,
/// restart, completion, and construction rollback. Because the sample hands the SAME application-wide
/// store to every child, a child tearing down could otherwise dispose storage still in use by the
/// parent and other conversations. Wrapping the shared store here severs that ownership: the
/// manager's <c>store is IAsyncDisposable</c> checks all skip this wrapper, so a child can never
/// dispose the shared store.
/// </para>
/// <para>
/// The wrapper is cheap and stateless, so allocating one per factory call is fine. The underlying
/// store owns its own lifetime and is disposed by the host, not by any child.
/// </para>
/// </remarks>
public sealed class NonOwningConversationStore : IConversationStore, IRunLedgerStore
{
    private readonly IConversationStore _conversation;
    private readonly IRunLedgerStore? _runLedger;

    /// <summary>
    /// Creates a non-owning wrapper over <paramref name="store"/>. If the wrapped store also
    /// implements <see cref="IRunLedgerStore"/> (the sample's <c>FileConversationStore</c> does),
    /// run-ledger members forward to it; otherwise invoking a run-ledger member throws
    /// <see cref="NotSupportedException"/>, mirroring a store that never supported the ledger.
    /// </summary>
    /// <param name="store">The shared store to forward to. Never disposed by this wrapper.</param>
    public NonOwningConversationStore(IConversationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _conversation = store;
        _runLedger = store as IRunLedgerStore;
    }

    private IRunLedgerStore RunLedger =>
        _runLedger
        ?? throw new NotSupportedException(
            "The wrapped conversation store does not implement IRunLedgerStore.");

    // === IConversationStore ===

    /// <inheritdoc />
    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default) =>
        _conversation.AppendMessagesAsync(threadId, messages, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default) =>
        _conversation.LoadMessagesAsync(threadId, ct);

    /// <inheritdoc />
    public Task ReplaceMessageAsync(
        string threadId,
        PersistedMessage replacement,
        CancellationToken ct = default) =>
        _conversation.ReplaceMessageAsync(threadId, replacement, ct);

    /// <inheritdoc />
    public Task SaveMetadataAsync(
        string threadId,
        ThreadMetadata metadata,
        CancellationToken ct = default) =>
        _conversation.SaveMetadataAsync(threadId, metadata, ct);

    /// <inheritdoc />
    public Task<ThreadMetadata?> LoadMetadataAsync(
        string threadId,
        CancellationToken ct = default) =>
        _conversation.LoadMetadataAsync(threadId, ct);

    /// <inheritdoc />
    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default) =>
        _conversation.UpdateMetadataAsync(threadId, update, ct);

    /// <inheritdoc />
    public Task DeleteThreadAsync(
        string threadId,
        CancellationToken ct = default) =>
        _conversation.DeleteThreadAsync(threadId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default) =>
        _conversation.ListThreadsAsync(limit, offset, ct);

    // === IRunLedgerStore ===

    /// <inheritdoc />
    public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
        RunLedger.UpsertRunLedgerAsync(entry, ct);

    /// <inheritdoc />
    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
        RunLedger.LoadRunLedgerAsync(runId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
        string threadId,
        CancellationToken ct = default) =>
        RunLedger.ListRunLedgerAsync(threadId, ct);

    /// <inheritdoc />
    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default) =>
        RunLedger.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

    /// <inheritdoc />
    public Task RemoveAcceptedInputAsync(
        string threadId,
        string inputId,
        CancellationToken ct = default) =>
        RunLedger.RemoveAcceptedInputAsync(threadId, inputId, ct);

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(
        string threadId,
        CancellationToken ct = default) =>
        RunLedger.ListAcceptedInputIdsAsync(threadId, ct);
}
