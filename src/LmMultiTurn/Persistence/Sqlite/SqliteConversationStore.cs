using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IConversationStore"/>.
/// Uses a factory pattern for connection pooling and lazy schema initialization.
/// </summary>
public sealed class SqliteConversationStore : IConversationStore, IAsyncDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly bool _ownsFactory;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
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

                command.Parameters.AddWithValue("$id", message.Id);
                command.Parameters.AddWithValue("$thread_id", message.ThreadId);
                command.Parameters.AddWithValue("$run_id", message.RunId);
                command.Parameters.AddWithValue("$parent_run_id", (object?)message.ParentRunId ?? DBNull.Value);
                command.Parameters.AddWithValue("$generation_id", (object?)message.GenerationId ?? DBNull.Value);
                command.Parameters.AddWithValue("$message_order_idx", (object?)message.MessageOrderIdx ?? DBNull.Value);
                command.Parameters.AddWithValue("$timestamp", message.Timestamp);
                command.Parameters.AddWithValue("$message_type", message.MessageType);
                command.Parameters.AddWithValue("$role", message.Role);
                command.Parameters.AddWithValue("$from_agent", (object?)message.FromAgent ?? DBNull.Value);
                command.Parameters.AddWithValue("$message_json", message.MessageJson);

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        command.Parameters.AddWithValue("$thread_id", threadId);

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

        command.Parameters.AddWithValue("$thread_id", threadId);
        command.Parameters.AddWithValue("$current_run_id", (object?)metadata.CurrentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_updated", metadata.LastUpdated);
        command.Parameters.AddWithValue("$metadata_json", (object?)metadataJson ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        command.Parameters.AddWithValue("$thread_id", threadId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadMetadata(reader);
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
            deleteMessagesCmd.Parameters.AddWithValue("$thread_id", threadId);
            await deleteMessagesCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var deleteMetadataCmd = connection.CreateCommand();
            deleteMetadataCmd.Transaction = transaction;
            deleteMetadataCmd.CommandText = "DELETE FROM thread_metadata WHERE thread_id = $thread_id;";
            deleteMetadataCmd.Parameters.AddWithValue("$thread_id", threadId);
            await deleteMetadataCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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
            _schemaLock.Release();
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
