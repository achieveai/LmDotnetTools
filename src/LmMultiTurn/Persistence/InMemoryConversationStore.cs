using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// In-memory implementation of IConversationStore for testing and development.
/// Thread-safe using ConcurrentDictionary.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, List<PersistedMessage>> _messages = new();
    private readonly ConcurrentDictionary<string, ThreadMetadata> _metadata = new();
    private readonly object _messagesLock = new();

    /// <inheritdoc />
    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (_messagesLock)
        {
            var threadMessages = _messages.GetOrAdd(threadId, _ => []);
            threadMessages.AddRange(messages);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default)
    {
        lock (_messagesLock)
        {
            if (_messages.TryGetValue(threadId, out var messages))
            {
                // Return a copy ordered by timestamp
                var result = messages
                    .OrderBy(m => m.Timestamp)
                    .ThenBy(m => m.MessageOrderIdx ?? 0)
                    .ToList();
                return Task.FromResult<IReadOnlyList<PersistedMessage>>(result);
            }
        }

        return Task.FromResult<IReadOnlyList<PersistedMessage>>([]);
    }

    /// <inheritdoc />
    public Task SaveMetadataAsync(
        string threadId,
        ThreadMetadata metadata,
        CancellationToken ct = default)
    {
        _metadata[threadId] = metadata;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ThreadMetadata?> LoadMetadataAsync(
        string threadId,
        CancellationToken ct = default)
    {
        _ = _metadata.TryGetValue(threadId, out var metadata);
        return Task.FromResult(metadata);
    }

    /// <inheritdoc />
    public Task DeleteThreadAsync(
        string threadId,
        CancellationToken ct = default)
    {
        lock (_messagesLock)
        {
            _ = _messages.TryRemove(threadId, out _);
        }

        _ = _metadata.TryRemove(threadId, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the count of messages for a thread. Useful for testing.
    /// </summary>
    public int GetMessageCount(string threadId)
    {
        lock (_messagesLock)
        {
            return _messages.TryGetValue(threadId, out var messages) ? messages.Count : 0;
        }
    }

    /// <summary>
    /// Gets all thread IDs in the store. Useful for testing.
    /// </summary>
    public IReadOnlyList<string> GetAllThreadIds()
    {
        return _messages.Keys.Union(_metadata.Keys).Distinct().ToList();
    }

    /// <summary>
    /// Clears all data from the store. Useful for testing.
    /// </summary>
    public void Clear()
    {
        lock (_messagesLock)
        {
            _messages.Clear();
        }

        _metadata.Clear();
    }
}
