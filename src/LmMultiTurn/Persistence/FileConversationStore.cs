using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// File-based implementation of IConversationStore.
/// Stores messages and metadata as JSON files in a directory structure.
/// </summary>
public sealed class FileConversationStore : IConversationStore, IRunLedgerStore
{
    private const string MessagesFileName = "messages.json";
    private const string MetadataFileName = "metadata.json";
    private const string RunsFileName = "runs.json";
    private const string AcceptedInputsFileName = "accepted-inputs.json";

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
