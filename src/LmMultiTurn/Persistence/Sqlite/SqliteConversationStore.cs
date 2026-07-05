using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IConversationStore"/> and <see cref="IRunLedgerStore"/>.
/// Uses a factory pattern for connection pooling and lazy schema initialization.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore, IRunLedgerStore, IAsyncDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly bool _ownsFactory;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _metadataWriteLock = new(1, 1);
    private bool _schemaInitialized;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Creates a SqliteConversationStore with the specified connection factory.
    /// </summary>
    /// <param name="connectionFactory">The connection factory to use.</param>
    /// <param name="ownsFactory">If true, the store will dispose the factory when disposed.</param>
    public SqliteConversationStore(ISqliteConnectionFactory connectionFactory, bool ownsFactory = false)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
        _ownsFactory = ownsFactory;
    }

    /// <summary>
    /// Creates a SqliteConversationStore with a new connection factory for the specified database path.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="maxConnections">Maximum number of concurrent connections.</param>
    public SqliteConversationStore(string databasePath, int maxConnections = 5)
        : this(new SqliteConnectionFactory(databasePath, maxConnections), ownsFactory: true)
    {
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

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();

        try
        {
            foreach (var message in messages)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO messages (
                        id, thread_id, run_id, parent_run_id, generation_id,
                        message_order_idx, timestamp, message_type, role, from_agent, message_json
                    ) VALUES (
                        $id, $thread_id, $run_id, $parent_run_id, $generation_id,
                        $message_order_idx, $timestamp, $message_type, $role, $from_agent, $message_json
                    );
                    """;

                _ = command.Parameters.AddWithValue("$id", message.Id);
                _ = command.Parameters.AddWithValue("$thread_id", message.ThreadId);
                _ = command.Parameters.AddWithValue("$run_id", message.RunId);
                _ = command.Parameters.AddWithValue("$parent_run_id", (object?)message.ParentRunId ?? DBNull.Value);
                _ = command.Parameters.AddWithValue("$generation_id", (object?)message.GenerationId ?? DBNull.Value);
                _ = command.Parameters.AddWithValue("$message_order_idx", (object?)message.MessageOrderIdx ?? DBNull.Value);
                _ = command.Parameters.AddWithValue("$timestamp", message.Timestamp);
                _ = command.Parameters.AddWithValue("$message_type", message.MessageType);
                _ = command.Parameters.AddWithValue("$role", message.Role);
                _ = command.Parameters.AddWithValue("$from_agent", (object?)message.FromAgent ?? DBNull.Value);
                _ = command.Parameters.AddWithValue("$message_json", message.MessageJson);

                _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
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

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        // Preserve the existing timestamp (don't update it on replace) so load ordering stays
        // stable when a deferred placeholder is later resolved.
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE messages SET
                run_id = $run_id,
                parent_run_id = $parent_run_id,
                generation_id = $generation_id,
                message_order_idx = $message_order_idx,
                message_type = $message_type,
                role = $role,
                from_agent = $from_agent,
                message_json = $message_json
            WHERE id = $id AND thread_id = $thread_id;
            """;

        _ = command.Parameters.AddWithValue("$id", replacement.Id);
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$run_id", replacement.RunId);
        _ = command.Parameters.AddWithValue("$parent_run_id", (object?)replacement.ParentRunId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$generation_id", (object?)replacement.GenerationId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$message_order_idx", (object?)replacement.MessageOrderIdx ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$message_type", replacement.MessageType);
        _ = command.Parameters.AddWithValue("$role", replacement.Role);
        _ = command.Parameters.AddWithValue("$from_agent", (object?)replacement.FromAgent ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$message_json", replacement.MessageJson);

        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rows == 0)
        {
            throw new InvalidOperationException(
                $"Message '{replacement.Id}' not found in thread '{threadId}'.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, thread_id, run_id, parent_run_id, generation_id,
                   message_order_idx, timestamp, message_type, role, from_agent, message_json
            FROM messages
            WHERE thread_id = $thread_id
            ORDER BY timestamp ASC, message_order_idx ASC;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        var messages = new List<PersistedMessage>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task SaveMetadataAsync(
        string threadId,
        ThreadMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(metadata);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        // Serialize extensible fields to JSON
        var metadataJson = SerializeMetadataExtensions(metadata);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO thread_metadata (thread_id, current_run_id, last_updated, metadata_json)
            VALUES ($thread_id, $current_run_id, $last_updated, $metadata_json)
            ON CONFLICT(thread_id) DO UPDATE SET
                current_run_id = excluded.current_run_id,
                last_updated = excluded.last_updated,
                metadata_json = excluded.metadata_json;
            """;

        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$current_run_id", (object?)metadata.CurrentRunId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$last_updated", metadata.LastUpdated);
        _ = command.Parameters.AddWithValue("$metadata_json", (object?)metadataJson ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ThreadMetadata?> LoadMetadataAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id, current_run_id, last_updated, metadata_json
            FROM thread_metadata
            WHERE thread_id = $thread_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return !await reader.ReadAsync(ct).ConfigureAwait(false)
            ? null
            : ReadMetadata(reader);
    }

    /// <inheritdoc />
    public async Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(update);

        // Serialize the read-modify-write so concurrent property-bag updates for the same thread cannot
        // clobber each other (matches the other stores' atomic UpdateMetadataAsync).
        await _metadataWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
            var updated = update(existing);
            await SaveMetadataAsync(threadId, updated, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _metadataWriteLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteThreadAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();

        try
        {
            using var deleteMessagesCmd = connection.CreateCommand();
            deleteMessagesCmd.Transaction = transaction;
            deleteMessagesCmd.CommandText = "DELETE FROM messages WHERE thread_id = $thread_id;";
            _ = deleteMessagesCmd.Parameters.AddWithValue("$thread_id", threadId);
            _ = await deleteMessagesCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var deleteMetadataCmd = connection.CreateCommand();
            deleteMetadataCmd.Transaction = transaction;
            deleteMetadataCmd.CommandText = "DELETE FROM thread_metadata WHERE thread_id = $thread_id;";
            _ = deleteMetadataCmd.Parameters.AddWithValue("$thread_id", threadId);
            _ = await deleteMetadataCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id, current_run_id, last_updated, metadata_json
            FROM thread_metadata
            ORDER BY last_updated DESC
            LIMIT $limit OFFSET $offset;
            """;
        _ = command.Parameters.AddWithValue("$limit", limit);
        _ = command.Parameters.AddWithValue("$offset", offset);

        var metadataList = new List<ThreadMetadata>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            metadataList.Add(ReadMetadata(reader));
        }

        return metadataList;
    }

    /// <inheritdoc />
    public async Task UpsertRunLedgerAsync(RunLedgerEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        var inputIdsJson = JsonSerializer.Serialize(entry.InputIds, JsonOptions);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_ledger (run_id, thread_id, status, input_ids, created_at, updated_at)
            VALUES ($run_id, $thread_id, $status, $input_ids, $created_at, $updated_at)
            ON CONFLICT(run_id) DO UPDATE SET
                thread_id = excluded.thread_id,
                status = excluded.status,
                input_ids = excluded.input_ids,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at;
            """;

        _ = command.Parameters.AddWithValue("$run_id", entry.RunId);
        _ = command.Parameters.AddWithValue("$thread_id", entry.ThreadId);
        _ = command.Parameters.AddWithValue("$status", entry.Status.ToString());
        _ = command.Parameters.AddWithValue("$input_ids", inputIdsJson);
        _ = command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToUnixTimeMilliseconds());
        _ = command.Parameters.AddWithValue("$updated_at", entry.UpdatedAt.ToUnixTimeMilliseconds());

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunLedgerEntry?> LoadRunLedgerAsync(string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, thread_id, status, input_ids, created_at, updated_at
            FROM run_ledger
            WHERE run_id = $run_id;
            """;
        _ = command.Parameters.AddWithValue("$run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return !await reader.ReadAsync(ct).ConfigureAwait(false)
            ? null
            : ReadRunLedgerEntry(reader);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunLedgerEntry>> ListRunLedgerAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, thread_id, status, input_ids, created_at, updated_at
            FROM run_ledger
            WHERE thread_id = $thread_id
            ORDER BY created_at DESC;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        var entries = new List<RunLedgerEntry>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(ReadRunLedgerEntry(reader));
        }

        return entries;
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

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accepted_inputs (thread_id, input_id, accepted_at)
            VALUES ($thread_id, $input_id, $accepted_at)
            ON CONFLICT(thread_id, input_id) DO UPDATE SET
                accepted_at = excluded.accepted_at;
            """;

        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$input_id", inputId);
        _ = command.Parameters.AddWithValue("$accepted_at", acceptedAt.ToUnixTimeMilliseconds());

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAcceptedInputAsync(
        string threadId,
        string inputId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(inputId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM accepted_inputs WHERE thread_id = $thread_id AND input_id = $input_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);
        _ = command.Parameters.AddWithValue("$input_id", inputId);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ListAcceptedInputIdsAsync(
        string threadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.GetConnectionAsync(ct)
            .ConfigureAwait(false);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_id FROM accepted_inputs WHERE thread_id = $thread_id;
            """;
        _ = command.Parameters.AddWithValue("$thread_id", threadId);

        var inputIds = new HashSet<string>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            _ = inputIds.Add(reader.GetString(0));
        }

        return inputIds;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _schemaLock.Dispose();
        _metadataWriteLock.Dispose();

        if (_ownsFactory)
        {
            await _connectionFactory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaInitialized)
        {
            return;
        }

        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaInitialized)
            {
                return;
            }

            await SqliteSchemaInitializer.InitializeSchemaAsync(_connectionFactory, ct)
                .ConfigureAwait(false);
            _schemaInitialized = true;
        }
        finally
        {
            _ = _schemaLock.Release();
        }
    }

    private static PersistedMessage ReadMessage(SqliteDataReader reader)
    {
        return new PersistedMessage
        {
            Id = reader.GetString(0),
            ThreadId = reader.GetString(1),
            RunId = reader.GetString(2),
            ParentRunId = reader.IsDBNull(3) ? null : reader.GetString(3),
            GenerationId = reader.IsDBNull(4) ? null : reader.GetString(4),
            MessageOrderIdx = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Timestamp = reader.GetInt64(6),
            MessageType = reader.GetString(7),
            Role = reader.GetString(8),
            FromAgent = reader.IsDBNull(9) ? null : reader.GetString(9),
            MessageJson = reader.GetString(10),
        };
    }

    private static RunLedgerEntry ReadRunLedgerEntry(SqliteDataReader reader)
    {
        var runId = reader.GetString(0);
        var threadId = reader.GetString(1);
        var status = Enum.Parse<RunStatus>(reader.GetString(2));
        var inputIds = JsonSerializer.Deserialize<List<string>>(reader.GetString(3), JsonOptions) ?? [];
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));

        return new RunLedgerEntry(threadId, runId, status, inputIds, createdAt, updatedAt);
    }

    private static ThreadMetadata ReadMetadata(SqliteDataReader reader)
    {
        var threadId = reader.GetString(0);
        var currentRunId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var lastUpdated = reader.GetInt64(2);
        var metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);

        var (sessionMappings, latestRunId, properties) = DeserializeMetadataExtensions(metadataJson);

        return new ThreadMetadata
        {
            ThreadId = threadId,
            CurrentRunId = currentRunId,
            LatestRunId = latestRunId,
            LastUpdated = lastUpdated,
            SessionMappings = sessionMappings,
            Properties = properties,
        };
    }

    private static string? SerializeMetadataExtensions(ThreadMetadata metadata)
    {
        if (metadata.SessionMappings == null &&
            metadata.LatestRunId == null &&
            metadata.Properties == null)
        {
            return null;
        }

        var extensionData = new MetadataExtensionData
        {
            LatestRunId = metadata.LatestRunId,
            SessionMappings = metadata.SessionMappings,
            Properties = metadata.Properties?.ToDictionary(x => x.Key, x => x.Value),
        };

        return JsonSerializer.Serialize(extensionData, JsonOptions);
    }

    private static (IReadOnlyDictionary<string, string>?, string?, ImmutableDictionary<string, object>?)
        DeserializeMetadataExtensions(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return (null, null, null);
        }

        try
        {
            var data = JsonSerializer.Deserialize<MetadataExtensionData>(json, JsonOptions);
            if (data == null)
            {
                return (null, null, null);
            }

            var properties = data.Properties?.ToImmutableDictionary();
            return (data.SessionMappings, data.LatestRunId, properties);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private sealed class MetadataExtensionData
    {
        public string? LatestRunId { get; set; }
        public IReadOnlyDictionary<string, string>? SessionMappings { get; set; }
        public Dictionary<string, object>? Properties { get; set; }
    }
}
