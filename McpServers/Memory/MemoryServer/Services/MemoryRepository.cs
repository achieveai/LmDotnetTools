using System.Globalization;
using System.Text.Json;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Data.Sqlite;

namespace MemoryServer.Services;

/// <summary>
/// SQLite-based repository for memory operations with session isolation and integer IDs.
/// Uses Database Session Pattern for reliable connection management.
/// </summary>
public class MemoryRepository : IMemoryRepository
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly MemoryIdGenerator _idGenerator;
    private readonly ILogger<MemoryRepository> _logger;

    public MemoryRepository(
        ISqliteSessionFactory sessionFactory,
        MemoryIdGenerator idGenerator,
        ILogger<MemoryRepository> logger
    )
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Adds a new memory to the repository.
    /// </summary>
    public async Task<Memory> AddAsync(
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Memory content cannot be empty", nameof(content));
        }

        if (content.Length > 10000)
        {
            throw new ArgumentException("Memory content cannot exceed 10,000 characters", nameof(content));
        }

        // Generate unique integer ID
        var id = await _idGenerator.GenerateNextIdAsync(cancellationToken);

        var memory = new Memory
        {
            Id = id,
            Content = content.Trim(),
            UserId = sessionContext.UserId,
            AgentId = sessionContext.AgentId,
            RunId = sessionContext.RunId,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Version = 1,
        };

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                INSERT INTO memories (id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version)
                VALUES (@id, @content, @userId, @agentId, @runId, @metadata, @createdAt, @updatedAt, @version)";

                _ = command.Parameters.AddWithValue("@id", memory.Id);
                _ = command.Parameters.AddWithValue("@content", memory.Content);
                _ = command.Parameters.AddWithValue("@userId", memory.UserId);
                _ = command.Parameters.AddWithValue("@agentId", memory.AgentId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue("@runId", memory.RunId ?? (object)DBNull.Value);
                _ = command.Parameters.AddWithValue(
                    "@metadata",
                    metadata != null ? JsonSerializer.Serialize(metadata) : (object)DBNull.Value
                );
                _ = command.Parameters.AddWithValue("@createdAt", memory.CreatedAt);
                _ = command.Parameters.AddWithValue("@updatedAt", memory.UpdatedAt);
                _ = command.Parameters.AddWithValue("@version", memory.Version);

                _ = await command.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation("Added memory {Id} for session {SessionContext}", memory.Id, sessionContext);
                return memory;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Gets a memory by its integer ID within the session context.
    /// </summary>
    public async Task<Memory?> GetByIdAsync(
        int id,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
                FROM memories 
                WHERE id = @id AND user_id = @userId";

                // Add session context filters
                if (!string.IsNullOrEmpty(sessionContext.AgentId))
                {
                    command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
                }

                if (!string.IsNullOrEmpty(sessionContext.RunId))
                {
                    command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
                }

                _ = command.Parameters.AddWithValue("@id", id);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                return await reader.ReadAsync(cancellationToken) ? ReadMemoryFromReader(reader) : null;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Updates an existing memory.
    /// </summary>
    public async Task<Memory?> UpdateAsync(
        int id,
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Memory content cannot be empty", nameof(content));
        }

        if (content.Length > 10000)
        {
            throw new ArgumentException("Memory content cannot exceed 10,000 characters", nameof(content));
        }

        // First get the existing memory to check permissions and get current version
        var existingMemory = await GetByIdAsync(id, sessionContext, cancellationToken);
        if (existingMemory == null)
        {
            return null;
        }

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                UPDATE memories 
                SET content = @content, metadata = @metadata, updated_at = @updatedAt, version = version + 1
                WHERE id = @id AND user_id = @userId AND version = @currentVersion";

                // Add session context filters for security
                if (!string.IsNullOrEmpty(sessionContext.AgentId))
                {
                    command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
                }

                if (!string.IsNullOrEmpty(sessionContext.RunId))
                {
                    command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
                }

                var updatedAt = DateTime.UtcNow;
                _ = command.Parameters.AddWithValue("@id", id);
                _ = command.Parameters.AddWithValue("@content", content.Trim());
                _ = command.Parameters.AddWithValue(
                    "@metadata",
                    metadata != null ? JsonSerializer.Serialize(metadata) : (object)DBNull.Value
                );
                _ = command.Parameters.AddWithValue("@updatedAt", updatedAt);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@currentVersion", existingMemory.Version);

                var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (rowsAffected == 0)
                {
                    // Memory was updated by another process (optimistic concurrency)
                    return null;
                }

                // Return updated memory
                var updatedMemory = existingMemory.WithUpdatedTimestamp();
                updatedMemory.Content = content.Trim();
                updatedMemory.Metadata = metadata;
                updatedMemory.UpdatedAt = updatedAt;

                _logger.LogInformation("Updated memory {Id} for session {SessionContext}", id, sessionContext);
                return updatedMemory;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Deletes a memory by its integer ID within the session context.
    /// </summary>
    public async Task<bool> DeleteAsync(
        int id,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                DELETE FROM memories 
                WHERE id = @id AND user_id = @userId";

                // Add session context filters for security
                if (!string.IsNullOrEmpty(sessionContext.AgentId))
                {
                    command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
                }

                if (!string.IsNullOrEmpty(sessionContext.RunId))
                {
                    command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
                }

                _ = command.Parameters.AddWithValue("@id", id);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);

                var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
                var deleted = rowsAffected > 0;

                if (deleted)
                {
                    _logger.LogInformation("Deleted memory {Id} for session {SessionContext}", id, sessionContext);
                }

                return deleted;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Gets all memories for a session context with pagination.
    /// </summary>
    public async Task<List<Memory>> GetAllAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
                FROM memories 
                WHERE user_id = @userId";

                // Add session context filters
                if (!string.IsNullOrEmpty(sessionContext.AgentId))
                {
                    command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
                }

                if (!string.IsNullOrEmpty(sessionContext.RunId))
                {
                    command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
                }

                command.CommandText += " ORDER BY created_at DESC LIMIT @limit OFFSET @offset";

                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@limit", limit);
                _ = command.Parameters.AddWithValue("@offset", offset);

                var memories = new List<Memory>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    memories.Add(ReadMemoryFromReader(reader));
                }

                return memories;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Searches memories using FTS5 or LIKE fallback.
    /// </summary>
    public async Task<List<Memory>> SearchAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        float scoreThreshold = 0.0f,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                try
                {
                    // Try FTS5 search first
                    return await SearchWithFts5Async(connection, query, sessionContext, limit, cancellationToken);
                }
                catch (SqliteException ex) when (ex.Message.Contains("no such table: memory_fts"))
                {
                    // Fallback to LIKE search if FTS5 is not available
                    _logger.LogWarning("FTS5 table not available, falling back to LIKE search");
                    return await SearchWithLikeAsync(connection, query, sessionContext, limit, cancellationToken);
                }
            },
            cancellationToken
        );
    }

    private async Task<List<Memory>> SearchWithFts5Async(
        SqliteConnection connection,
        string query,
        SessionContext sessionContext,
        int limit,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();

        // Use FTS5 MATCH for full-text search
        command.CommandText =
            @"
            SELECT m.id, m.content, m.user_id, m.agent_id, m.run_id, m.metadata, m.created_at, m.updated_at, m.version,
                   fts.rank
            FROM memory_fts fts
            JOIN memories m ON m.id = fts.rowid
            WHERE memory_fts MATCH @query 
              AND m.user_id = @userId";

        // Add session context filters
        if (!string.IsNullOrEmpty(sessionContext.AgentId))
        {
            command.CommandText += " AND (m.agent_id = @agentId OR m.agent_id IS NULL)";
            _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
        }

        if (!string.IsNullOrEmpty(sessionContext.RunId))
        {
            command.CommandText += " AND (m.run_id = @runId OR m.run_id IS NULL)";
            _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
        }

        command.CommandText += " ORDER BY fts.rank LIMIT @limit";

        // Escape FTS5 special characters and prepare query
        var escapedQuery = EscapeFts5Query(query);
        _ = command.Parameters.AddWithValue("@query", escapedQuery);
        _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        _ = command.Parameters.AddWithValue("@limit", limit);

        var memories = new List<Memory>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader);
            // Set score from FTS5 rank if available
            var rankOrdinal = reader.GetOrdinal("rank");
            if (!reader.IsDBNull(rankOrdinal))
            {
                memory.Score = reader.GetFloat(rankOrdinal);
            }
            memories.Add(memory);
        }

        return memories;
    }

    private async Task<List<Memory>> SearchWithLikeAsync(
        SqliteConnection connection,
        string query,
        SessionContext sessionContext,
        int limit,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            @"
            SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
            FROM memories 
            WHERE (content LIKE @query OR metadata LIKE @query)
              AND user_id = @userId";

        // Add session context filters
        if (!string.IsNullOrEmpty(sessionContext.AgentId))
        {
            command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
            _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
        }

        if (!string.IsNullOrEmpty(sessionContext.RunId))
        {
            command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
            _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
        }

        command.CommandText += " ORDER BY created_at DESC LIMIT @limit";

        var likeQuery = $"%{query}%";
        _ = command.Parameters.AddWithValue("@query", likeQuery);
        _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        _ = command.Parameters.AddWithValue("@limit", limit);

        var memories = new List<Memory>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            memories.Add(ReadMemoryFromReader(reader));
        }

        return memories;
    }

    /// <summary>
    /// Gets memory statistics for a session context.
    /// </summary>
    public async Task<MemoryStats> GetStatsAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT 
                    COUNT(*) as total_count,
                    SUM(LENGTH(content)) as total_content_size,
                    AVG(LENGTH(content)) as avg_content_length,
                    MIN(created_at) as oldest_memory,
                    MAX(created_at) as newest_memory
                FROM memories 
                WHERE user_id = @userId";

                // Add session context filters
                if (!string.IsNullOrEmpty(sessionContext.AgentId))
                {
                    command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
                }

                if (!string.IsNullOrEmpty(sessionContext.RunId))
                {
                    command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
                }

                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var totalCountOrdinal = reader.GetOrdinal("total_count");
                    var totalContentSizeOrdinal = reader.GetOrdinal("total_content_size");
                    var avgContentLengthOrdinal = reader.GetOrdinal("avg_content_length");
                    var oldestMemoryOrdinal = reader.GetOrdinal("oldest_memory");
                    var newestMemoryOrdinal = reader.GetOrdinal("newest_memory");

                    return new MemoryStats
                    {
                        TotalMemories = reader.GetInt32(totalCountOrdinal),
                        TotalContentSize = reader.IsDBNull(totalContentSizeOrdinal)
                            ? 0
                            : reader.GetInt64(totalContentSizeOrdinal),
                        AverageContentLength = reader.IsDBNull(avgContentLengthOrdinal)
                            ? 0
                            : reader.GetDouble(avgContentLengthOrdinal),
                        OldestMemory = reader.IsDBNull(oldestMemoryOrdinal)
                            ? (DateTime?)null
                            : reader.GetDateTime(oldestMemoryOrdinal),
                        NewestMemory = reader.IsDBNull(newestMemoryOrdinal)
                            ? (DateTime?)null
                            : reader.GetDateTime(newestMemoryOrdinal),
                    };
                }

                return new MemoryStats();
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Deletes all memories for a session context.
    /// </summary>
    public async Task<int> DeleteAllAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    @"
                DELETE FROM memories 
                WHERE user_id = @userId";

                // Add session context filters for security
                if (!string.IsNullOrEmpty(sessionContext.AgentId))
                {
                    command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
                }

                if (!string.IsNullOrEmpty(sessionContext.RunId))
                {
                    command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
                }

                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);

                var deletedCount = await command.ExecuteNonQueryAsync(cancellationToken);

                if (deletedCount > 0)
                {
                    _logger.LogInformation(
                        "Deleted {Count} memories for session {SessionContext}",
                        deletedCount,
                        sessionContext
                    );
                }

                return deletedCount;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Gets memory history (placeholder for future versioning support).
    /// </summary>
    public async Task<List<MemoryHistoryEntry>> GetHistoryAsync(
        int id,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    )
    {
        // For now, just return the current version as a single history entry
        var memory = await GetByIdAsync(id, sessionContext, cancellationToken);
        return memory == null
            ? []
            : [
            new MemoryHistoryEntry
            {
                MemoryId = memory.Id,
                Content = memory.Content,
                Version = memory.Version,
                CreatedAt = memory.UpdatedAt,
                ChangeType = "current",
            },
        ];
    }

    private Memory ReadMemoryFromReader(SqliteDataReader reader)
    {
        var idOrdinal = reader.GetOrdinal("id");
        var metadataOrdinal = reader.GetOrdinal("metadata");
        var contentOrdinal = reader.GetOrdinal("content");
        var userIdOrdinal = reader.GetOrdinal("user_id");
        var agentIdOrdinal = reader.GetOrdinal("agent_id");
        var runIdOrdinal = reader.GetOrdinal("run_id");
        var createdAtOrdinal = reader.GetOrdinal("created_at");
        var updatedAtOrdinal = reader.GetOrdinal("updated_at");
        var versionOrdinal = reader.GetOrdinal("version");

        var metadataJson = reader.IsDBNull(metadataOrdinal) ? null : reader.GetString(metadataOrdinal);
        Dictionary<string, object>? metadata = null;

        if (!string.IsNullOrEmpty(metadataJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize metadata for memory {Id}", reader.GetInt32(idOrdinal));
            }
        }

        return new Memory
        {
            Id = reader.GetInt32(idOrdinal),
            Content = reader.IsDBNull(contentOrdinal) ? string.Empty : reader.GetString(contentOrdinal),
            UserId = reader.IsDBNull(userIdOrdinal) ? string.Empty : reader.GetString(userIdOrdinal),
            AgentId = reader.IsDBNull(agentIdOrdinal) ? null : reader.GetString(agentIdOrdinal),
            RunId = reader.IsDBNull(runIdOrdinal) ? null : reader.GetString(runIdOrdinal),
            Metadata = metadata,
            CreatedAt = reader.GetDateTime(createdAtOrdinal),
            UpdatedAt = reader.GetDateTime(updatedAtOrdinal),
            Version = reader.GetInt32(versionOrdinal),
        };
    }

    private static string EscapeFts5Query(string query)
    {
        // Escape FTS5 special characters
        // FTS5 special characters: " ( ) * + - / : < = > ^ | ~ #
        return query
            .Replace("\"", "\"\"") // Escape double quotes
            .Replace("/", " ") // Replace forward slash with space (common in search)
            .Replace("(", " ") // Replace parentheses with spaces
            .Replace(")", " ")
            .Replace("*", " ") // Replace wildcard characters
            .Replace("+", " ")
            .Replace("-", " ")
            .Replace(":", " ") // Replace colon (used for field searches)
            .Replace("<", " ") // Replace comparison operators
            .Replace("=", " ")
            .Replace(">", " ")
            .Replace("^", " ") // Replace boost operator
            .Replace("|", " ") // Replace OR operator
            .Replace("~", " ") // Replace NOT operator
            .Replace("#", " "); // Replace hash symbol (used in work item IDs)
    }

    /// <summary>
    /// Checks if the memory_embeddings table is using vec0 virtual table.
    /// </summary>
    private static async Task<bool> CheckForVec0TableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            @"
            SELECT sql FROM sqlite_master 
            WHERE type='table' AND name='memory_embeddings' AND sql LIKE '%vec0%'";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null;
    }

    /// <summary>
    /// Checks if a specific table exists in the database.
    /// </summary>
    private static async Task<bool> CheckForTableAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            @"
            SELECT COUNT(*) FROM sqlite_master 
            WHERE type='table' AND name=@tableName";

        _ = command.Parameters.AddWithValue("@tableName", tableName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result != null && Convert.ToInt32(result) > 0;
    }

    // Vector storage and search methods

    /// <summary>
    /// Stores an embedding for a specific memory.
    /// </summary>
    public async Task StoreEmbeddingAsync(
        int memoryId,
        float[] embedding,
        string modelName,
        CancellationToken cancellationToken = default
    )
    {
        if (embedding == null || embedding.Length == 0)
        {
            throw new ArgumentException("Embedding cannot be empty", nameof(embedding));
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Model name cannot be empty", nameof(modelName));
        }

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        await session.ExecuteInTransactionAsync(
            async (connection, transaction) =>
            {
                // Convert embedding to JSON format for sqlite-vec (more reliable than byte conversion)
                var embeddingJson =
                    "[" + string.Join(",", embedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture))) + "]";

                // Check if we're using vec0 virtual table or regular table with dimension column
                var hasVec0Table = await CheckForVec0TableAsync(connection, cancellationToken);

                using var embeddingCommand = connection.CreateCommand();
                embeddingCommand.Transaction = transaction;

                if (hasVec0Table)
                {
                    // Use vec0 virtual table (production schema)
                    embeddingCommand.CommandText =
                        @"
                    INSERT OR REPLACE INTO memory_embeddings (memory_id, embedding)
                    VALUES (@memoryId, @embedding)";
                }
                else
                {
                    // Use regular table with dimension column (test schema)
                    embeddingCommand.CommandText =
                        @"
                    INSERT OR REPLACE INTO memory_embeddings (memory_id, embedding, dimension)
                    VALUES (@memoryId, @embedding, @dimension)";
                    _ = embeddingCommand.Parameters.AddWithValue("@dimension", embedding.Length);
                }

                _ = embeddingCommand.Parameters.AddWithValue("@memoryId", memoryId);
                _ = embeddingCommand.Parameters.AddWithValue("@embedding", embeddingJson);

                _ = await embeddingCommand.ExecuteNonQueryAsync(cancellationToken);

                // Store metadata if embedding_metadata table exists
                if (await CheckForTableAsync(connection, "embedding_metadata", cancellationToken))
                {
                    using var metadataCommand = connection.CreateCommand();
                    metadataCommand.Transaction = transaction;
                    metadataCommand.CommandText =
                        @"
                    INSERT OR REPLACE INTO embedding_metadata (memory_id, model_name, embedding_dimension, created_at)
                    VALUES (@memoryId, @modelName, @dimension, @createdAt)";

                    _ = metadataCommand.Parameters.AddWithValue("@memoryId", memoryId);
                    _ = metadataCommand.Parameters.AddWithValue("@modelName", modelName);
                    _ = metadataCommand.Parameters.AddWithValue("@dimension", embedding.Length);
                    _ = metadataCommand.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);

                    _ = await metadataCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                _logger.LogDebug(
                    "Stored embedding for memory {MemoryId} using model {ModelName} (dimension: {Dimension})",
                    memoryId,
                    modelName,
                    embedding.Length
                );
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Gets the embedding for a specific memory.
    /// </summary>
    public async Task<float[]?> GetEmbeddingAsync(int memoryId, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT embedding 
                FROM memory_embeddings 
                WHERE memory_id = @memoryId";

                _ = command.Parameters.AddWithValue("@memoryId", memoryId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var embeddingOrdinal = reader.GetOrdinal("embedding");
                    if (!reader.IsDBNull(embeddingOrdinal))
                    {
                        // sqlite-vec stores embeddings as BLOB, convert to float array
                        var embeddingBytes = (byte[])reader.GetValue(embeddingOrdinal);
                        var embedding = new float[embeddingBytes.Length / sizeof(float)];
                        Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
                        return embedding;
                    }
                }

                return null;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Performs vector similarity search to find memories similar to the query embedding.
    /// </summary>
    public async Task<List<VectorSearchResult>> SearchVectorAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default
    )
    {
        if (queryEmbedding == null || queryEmbedding.Length == 0)
        {
            throw new ArgumentException("Query embedding cannot be empty", nameof(queryEmbedding));
        }

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();

                // Use sqlite-vec for similarity search with session filtering
                command.CommandText =
                    @"
                SELECT m.id, m.content, m.user_id, m.agent_id, m.run_id, m.metadata, 
                       m.created_at, m.updated_at, m.version,
                       vec_distance_cosine(e.embedding, @queryEmbedding) as distance
                FROM memories m
                JOIN memory_embeddings e ON m.id = e.memory_id
                WHERE m.user_id = @userId
                  AND vec_distance_cosine(e.embedding, @queryEmbedding) <= @distanceThreshold";

                // Add session context filters
                if (!string.IsNullOrEmpty(sessionContext.AgentId))
                {
                    command.CommandText += " AND (m.agent_id = @agentId OR m.agent_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
                }

                if (!string.IsNullOrEmpty(sessionContext.RunId))
                {
                    command.CommandText += " AND (m.run_id = @runId OR m.run_id IS NULL)";
                    _ = command.Parameters.AddWithValue("@runId", sessionContext.RunId);
                }

                command.CommandText +=
                    @"
                ORDER BY distance ASC
                LIMIT @limit";

                // Convert similarity threshold to distance threshold (cosine distance = 1 - cosine similarity)
                var distanceThreshold = 1.0f - threshold;

                // Convert query embedding to JSON format for sqlite-vec (more reliable than byte conversion)
                var queryEmbeddingJson =
                    "["
                    + string.Join(",", queryEmbedding.Select(f => f.ToString("G", CultureInfo.InvariantCulture)))
                    + "]";

                _ = command.Parameters.AddWithValue("@queryEmbedding", queryEmbeddingJson);
                _ = command.Parameters.AddWithValue("@userId", sessionContext.UserId);
                _ = command.Parameters.AddWithValue("@distanceThreshold", distanceThreshold);
                _ = command.Parameters.AddWithValue("@limit", limit);

                var results = new List<VectorSearchResult>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var memory = ReadMemoryFromReader(reader);
                    var distanceOrdinal = reader.GetOrdinal("distance");
                    var distance = reader.GetFloat(distanceOrdinal);
                    var similarity = 1.0f - distance; // Convert distance back to similarity

                    results.Add(
                        new VectorSearchResult
                        {
                            Memory = memory,
                            Score = similarity,
                            Distance = distance,
                        }
                    );
                }

                _logger.LogDebug(
                    "Vector search returned {Count} results for session {SessionContext} with threshold {Threshold}",
                    results.Count,
                    sessionContext,
                    threshold
                );

                return results;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Performs hybrid search combining FTS5 and vector similarity search.
    /// </summary>
    public async Task<List<Memory>> SearchHybridAsync(
        string query,
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float traditionalWeight = 0.3f,
        float vectorWeight = 0.7f,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query cannot be empty", nameof(query));
        }

        if (queryEmbedding == null || queryEmbedding.Length == 0)
        {
            throw new ArgumentException("Query embedding cannot be empty", nameof(queryEmbedding));
        }

        // Perform both searches in parallel
        var traditionalSearchTask = SearchAsync(query, sessionContext, limit * 2, 0.0f, cancellationToken);
        var vectorSearchTask = SearchVectorAsync(queryEmbedding, sessionContext, limit * 2, 0.5f, cancellationToken);

        await Task.WhenAll(traditionalSearchTask, vectorSearchTask);

        var traditionalResults = await traditionalSearchTask;
        var vectorResults = await vectorSearchTask;

        // Combine and score results
        var combinedResults = new Dictionary<int, (Memory memory, float combinedScore)>();

        // Add traditional search results with their weights
        for (var i = 0; i < traditionalResults.Count; i++)
        {
            var memory = traditionalResults[i];
            var traditionalScore = 1.0f - ((float)i / traditionalResults.Count); // Higher score for earlier results
            var weightedScore = traditionalScore * traditionalWeight;

            combinedResults[memory.Id] = (memory, weightedScore);
        }

        // Add vector search results with their weights
        foreach (var vectorResult in vectorResults)
        {
            var memory = vectorResult.Memory;
            var vectorScore = vectorResult.Score;
            var weightedScore = vectorScore * vectorWeight;

            if (combinedResults.TryGetValue(memory.Id, out var value))
            {
                // Combine scores for memories found in both searches
                var (existingMemory, existingScore) = value;
                combinedResults[memory.Id] = (existingMemory, existingScore + weightedScore);
            }
            else
            {
                // Add new memory from vector search only
                combinedResults[memory.Id] = (memory, weightedScore);
            }
        }

        // Sort by combined score and return top results
        var finalResults = combinedResults
            .Values.OrderByDescending(x => x.combinedScore)
            .Take(limit)
            .Select(x => x.memory.WithScore(x.combinedScore))
            .ToList();

        _logger.LogInformation(
            "Hybrid search returned {Count} results for session {SessionContext} (traditional: {TraditionalCount}, vector: {VectorCount})",
            finalResults.Count,
            sessionContext,
            traditionalResults.Count,
            vectorResults.Count
        );

        return finalResults;
    }

    /// <summary>
    /// Gets all agents for a specific user.
    /// </summary>
    public async Task<List<string>> GetAgentsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("UserId cannot be empty", nameof(userId));
        }

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT DISTINCT agent_id
                FROM memories 
                WHERE user_id = @userId 
                  AND agent_id IS NOT NULL 
                  AND agent_id != ''
                ORDER BY agent_id";

                _ = command.Parameters.AddWithValue("@userId", userId);

                var agents = new List<string>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var agentIdOrdinal = reader.GetOrdinal("agent_id");
                    if (!reader.IsDBNull(agentIdOrdinal))
                    {
                        var agentId = reader.GetString(agentIdOrdinal);
                        if (!string.IsNullOrWhiteSpace(agentId))
                        {
                            agents.Add(agentId);
                        }
                    }
                }

                _logger.LogDebug("Found {Count} agents for user {UserId}", agents.Count, userId);
                return agents;
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Gets all run IDs for a specific user and agent.
    /// </summary>
    public async Task<List<string>> GetRunsAsync(
        string userId,
        string agentId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("UserId cannot be empty", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("AgentId cannot be empty", nameof(agentId));
        }

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

        return await session.ExecuteAsync(
            async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    @"
                SELECT DISTINCT run_id
                FROM memories 
                WHERE user_id = @userId 
                  AND agent_id = @agentId
                  AND run_id IS NOT NULL 
                  AND run_id != ''
                ORDER BY run_id";

                _ = command.Parameters.AddWithValue("@userId", userId);
                _ = command.Parameters.AddWithValue("@agentId", agentId);

                var runs = new List<string>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var runIdOrdinal = reader.GetOrdinal("run_id");
                    if (!reader.IsDBNull(runIdOrdinal))
                    {
                        var runId = reader.GetString(runIdOrdinal);
                        if (!string.IsNullOrWhiteSpace(runId))
                        {
                            runs.Add(runId);
                        }
                    }
                }

                _logger.LogDebug(
                    "Found {Count} runs for user {UserId} and agent {AgentId}",
                    runs.Count,
                    userId,
                    agentId
                );
                return runs;
            },
            cancellationToken
        );
    }
}
