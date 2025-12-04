using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// File-based implementation of IConversationStore.
/// Stores messages and metadata as JSON files in a directory structure.
/// </summary>
public sealed class FileConversationStore : IConversationStore
{
    private const string MessagesFileName = "messages.json";
    private const string MetadataFileName = "metadata.json";

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
