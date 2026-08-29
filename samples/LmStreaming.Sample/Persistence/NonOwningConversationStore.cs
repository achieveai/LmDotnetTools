using System.Collections.Immutable;
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
/// It is also the one seam that knows BOTH the parent conversation and the child thread id, so it
/// carries the second job the shared store needs: stamping <see cref="SubAgentProvenance"/> onto the
/// child's metadata writes. See <see cref="SubAgentProvenance"/> for why that link has to be durable.
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
    private readonly string? _provenanceThreadId;
    private readonly Func<ImmutableDictionary<string, object>>? _provenance;

    /// <summary>
    /// Creates a non-owning wrapper over <paramref name="store"/> that forwards metadata writes
    /// unchanged.
    /// </summary>
    /// <param name="store">The shared store to forward to. Never disposed by this wrapper.</param>
    public NonOwningConversationStore(IConversationStore store)
        : this(store, provenanceThreadId: null, provenance: null) { }

    /// <summary>
    /// Creates a non-owning wrapper over <paramref name="store"/>. If the wrapped store also
    /// implements <see cref="IRunLedgerStore"/> (the sample's <c>FileConversationStore</c> does),
    /// run-ledger members forward to it; otherwise invoking a run-ledger member throws
    /// <see cref="NotSupportedException"/>, mirroring a store that never supported the ledger.
    /// </summary>
    /// <param name="store">The shared store to forward to. Never disposed by this wrapper.</param>
    /// <param name="provenanceThreadId">
    /// The single thread whose metadata writes are stamped — the child this store was handed to.
    /// Writes for any other thread pass through untouched, so a child that persists something on
    /// behalf of another conversation (e.g. the usage projection, which writes under the ROOT
    /// conversation id) can never be mislabelled as that conversation's own child.
    /// </param>
    /// <param name="provenance">
    /// Supplies the properties to merge into each stamped write. Resolved per write, not once: the
    /// sub-agent's identity is only knowable from the live manager, which the caller cannot query
    /// before the parent loop exists.
    /// </param>
    public NonOwningConversationStore(
        IConversationStore store,
        string? provenanceThreadId,
        Func<ImmutableDictionary<string, object>>? provenance
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        _conversation = store;
        _runLedger = store as IRunLedgerStore;
        _provenanceThreadId = provenanceThreadId;
        _provenance = provenance;
    }

    private IRunLedgerStore RunLedger =>
        _runLedger
        ?? throw new NotSupportedException("The wrapped conversation store does not implement IRunLedgerStore.");

    // === IConversationStore ===

    /// <inheritdoc />
    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    ) => _conversation.AppendMessagesAsync(threadId, messages, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
        _conversation.LoadMessagesAsync(threadId, ct);

    /// <inheritdoc />
    public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default) =>
        _conversation.ReplaceMessageAsync(threadId, replacement, ct);

    /// <inheritdoc />
    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return _conversation.SaveMetadataAsync(threadId, Stamp(metadata, ResolveStamp(threadId)), ct);
    }

    /// <inheritdoc />
    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
        _conversation.LoadMetadataAsync(threadId, ct);

    /// <inheritdoc />
    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    )
    {
        // Resolve BEFORE handing the callback to the store: `update` runs while the store holds its
        // write lock, and the provenance supplier walks the parent's live sub-agent registry.
        var stamp = ResolveStamp(threadId);
        return _conversation.UpdateMetadataAsync(threadId, existing => Stamp(update(existing), stamp), ct);
    }

    /// <summary>
    /// Returns the properties to stamp for <paramref name="threadId"/>, or null when this store was
    /// created without provenance or the write targets some other thread.
    /// </summary>
    private ImmutableDictionary<string, object>? ResolveStamp(string threadId) =>
        _provenance is not null && string.Equals(threadId, _provenanceThreadId, StringComparison.Ordinal)
            ? _provenance()
            : null;

    /// <summary>
    /// Merges <paramref name="stamp"/> into the metadata's property bag, overwriting same-named keys
    /// (the stamp is derived state, so the freshest resolution wins) and leaving every other property
    /// — the usage projection's records above all — untouched. A stamped value that is
    /// <see cref="SubAgentProvenance.RemovalMarker"/> (by reference) REMOVES the key instead of
    /// writing it — <see cref="SubAgentProvenance.Build"/> uses this to actually clear a stale
    /// <see cref="SubAgentProvenance.TerminalAtKey"/> left by a prior terminal transition once the
    /// child is Running again, since merely omitting the key from a later stamp would otherwise leave
    /// it in place forever (this merge never removes a key just because a new stamp doesn't mention it).
    /// </summary>
    private static ThreadMetadata Stamp(ThreadMetadata metadata, ImmutableDictionary<string, object>? stamp)
    {
        if (stamp is null || stamp.IsEmpty)
        {
            return metadata;
        }

        var builder = (metadata.Properties ?? ImmutableDictionary<string, object>.Empty).ToBuilder();
        foreach (var (key, value) in stamp)
        {
            if (ReferenceEquals(value, SubAgentProvenance.RemovalMarker))
            {
                _ = builder.Remove(key);
            }
            else
            {
                builder[key] = value;
            }
        }

        return metadata with
        {
            Properties = builder.ToImmutable(),
        };
    }

    /// <inheritdoc />
    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        _conversation.DeleteThreadAsync(threadId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => _conversation.ListThreadsAsync(limit, offset, options, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        ConversationListScope scope,
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => _conversation.ListThreadsAsync(scope, limit, offset, options, ct);

    // === IRunLedgerStore ===

    /// <inheritdoc />
    public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default) =>
        RunLedger.UpsertRunLedgerAsync(entry, ct);

    /// <inheritdoc />
    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
        RunLedger.LoadRunLedgerAsync(runId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default) =>
        RunLedger.ListRunLedgerAsync(threadId, ct);

    /// <inheritdoc />
    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    ) => RunLedger.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

    /// <inheritdoc />
    public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
        RunLedger.RemoveAcceptedInputAsync(threadId, inputId, ct);

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
        RunLedger.ListAcceptedInputIdsAsync(threadId, ct);
}
