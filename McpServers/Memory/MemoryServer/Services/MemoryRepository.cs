using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
        ILogger<MemoryRepository> logger)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Adds a new memory to the repository.
    /// </summary>
    public async Task<Memory> AddAsync(string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Memory content cannot be empty", nameof(content));

        if (content.Length > 10000)
            throw new ArgumentException("Memory content cannot exceed 10,000 characters", nameof(content));

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
            Version = 1
        };

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO memories (id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version)
                VALUES (@id, @content, @userId, @agentId, @runId, @metadata, @createdAt, @updatedAt, @version)";

            command.Parameters.AddWithValue("@id", memory.Id);
            command.Parameters.AddWithValue("@content", memory.Content);
            command.Parameters.AddWithValue("@userId", memory.UserId);
            command.Parameters.AddWithValue("@agentId", memory.AgentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@runId", memory.RunId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@metadata", metadata != null ? JsonSerializer.Serialize(metadata) : (object)DBNull.Value);
            command.Parameters.AddWithValue("@createdAt", memory.CreatedAt);
            command.Parameters.AddWithValue("@updatedAt", memory.UpdatedAt);
            command.Parameters.AddWithValue("@version", memory.Version);

            await command.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Added memory {Id} for session {SessionContext}", memory.Id, sessionContext);
            return memory;
        });
    }

    /// <summary>
    /// Gets a memory by its integer ID within the session context.
    /// </summary>
    public async Task<Memory?> GetByIdAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
                FROM memories 
                WHERE id = @id AND user_id = @userId";

            // Add session context filters
            if (!string.IsNullOrEmpty(sessionContext.AgentId))
            {
                command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
            }

            if (!string.IsNullOrEmpty(sessionContext.RunId))
            {
                command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                command.Parameters.AddWithValue("@runId", sessionContext.RunId);
            }

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@userId", sessionContext.UserId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadMemoryFromReader(reader);
            }

            return null;
        });
    }

    /// <summary>
    /// Updates an existing memory.
    /// </summary>
    public async Task<Memory?> UpdateAsync(int id, string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Memory content cannot be empty", nameof(content));

        if (content.Length > 10000)
            throw new ArgumentException("Memory content cannot exceed 10,000 characters", nameof(content));

        // First get the existing memory to check permissions and get current version
        var existingMemory = await GetByIdAsync(id, sessionContext, cancellationToken);
        if (existingMemory == null)
        {
            return null;
        }

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                UPDATE memories 
                SET content = @content, metadata = @metadata, updated_at = @updatedAt, version = version + 1
                WHERE id = @id AND user_id = @userId AND version = @currentVersion";

            // Add session context filters for security
            if (!string.IsNullOrEmpty(sessionContext.AgentId))
            {
                command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
            }

            if (!string.IsNullOrEmpty(sessionContext.RunId))
            {
                command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                command.Parameters.AddWithValue("@runId", sessionContext.RunId);
            }

            var updatedAt = DateTime.UtcNow;
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@content", content.Trim());
            command.Parameters.AddWithValue("@metadata", metadata != null ? JsonSerializer.Serialize(metadata) : (object)DBNull.Value);
            command.Parameters.AddWithValue("@updatedAt", updatedAt);
            command.Parameters.AddWithValue("@userId", sessionContext.UserId);
            command.Parameters.AddWithValue("@currentVersion", existingMemory.Version);

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
        });
    }

    /// <summary>
    /// Deletes a memory by its integer ID within the session context.
    /// </summary>
    public async Task<bool> DeleteAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                DELETE FROM memories 
                WHERE id = @id AND user_id = @userId";

            // Add session context filters for security
            if (!string.IsNullOrEmpty(sessionContext.AgentId))
            {
                command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
            }

            if (!string.IsNullOrEmpty(sessionContext.RunId))
            {
                command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                command.Parameters.AddWithValue("@runId", sessionContext.RunId);
            }

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@userId", sessionContext.UserId);

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
            var deleted = rowsAffected > 0;

            if (deleted)
            {
                _logger.LogInformation("Deleted memory {Id} for session {SessionContext}", id, sessionContext);
            }

            return deleted;
        });
    }

    /// <summary>
    /// Gets all memories for a session context with pagination.
    /// </summary>
    public async Task<List<Memory>> GetAllAsync(SessionContext sessionContext, int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
                FROM memories 
                WHERE user_id = @userId";

            // Add session context filters
            if (!string.IsNullOrEmpty(sessionContext.AgentId))
            {
                command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
            }

            if (!string.IsNullOrEmpty(sessionContext.RunId))
            {
                command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                command.Parameters.AddWithValue("@runId", sessionContext.RunId);
            }

            command.CommandText += " ORDER BY created_at DESC LIMIT @limit OFFSET @offset";

            command.Parameters.AddWithValue("@userId", sessionContext.UserId);
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);

            var memories = new List<Memory>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                memories.Add(ReadMemoryFromReader(reader));
            }

            return memories;
        });
    }

    /// <summary>
    /// Searches memories using FTS5 or LIKE fallback.
    /// </summary>
    public async Task<List<Memory>> SearchAsync(string query, SessionContext sessionContext, int limit = 10, float scoreThreshold = 0.0f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<Memory>();

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteAsync(async connection =>
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
        });
    }

    private async Task<List<Memory>> SearchWithFts5Async(SqliteConnection connection, string query, SessionContext sessionContext, int limit, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        
        // Use FTS5 MATCH for full-text search
        command.CommandText = @"
            SELECT m.id, m.content, m.user_id, m.agent_id, m.run_id, m.metadata, m.created_at, m.updated_at, m.version,
                   fts.rank
            FROM memory_fts fts
            JOIN memories m ON m.id = fts.memory_id
            WHERE fts MATCH @query 
              AND m.user_id = @userId";

        // Add session context filters
        if (!string.IsNullOrEmpty(sessionContext.AgentId))
        {
            command.CommandText += " AND (m.agent_id = @agentId OR m.agent_id IS NULL)";
            command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
        }

        if (!string.IsNullOrEmpty(sessionContext.RunId))
        {
            command.CommandText += " AND (m.run_id = @runId OR m.run_id IS NULL)";
            command.Parameters.AddWithValue("@runId", sessionContext.RunId);
        }

        command.CommandText += " ORDER BY fts.rank LIMIT @limit";

        // Escape FTS5 special characters and prepare query
        var escapedQuery = EscapeFts5Query(query);
        command.Parameters.AddWithValue("@query", escapedQuery);
        command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        command.Parameters.AddWithValue("@limit", limit);

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

    private async Task<List<Memory>> SearchWithLikeAsync(SqliteConnection connection, string query, SessionContext sessionContext, int limit, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        
        command.CommandText = @"
            SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
            FROM memories 
            WHERE (content LIKE @query OR metadata LIKE @query)
              AND user_id = @userId";

        // Add session context filters
        if (!string.IsNullOrEmpty(sessionContext.AgentId))
        {
            command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
            command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
        }

        if (!string.IsNullOrEmpty(sessionContext.RunId))
        {
            command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
            command.Parameters.AddWithValue("@runId", sessionContext.RunId);
        }

        command.CommandText += " ORDER BY created_at DESC LIMIT @limit";

        var likeQuery = $"%{query}%";
        command.Parameters.AddWithValue("@query", likeQuery);
        command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        command.Parameters.AddWithValue("@limit", limit);

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
    public async Task<MemoryStats> GetStatsAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
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
                command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
            }

            if (!string.IsNullOrEmpty(sessionContext.RunId))
            {
                command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                command.Parameters.AddWithValue("@runId", sessionContext.RunId);
            }

            command.Parameters.AddWithValue("@userId", sessionContext.UserId);

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
                    TotalContentSize = reader.IsDBNull(totalContentSizeOrdinal) ? 0 : reader.GetInt64(totalContentSizeOrdinal),
                    AverageContentLength = reader.IsDBNull(avgContentLengthOrdinal) ? 0 : reader.GetDouble(avgContentLengthOrdinal),
                    OldestMemory = reader.IsDBNull(oldestMemoryOrdinal) ? (DateTime?)null : reader.GetDateTime(oldestMemoryOrdinal),
                    NewestMemory = reader.IsDBNull(newestMemoryOrdinal) ? (DateTime?)null : reader.GetDateTime(newestMemoryOrdinal)
                };
            }

            return new MemoryStats();
        });
    }

    /// <summary>
    /// Deletes all memories for a session context.
    /// </summary>
    public async Task<int> DeleteAllAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                DELETE FROM memories 
                WHERE user_id = @userId";

            // Add session context filters for security
            if (!string.IsNullOrEmpty(sessionContext.AgentId))
            {
                command.CommandText += " AND (agent_id = @agentId OR agent_id IS NULL)";
                command.Parameters.AddWithValue("@agentId", sessionContext.AgentId);
            }

            if (!string.IsNullOrEmpty(sessionContext.RunId))
            {
                command.CommandText += " AND (run_id = @runId OR run_id IS NULL)";
                command.Parameters.AddWithValue("@runId", sessionContext.RunId);
            }

            command.Parameters.AddWithValue("@userId", sessionContext.UserId);

            var deletedCount = await command.ExecuteNonQueryAsync(cancellationToken);

            if (deletedCount > 0)
            {
                _logger.LogInformation("Deleted {Count} memories for session {SessionContext}", deletedCount, sessionContext);
            }

            return deletedCount;
        });
    }

    /// <summary>
    /// Gets memory history (placeholder for future versioning support).
    /// </summary>
    public async Task<List<MemoryHistoryEntry>> GetHistoryAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        // For now, just return the current version as a single history entry
        var memory = await GetByIdAsync(id, sessionContext, cancellationToken);
        if (memory == null)
        {
            return new List<MemoryHistoryEntry>();
        }

        return new List<MemoryHistoryEntry>
        {
            new MemoryHistoryEntry
            {
                MemoryId = memory.Id,
                Content = memory.Content,
                Version = memory.Version,
                CreatedAt = memory.UpdatedAt,
                ChangeType = "current"
            }
        };
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
            Version = reader.GetInt32(versionOrdinal)
        };
    }

    private static string EscapeFts5Query(string query)
    {
        // Escape FTS5 special characters
        return query.Replace("\"", "\"\"");
    }
} 