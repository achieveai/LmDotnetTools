using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// In-memory implementation of IConversationStore for testing and development.
/// Thread-safe using ConcurrentDictionary.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore, IRunLedgerStore, IRunLifecycleStore
{
    private readonly ConcurrentDictionary<string, List<PersistedMessage>> _messages = new();
    private readonly ConcurrentDictionary<string, ThreadMetadata> _metadata = new();
    private readonly ConcurrentDictionary<string, RunLedgerEntry> _runLedger = new();
    private readonly ConcurrentDictionary<(string ThreadId, string InputId), AcceptedInputEntry> _acceptedInputs = new();
    private readonly Dictionary<string, RunLifecycleState> _runLifecycle = new(StringComparer.Ordinal);
    private readonly object _messagesLock = new();
    private readonly object _metadataLock = new();

    // Every lifecycle mutation is a compare-and-set, so they all serialize on one lock rather than
    // living in a ConcurrentDictionary that would let two terminalizations both believe they won.
    private readonly object _lifecycleLock = new();

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
    public Task ReplaceMessageAsync(
        string threadId,
        PersistedMessage replacement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(replacement);

        lock (_messagesLock)
        {
            if (!_messages.TryGetValue(threadId, out var threadMessages))
            {
                throw new InvalidOperationException(
                    $"Thread '{threadId}' not found; cannot replace message '{replacement.Id}'.");
            }

            var idx = threadMessages.FindIndex(m => m.Id == replacement.Id);
            if (idx < 0)
            {
                throw new InvalidOperationException(
                    $"Message '{replacement.Id}' not found in thread '{threadId}'.");
            }

            // Preserve the original timestamp so load ordering remains stable across replacement.
            threadMessages[idx] = replacement with { Timestamp = threadMessages[idx].Timestamp };
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
    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(update);

        // Serialize the read-modify-write so two concurrent property-bag updates for the same thread
        // cannot clobber each other (matches FileConversationStore's atomic UpdateMetadataAsync).
        lock (_metadataLock)
        {
            _ = _metadata.TryGetValue(threadId, out var existing);
            _metadata[threadId] = update(existing);
        }

        return Task.CompletedTask;
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

        foreach (var runId in _runLedger
            .Where(kvp => kvp.Value.ThreadId == threadId)
            .Select(kvp => kvp.Key)
            .ToList())
        {
            _ = _runLedger.TryRemove(runId, out _);
        }

        foreach (var key in _acceptedInputs.Keys.Where(k => k.ThreadId == threadId).ToList())
        {
            _ = _acceptedInputs.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var allThreadIds = GetAllThreadIds();
        var metadataList = new List<ThreadMetadata>();

        foreach (var threadId in allThreadIds)
        {
            if (_metadata.TryGetValue(threadId, out var metadata))
            {
                metadataList.Add(metadata);
            }
            else
            {
                // Thread has messages but no metadata - create minimal entry
                long lastUpdated;
                lock (_messagesLock)
                {
                    lastUpdated = _messages.TryGetValue(threadId, out var messages) && messages.Count > 0
                        ? messages.Max(m => m.Timestamp)
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                metadataList.Add(new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = lastUpdated,
                });
            }
        }

        var result = metadataList
            .OrderByDescending(m => m.LastUpdated)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<ThreadMetadata>>(result);
    }

    /// <inheritdoc />
    public Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _runLedger[entry.RunId] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
    {
        _ = _runLedger.TryGetValue(runId, out var entry);
        return Task.FromResult(entry);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
        string threadId,
        CancellationToken ct = default)
    {
        var result = _runLedger.Values
            .Where(e => e.ThreadId == threadId)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<RunLedgerEntry>>(result);
    }

    /// <inheritdoc />
    public Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default)
    {
        _acceptedInputs[(threadId, inputId)] = new AcceptedInputEntry(threadId, inputId, acceptedAt);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAcceptedInputAsync(string threadId, string inputId, CancellationToken ct = default)
    {
        _ = _acceptedInputs.TryRemove((threadId, inputId), out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        var result = new HashSet<string>(
            _acceptedInputs.Keys.Where(k => k.ThreadId == threadId).Select(k => k.InputId));

        return Task.FromResult<IReadOnlySet<string>>(result);
    }

    /// <inheritdoc />
    public Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        RunLifecycleGuards.ValidateStart(state);

        lock (_lifecycleLock)
        {
            if (_runLifecycle.TryGetValue(state.RunId, out var existing)
                && existing.Phase == RunLifecyclePhase.Terminal)
            {
                throw new InvalidOperationException(
                    $"Run '{state.RunId}' already reached a terminal boundary; it cannot be restarted.");
            }

            // A re-record refreshes how the run describes itself; it does not roll back what the
            // run has already committed. Deferrals and turns are facts recorded after the start,
            // and SQLite's upsert leaves both alone — the three stores have to agree.
            _runLifecycle[state.RunId] = state with
            {
                UpdatedAt = state.StartedAt,
                TurnCount = existing?.TurnCount ?? state.TurnCount,
                DeferredToolCalls = existing?.DeferredToolCalls ?? state.DeferredToolCalls,
            };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RunLifecycleState?> LoadRunLifecycleAsync(string runId, CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            _ = _runLifecycle.TryGetValue(runId, out var state);
            return Task.FromResult(state);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
        string threadId,
        CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            var result = _runLifecycle.Values
                .Where(s => s.ThreadId == threadId)
                .OrderByDescending(s => s.StartedAt)
                .ThenBy(s => s.RunId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<RunLifecycleState>>(result);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            var result = _runLifecycle.Values
                .Where(s => s.ThreadId == threadId && s.Phase == RunLifecyclePhase.Running)
                .OrderBy(s => s.StartedAt)
                .ThenBy(s => s.RunId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<RunLifecycleState>>(result);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryMarkRunTerminalAsync(
        string runId,
        string outcome,
        int turnCount,
        DateTimeOffset terminalAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(outcome);

        lock (_lifecycleLock)
        {
            if (!_runLifecycle.TryGetValue(runId, out var existing)
                || existing.Phase == RunLifecyclePhase.Terminal)
            {
                return Task.FromResult(false);
            }

            _runLifecycle[runId] = existing with
            {
                Phase = RunLifecyclePhase.Terminal,
                Outcome = outcome,
                TurnCount = turnCount,
                TerminalAt = terminalAt,
                UpdatedAt = terminalAt,
            };

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_lifecycleLock)
        {
            if (!_runLifecycle.TryGetValue(runId, out var existing))
            {
                throw new InvalidOperationException(
                    $"Run '{runId}' was never recorded as started; cannot record deferral '{record.ToolCallId}'.");
            }

            var already = existing.DeferredToolCalls
                .FirstOrDefault(d => d.ToolCallId == record.ToolCallId);
            if (already != null)
            {
                return Task.FromResult(already);
            }

            var committed = record with { Ordinal = existing.DeferredToolCalls.Count + 1 };
            _runLifecycle[runId] = existing with
            {
                DeferredToolCalls = [.. existing.DeferredToolCalls, committed],
                UpdatedAt = record.DeferredAt,
            };

            return Task.FromResult(committed);
        }
    }

    /// <inheritdoc />
    public Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string threadId,
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(resolutionFingerprint);

        lock (_lifecycleLock)
        {
            foreach (var state in _runLifecycle.Values.Where(s => s.ThreadId == threadId))
            {
                var index = RunLifecycleGuards.IndexOfDeferral(state, toolCallId);
                if (index < 0)
                {
                    continue;
                }

                var existing = state.DeferredToolCalls[index];
                if (existing.IsResolved)
                {
                    return Task.FromResult(
                        string.Equals(
                            existing.ResolutionFingerprint,
                            resolutionFingerprint,
                            StringComparison.Ordinal)
                            ? DeferredResolutionOutcome.Duplicate
                            : DeferredResolutionOutcome.Conflict);
                }

                var updated = state.DeferredToolCalls.ToArray();
                updated[index] = existing with
                {
                    ResolvedAt = resolvedAt,
                    ResolutionFingerprint = resolutionFingerprint,
                    ChildRunId = childRunId,
                };

                _runLifecycle[state.RunId] = state with
                {
                    DeferredToolCalls = updated,
                    UpdatedAt = resolvedAt,
                };

                return Task.FromResult(DeferredResolutionOutcome.Resolved);
            }

            return Task.FromResult(DeferredResolutionOutcome.NotFound);
        }
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
        return [.. _messages.Keys.Union(_metadata.Keys).Distinct()];
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
        _runLedger.Clear();
        _acceptedInputs.Clear();
    }
}
