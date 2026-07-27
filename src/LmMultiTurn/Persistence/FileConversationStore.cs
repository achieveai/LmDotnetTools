using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// File-based implementation of IConversationStore.
/// Stores messages and metadata as JSON files in a directory structure.
/// </summary>
public sealed class FileConversationStore : IConversationStore, IRunLedgerStore, IRunLifecycleStore
{
    private const string MessagesFileName = "messages.json";
    private const string MetadataFileName = "metadata.json";
    private const string RunsFileName = "runs.json";
    private const string AcceptedInputsFileName = "accepted-inputs.json";

    // Deliberately a separate file from runs.json: the run ledger's shape is part of the status
    // API's contract, and lifecycle observation must be addable without rewriting it.
    private const string RunLifecycleFileName = "run-lifecycle.json";

    private readonly string _baseDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Creates a new FileConversationStore.
    /// </summary>
    /// <param name="baseDirectory">Base directory for storing conversation data.</param>
    public FileConversationStore(string baseDirectory)
    {
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
        _ = Directory.CreateDirectory(_baseDirectory);
    }

    /// <inheritdoc />
    public async Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return;
        }

        await _lock.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var messagesFile = Path.Combine(threadDir, MessagesFileName);
            var existingMessages = await LoadMessagesFromFileAsync(messagesFile, ct);

            var allMessages = existingMessages.Concat(messages).ToList();
            await WriteJsonFileAsync(messagesFile, allMessages, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReplaceMessageAsync(
        string threadId,
        PersistedMessage replacement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(replacement);

        await _lock.WaitAsync(ct);
        try
        {
            var messagesFile = Path.Combine(GetThreadDirectory(threadId), MessagesFileName);
            var existing = await LoadMessagesFromFileAsync(messagesFile, ct);
            var idx = existing.FindIndex(m => m.Id == replacement.Id);
            if (idx < 0)
            {
                throw new InvalidOperationException(
                    $"Message '{replacement.Id}' not found in thread '{threadId}'.");
            }

            // Preserve original timestamp so load ordering remains stable across replacement.
            existing[idx] = replacement with { Timestamp = existing[idx].Timestamp };
            await WriteJsonFileAsync(messagesFile, existing, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var messagesFile = Path.Combine(GetThreadDirectory(threadId), MessagesFileName);
            var messages = await LoadMessagesFromFileAsync(messagesFile, ct);

            return [.. messages.OrderBy(m => m.Timestamp).ThenBy(m => m.MessageOrderIdx ?? 0)];
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveMetadataAsync(
        string threadId,
        ThreadMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var metadataFile = Path.Combine(threadDir, MetadataFileName);
            await WriteJsonFileAsync(metadataFile, metadata, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ThreadMetadata?> LoadMetadataAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var metadataFile = Path.Combine(GetThreadDirectory(threadId), MetadataFileName);
            return await LoadJsonFileAsync<ThreadMetadata>(metadataFile, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(update);

        // Hold the lock across the read AND the write so a concurrent read-modify-write for the same
        // thread cannot interleave and clobber the other's properties (the provider-vs-workspace and
        // bindings-vs-title/preview lost-update race that dropped the persisted provider).
        await _lock.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var metadataFile = Path.Combine(threadDir, MetadataFileName);
            var existing = await LoadJsonFileAsync<ThreadMetadata>(metadataFile, ct);
            var updated = update(existing);
            await WriteJsonFileAsync(metadataFile, updated, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteThreadAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            if (Directory.Exists(threadDir))
            {
                Directory.Delete(threadDir, recursive: true);
            }
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_baseDirectory))
            {
                return [];
            }

            var directories = Directory.GetDirectories(_baseDirectory);
            var metadataList = new List<ThreadMetadata>();

            foreach (var dir in directories)
            {
                ct.ThrowIfCancellationRequested();

                var threadId = Path.GetFileName(dir);
                var metadataFile = Path.Combine(dir, MetadataFileName);
                var metadata = await LoadJsonFileAsync<ThreadMetadata>(metadataFile, ct);

                if (metadata != null)
                {
                    metadataList.Add(metadata);
                }
                else
                {
                    // Thread exists but has no metadata - create minimal entry
                    var messagesFile = Path.Combine(dir, MessagesFileName);
                    var messages = await LoadMessagesFromFileAsync(messagesFile, ct);
                    var lastUpdated = messages.Count > 0
                        ? messages.Max(m => m.Timestamp)
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    metadataList.Add(new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated = lastUpdated,
                    });
                }
            }

            return
            [
                .. metadataList
                    .OrderByDescending(m => m.LastUpdated)
                    .Skip(offset)
                    .Take(limit)
            ];
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _lock.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(entry.ThreadId);
            _ = Directory.CreateDirectory(threadDir);

            var runsFile = Path.Combine(threadDir, RunsFileName);
            var runs = await LoadJsonFileAsync<List<RunLedgerEntry>>(runsFile, ct) ?? [];

            var idx = runs.FindIndex(r => r.RunId == entry.RunId);
            if (idx >= 0)
            {
                runs[idx] = entry;
            }
            else
            {
                runs.Add(entry);
            }

            await WriteJsonFileAsync(runsFile, runs, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_baseDirectory))
            {
                return null;
            }

            // No threadId-from-runId index exists on disk, so scan each thread's runs.json.
            foreach (var dir in Directory.GetDirectories(_baseDirectory))
            {
                ct.ThrowIfCancellationRequested();

                var runsFile = Path.Combine(dir, RunsFileName);
                var runs = await LoadJsonFileAsync<List<RunLedgerEntry>>(runsFile, ct);
                var match = runs?.FirstOrDefault(r => r.RunId == runId);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var runsFile = Path.Combine(GetThreadDirectory(threadId), RunsFileName);
            var runs = await LoadJsonFileAsync<List<RunLedgerEntry>>(runsFile, ct) ?? [];

            return [.. runs.OrderByDescending(r => r.CreatedAt)];
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RecordAcceptedInputAsync(
        string threadId,
        string inputId,
        DateTimeOffset acceptedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        await _lock.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(threadId);
            _ = Directory.CreateDirectory(threadDir);

            var acceptedFile = Path.Combine(threadDir, AcceptedInputsFileName);
            var accepted = await LoadJsonFileAsync<List<AcceptedInputEntry>>(acceptedFile, ct) ?? [];

            var idx = accepted.FindIndex(a => a.InputId == inputId);
            var entry = new AcceptedInputEntry(threadId, inputId, acceptedAt);
            if (idx >= 0)
            {
                accepted[idx] = entry;
            }
            else
            {
                accepted.Add(entry);
            }

            await WriteJsonFileAsync(acceptedFile, accepted, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAcceptedInputAsync(
        string threadId,
        string inputId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        await _lock.WaitAsync(ct);
        try
        {
            var acceptedFile = Path.Combine(GetThreadDirectory(threadId), AcceptedInputsFileName);
            var accepted = await LoadJsonFileAsync<List<AcceptedInputEntry>>(acceptedFile, ct);
            if (accepted == null)
            {
                return;
            }

            if (accepted.RemoveAll(a => a.InputId == inputId) > 0)
            {
                await WriteJsonFileAsync(acceptedFile, accepted, ct);
            }
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var acceptedFile = Path.Combine(GetThreadDirectory(threadId), AcceptedInputsFileName);
            var accepted = await LoadJsonFileAsync<List<AcceptedInputEntry>>(acceptedFile, ct) ?? [];

            return accepted.Select(a => a.InputId).ToHashSet();
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RecordRunStartedAsync(RunLifecycleState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        RunLifecycleGuards.ValidateStart(state);

        await _lock.WaitAsync(ct);
        try
        {
            var threadDir = GetThreadDirectory(state.ThreadId);
            _ = Directory.CreateDirectory(threadDir);

            var file = Path.Combine(threadDir, RunLifecycleFileName);
            var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct) ?? [];

            var idx = runs.FindIndex(r => r.RunId == state.RunId);
            if (idx >= 0)
            {
                if (runs[idx].Phase == RunLifecyclePhase.Terminal)
                {
                    throw new InvalidOperationException(
                        $"Run '{state.RunId}' already reached a terminal boundary; it cannot be restarted.");
                }

                runs[idx] = state with { UpdatedAt = state.StartedAt };
            }
            else
            {
                runs.Add(state with { UpdatedAt = state.StartedAt });
            }

            await WriteJsonFileAsync(file, runs, ct);
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RunLifecycleState?> LoadRunLifecycleAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        await _lock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_baseDirectory))
            {
                return null;
            }

            // Same absence of a runId→threadId index as the run ledger: scan each thread's file.
            foreach (var dir in Directory.GetDirectories(_baseDirectory))
            {
                ct.ThrowIfCancellationRequested();

                var file = Path.Combine(dir, RunLifecycleFileName);
                var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct);
                var match = runs?.FirstOrDefault(r => r.RunId == runId);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLifecycleState>> ListRunLifecycleAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var runs = await LoadThreadLifecycleAsync(threadId, ct);
            return
            [
                .. runs
                    .OrderByDescending(r => r.StartedAt)
                    .ThenBy(r => r.RunId, StringComparer.Ordinal),
            ];
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLifecycleState>> ListNonTerminalRunsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await _lock.WaitAsync(ct);
        try
        {
            var runs = await LoadThreadLifecycleAsync(threadId, ct);
            return
            [
                .. runs
                    .Where(r => r.Phase == RunLifecyclePhase.Running)
                    .OrderBy(r => r.StartedAt)
                    .ThenBy(r => r.RunId, StringComparer.Ordinal),
            ];
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkRunTerminalAsync(
        string runId,
        string outcome,
        int turnCount,
        DateTimeOffset terminalAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentException.ThrowIfNullOrEmpty(outcome);

        await _lock.WaitAsync(ct);
        try
        {
            var located = await LocateRunAsync(runId, ct);
            if (located == null)
            {
                return false;
            }

            var (file, runs, idx) = located.Value;
            if (runs[idx].Phase == RunLifecyclePhase.Terminal)
            {
                return false;
            }

            runs[idx] = runs[idx] with
            {
                Phase = RunLifecyclePhase.Terminal,
                Outcome = outcome,
                TurnCount = turnCount,
                TerminalAt = terminalAt,
                UpdatedAt = terminalAt,
            };

            await WriteJsonFileAsync(file, runs, ct);
            return true;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DeferredToolCallRecord> RecordDeferredToolCallAsync(
        string runId,
        DeferredToolCallRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(record);

        await _lock.WaitAsync(ct);
        try
        {
            var located = await LocateRunAsync(runId, ct)
                ?? throw new InvalidOperationException(
                    $"Run '{runId}' was never recorded as started; cannot record deferral "
                        + $"'{record.ToolCallId}'.");

            var (file, runs, idx) = located;
            var state = runs[idx];

            var already = state.DeferredToolCalls.FirstOrDefault(d => d.ToolCallId == record.ToolCallId);
            if (already != null)
            {
                return already;
            }

            var committed = record with { Ordinal = state.DeferredToolCalls.Count + 1 };
            runs[idx] = state with
            {
                DeferredToolCalls = [.. state.DeferredToolCalls, committed],
                UpdatedAt = record.DeferredAt,
            };

            await WriteJsonFileAsync(file, runs, ct);
            return committed;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DeferredResolutionOutcome> TryResolveDeferredToolCallAsync(
        string threadId,
        string toolCallId,
        string resolutionFingerprint,
        string? childRunId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(toolCallId);
        ArgumentException.ThrowIfNullOrEmpty(resolutionFingerprint);

        await _lock.WaitAsync(ct);
        try
        {
            var file = Path.Combine(GetThreadDirectory(threadId), RunLifecycleFileName);
            var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct) ?? [];

            for (var runIdx = 0; runIdx < runs.Count; runIdx++)
            {
                var callIdx = RunLifecycleGuards.IndexOfDeferral(runs[runIdx], toolCallId);
                if (callIdx < 0)
                {
                    continue;
                }

                var existing = runs[runIdx].DeferredToolCalls[callIdx];
                if (existing.IsResolved)
                {
                    return string.Equals(
                        existing.ResolutionFingerprint,
                        resolutionFingerprint,
                        StringComparison.Ordinal)
                        ? DeferredResolutionOutcome.Duplicate
                        : DeferredResolutionOutcome.Conflict;
                }

                var updated = runs[runIdx].DeferredToolCalls.ToArray();
                updated[callIdx] = existing with
                {
                    ResolvedAt = resolvedAt,
                    ResolutionFingerprint = resolutionFingerprint,
                    ChildRunId = childRunId,
                };

                runs[runIdx] = runs[runIdx] with
                {
                    DeferredToolCalls = updated,
                    UpdatedAt = resolvedAt,
                };

                await WriteJsonFileAsync(file, runs, ct);
                return DeferredResolutionOutcome.Resolved;
            }

            return DeferredResolutionOutcome.NotFound;
        }
        finally
        {
            _ = _lock.Release();
        }
    }

    private async Task<List<RunLifecycleState>> LoadThreadLifecycleAsync(
        string threadId,
        CancellationToken ct)
    {
        var file = Path.Combine(GetThreadDirectory(threadId), RunLifecycleFileName);
        return await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct) ?? [];
    }

    /// <summary>
    /// Finds the thread file holding a run, its deserialized contents, and the run's index in it.
    /// Callers already hold <c>_lock</c>.
    /// </summary>
    private async Task<(string File, List<RunLifecycleState> Runs, int Index)?> LocateRunAsync(
        string runId,
        CancellationToken ct)
    {
        if (!Directory.Exists(_baseDirectory))
        {
            return null;
        }

        foreach (var dir in Directory.GetDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();

            var file = Path.Combine(dir, RunLifecycleFileName);
            var runs = await LoadJsonFileAsync<List<RunLifecycleState>>(file, ct);
            var idx = runs?.FindIndex(r => r.RunId == runId) ?? -1;
            if (idx >= 0)
            {
                return (file, runs!, idx);
            }
        }

        return null;
    }

    private string GetThreadDirectory(string threadId)
    {
        // Sanitize thread ID for filesystem safety
        var safeThreadId = string.Join("_", threadId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_baseDirectory, safeThreadId);
    }

    private static async Task<List<PersistedMessage>> LoadMessagesFromFileAsync(
        string filePath,
        CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<List<PersistedMessage>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // If file is corrupted, start fresh
            return [];
        }
    }

    private static async Task<T?> LoadJsonFileAsync<T>(string filePath, CancellationToken ct)
        where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteJsonFileAsync<T>(string filePath, T data, CancellationToken ct)
    {
        // Write to temp file first, then rename for atomic operation
        var tempFile = filePath + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOptions);

        await File.WriteAllTextAsync(tempFile, json, ct);

        // Atomic rename
        File.Move(tempFile, filePath, overwrite: true);
    }
}
