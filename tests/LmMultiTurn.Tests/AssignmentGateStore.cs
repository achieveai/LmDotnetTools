using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmMultiTurn.Tests;

/// <summary>
/// Holds the first run-ledger write that matches <paramref name="gate"/>, and the first metadata write
/// whose result matches <paramref name="metadataGate"/>, until the loop is stopped. Both are boundaries
/// after the loop has taken input and before that input reaches history.
/// </summary>
internal sealed class AssignmentGateStore(
    Func<RunLedgerEntry, bool> gate,
    Func<ThreadMetadata, bool>? metadataGate = null
) : IConversationStore, IRunLedgerStore
{
    private readonly InMemoryConversationStore _inner = new();
    private int _gated;
    private int _metadataGated;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _persisted = new(StringComparer.Ordinal);

    public TaskCompletionSource<RunLedgerEntry> AssignmentEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource MetadataEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource NoticeAPersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource MessageBPersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when a message containing <paramref name="marker"/> is persisted. Ask before that message can
    /// be written: a write that came first is not replayed.
    /// </summary>
    public Task Persisted(string marker) =>
        _persisted.GetOrAdd(marker, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
    {
        if (gate(entry) && Interlocked.Exchange(ref _gated, 1) == 0)
        {
            AssignmentEntered.TrySetResult(entry);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        await _inner.UpsertRunLedgerAsync(entry, ct);
    }

    public async Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    )
    {
        await _inner.AppendMessagesAsync(threadId, messages, ct);
        if (messages.Any(message => message.MessageJson.Contains("notice-A", StringComparison.Ordinal)))
            NoticeAPersisted.TrySetResult();
        if (messages.Any(message => message.MessageJson.Contains("message-B", StringComparison.Ordinal)))
            MessageBPersisted.TrySetResult();
        foreach (var (marker, persisted) in _persisted)
        {
            if (messages.Any(message => message.MessageJson.Contains(marker, StringComparison.Ordinal)))
                persisted.TrySetResult();
        }
    }

    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
        _inner.LoadMessagesAsync(threadId, ct);

    public Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default) =>
        _inner.ReplaceMessageAsync(threadId, replacement, ct);

    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
        _inner.SaveMetadataAsync(threadId, metadata, ct);

    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
        _inner.LoadMetadataAsync(threadId, ct);

    public async Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    )
    {
        if (
            metadataGate != null
            && metadataGate(update(await _inner.LoadMetadataAsync(threadId, ct)))
            && Interlocked.Exchange(ref _metadataGated, 1) == 0
        )
        {
            MetadataEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        await _inner.UpdateMetadataAsync(threadId, update, ct);
    }

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        _inner.DeleteThreadAsync(threadId, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => _inner.ListThreadsAsync(limit, offset, options, ct);

    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default) =>
        _inner.LoadRunLedgerAsync(runId, ct);

    public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(string threadId, CancellationToken ct = default) =>
        _inner.ListRunLedgerAsync(threadId, ct);

    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default
    ) => _inner.RecordAcceptedInputAsync(threadId, inputId, acceptedAt, ct);

    public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default) =>
        _inner.RemoveAcceptedInputAsync(threadId, inputId, ct);

    public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(string threadId, CancellationToken ct = default) =>
        _inner.ListAcceptedInputIdsAsync(threadId, ct);
}
