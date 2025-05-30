using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace MemoryServer.Services;

/// <summary>
/// SQLite-based repository for memory operations with session isolation and integer IDs.
/// </summary>
public class MemoryRepository : IMemoryRepository
{
    private readonly SqliteManager _sqliteManager;
    private readonly MemoryIdGenerator _idGenerator;
    private readonly ILogger<MemoryRepository> _logger;

    public MemoryRepository(
        SqliteManager sqliteManager,
        MemoryIdGenerator idGenerator,
        ILogger<MemoryRepository> logger)
    {
        _sqliteManager = sqliteManager;
        _idGenerator = idGenerator;
        _logger = logger;
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

        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        try
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
            transaction.Commit();

            _logger.LogInformation("Added memory {Id} for session {SessionContext}", memory.Id, sessionContext);
            return memory;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Gets a memory by its integer ID within the session context.
    /// </summary>
    public async Task<Memory?> GetByIdAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
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

        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        try
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
                transaction.Rollback();
                return null;
            }

            transaction.Commit();

            // Return updated memory
            var updatedMemory = existingMemory.WithUpdatedTimestamp();
            updatedMemory.Content = content.Trim();
            updatedMemory.Metadata = metadata;
            updatedMemory.UpdatedAt = updatedAt;

            _logger.LogInformation("Updated memory {Id} for session {SessionContext}", id, sessionContext);
            return updatedMemory;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Deletes a memory by its integer ID within the session context.
    /// </summary>
    public async Task<bool> DeleteAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();

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
    }

    /// <summary>
    /// Gets all memories for a session context with optional pagination.
    /// </summary>
    public async Task<List<Memory>> GetAllAsync(SessionContext sessionContext, int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
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

        _logger.LogDebug("Retrieved {Count} memories for session {SessionContext}", memories.Count, sessionContext);
        return memories;
    }

    /// <summary>
    /// Searches memories using full-text search within the session context.
    /// </summary>
    public async Task<List<Memory>> SearchAsync(string query, SessionContext sessionContext, int limit = 10, float scoreThreshold = 0.0f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<Memory>();

        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);

        // Try FTS5 search first
        try
        {
            return await SearchWithFts5Async(connection, query, sessionContext, limit, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS5 search failed for query '{Query}', falling back to LIKE search", query);
            return await SearchWithLikeAsync(connection, query, sessionContext, limit, cancellationToken);
        }
    }

    /// <summary>
    /// Searches using FTS5 with proper two-step approach.
    /// </summary>
    private async Task<List<Memory>> SearchWithFts5Async(SqliteConnection connection, string query, SessionContext sessionContext, int limit, CancellationToken cancellationToken)
    {
        // Step 1: Get memory IDs from FTS5 table
        var memoryIds = new List<int>();
        using (var command = connection.CreateCommand())
        {
            // Use simple FTS5 query without JOINs
            command.CommandText = "SELECT memory_id FROM memory_fts WHERE content MATCH @query LIMIT @limit";
            command.Parameters.AddWithValue("@query", query);
            command.Parameters.AddWithValue("@limit", limit * 2); // Get more IDs to account for session filtering

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                memoryIds.Add(reader.GetInt32(0));
            }
        }

        if (memoryIds.Count == 0)
        {
            return new List<Memory>();
        }

        // Step 2: Get full memory records with session filtering
        var memories = new List<Memory>();
        using (var command = connection.CreateCommand())
        {
            var idPlaceholders = string.Join(",", memoryIds.Select((_, i) => $"@id{i}"));
            command.CommandText = $@"
                SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
                FROM memories 
                WHERE id IN ({idPlaceholders}) AND user_id = @userId";

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

            // Add parameters for memory IDs
            for (int i = 0; i < memoryIds.Count; i++)
            {
                command.Parameters.AddWithValue($"@id{i}", memoryIds[i]);
            }
            command.Parameters.AddWithValue("@userId", sessionContext.UserId);
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var memory = ReadMemoryFromReader(reader);
                memory.Score = 1.0f; // FTS5 score (can be improved later)
                memories.Add(memory);
            }
        }

        _logger.LogDebug("Found {Count} memories using FTS5 for query '{Query}' in session {SessionContext}", memories.Count, query, sessionContext);
        return memories;
    }

    /// <summary>
    /// Fallback search using LIKE operator.
    /// </summary>
    private async Task<List<Memory>> SearchWithLikeAsync(SqliteConnection connection, string query, SessionContext sessionContext, int limit, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = @"
            SELECT id, content, user_id, agent_id, run_id, metadata, created_at, updated_at, version
            FROM memories 
            WHERE content LIKE @query AND user_id = @userId";

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

        command.Parameters.AddWithValue("@query", $"%{query}%");
        command.Parameters.AddWithValue("@userId", sessionContext.UserId);
        command.Parameters.AddWithValue("@limit", limit);

        var memories = new List<Memory>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var memory = ReadMemoryFromReader(reader);
            memory.Score = 0.8f; // Lower score for LIKE-based search
            memories.Add(memory);
        }

        _logger.LogDebug("Found {Count} memories using LIKE search for query '{Query}' in session {SessionContext}", memories.Count, query, sessionContext);
        return memories;
    }

    /// <summary>
    /// Gets memory statistics for a session context.
    /// </summary>
    public async Task<MemoryStats> GetStatsAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();

        command.CommandText = @"
            SELECT 
                COUNT(*) as total_memories,
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
            return new MemoryStats
            {
                TotalMemories = reader.GetInt32(0), // total_memories
                TotalContentSize = reader.IsDBNull(1) ? 0 : reader.GetInt64(1), // total_content_size
                AverageContentLength = reader.IsDBNull(2) ? 0 : reader.GetDouble(2), // avg_content_length
                OldestMemory = reader.IsDBNull(3) ? null : reader.GetDateTime(3), // oldest_memory
                NewestMemory = reader.IsDBNull(4) ? null : reader.GetDateTime(4) // newest_memory
            };
        }

        return new MemoryStats();
    }

    /// <summary>
    /// Deletes all memories for a session context.
    /// </summary>
    public async Task<int> DeleteAllAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        using var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();

        command.CommandText = @"
            DELETE FROM memories 
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

        var deletedCount = await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Deleted {Count} memories for session {SessionContext}", deletedCount, sessionContext);
        return deletedCount;
    }

    /// <summary>
    /// Gets memory history/changes for a specific memory ID.
    /// </summary>
    public async Task<List<MemoryHistoryEntry>> GetHistoryAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        // For now, return a simple history entry since we don't have a separate history table
        // In a full implementation, you would have a memory_history table
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
                Version = memory.Version,
                Content = memory.Content,
                CreatedAt = memory.UpdatedAt,
                ChangeType = memory.Version == 1 ? "CREATE" : "UPDATE"
            }
        };
    }

    /// <summary>
    /// Reads a Memory object from a SqliteDataReader.
    /// </summary>
    private Memory ReadMemoryFromReader(SqliteDataReader reader)
    {
        var metadataJson = reader.IsDBNull(5) ? null : reader.GetString(5); // metadata column
        Dictionary<string, object>? metadata = null;
        if (!string.IsNullOrEmpty(metadataJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize metadata for memory {Id}", reader.GetInt32(0));
            }
        }

        return new Memory
        {
            Id = reader.GetInt32(0), // id
            Content = reader.GetString(1), // content
            UserId = reader.GetString(2), // user_id
            AgentId = reader.IsDBNull(3) ? null : reader.GetString(3), // agent_id
            RunId = reader.IsDBNull(4) ? null : reader.GetString(4), // run_id
            Metadata = metadata, // metadata
            CreatedAt = reader.GetDateTime(6), // created_at
            UpdatedAt = reader.GetDateTime(7), // updated_at
            Version = reader.GetInt32(8) // version
        };
    }
} 