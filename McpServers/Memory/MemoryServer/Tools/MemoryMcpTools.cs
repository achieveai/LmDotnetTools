using System.ComponentModel;
using System.Text.Json;
using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MemoryServer.Tools;

/// <summary>
/// MCP tools for memory operations with session management and integer IDs.
/// </summary>
[McpServerToolType]
public class MemoryMcpTools
{
    private readonly IMemoryService _memoryService;
    private readonly ISessionContextResolver _sessionResolver;
    private readonly ILogger<MemoryMcpTools> _logger;

    public MemoryMcpTools(
        IMemoryService memoryService,
        ISessionContextResolver sessionResolver,
        ILogger<MemoryMcpTools> logger)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Adds new memories from conversation messages or direct content.
    /// </summary>
    /// <param name="content">The content to add as a memory</param>
    /// <param name="userId">Optional user identifier for session isolation</param>
    /// <param name="agentId">Optional agent identifier for session isolation</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="metadata">Optional additional metadata as JSON string</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Created memory with integer ID</returns>
    [McpServerTool(Name = "memory_add"), Description("Adds new memories from conversation messages or direct content")]
    public async Task<object> AddMemoryAsync(
        [Description("The content to add as a memory")] string? content,
        [Description("User identifier for session isolation")] string? userId = "",
        [Description("Agent identifier for session isolation")] string? agentId = "",
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Additional metadata as JSON string")] string? metadata = "",
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new { success = false, error = "Content is required" };
            }

            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Parse metadata if provided
            Dictionary<string, object>? metadataDict = null;
            if (!string.IsNullOrWhiteSpace(metadata))
            {
                try
                {
                    metadataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(metadata);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse metadata JSON: {Metadata}", metadata);
                    return new { success = false, error = "Invalid metadata JSON format" };
                }
            }

            // Add memory
            var memory = await _memoryService.AddMemoryAsync(content, sessionContext, metadataDict);

            _logger.LogInformation("Added memory {MemoryId} for session {SessionContext}", memory.Id, sessionContext);

            return new
            {
                success = true,
                memory = new
                {
                    id = memory.Id,
                    content = memory.Content,
                    user_id = memory.UserId,
                    agent_id = memory.AgentId,
                    run_id = memory.RunId,
                    created_at = memory.CreatedAt,
                    metadata = memory.Metadata
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding memory");
            return new { success = false, error = $"Error adding memory: {ex.Message}" };
        }
    }

    /// <summary>
    /// Searches for relevant memories using semantic similarity and full-text search.
    /// </summary>
    /// <param name="query">Search query text</param>
    /// <param name="userId">Optional user identifier for session filtering</param>
    /// <param name="agentId">Optional agent identifier for session filtering</param>
    /// <param name="runId">Optional run identifier for session filtering</param>
    /// <param name="limit">Maximum number of results (default: 10, max: 100)</param>
    /// <param name="scoreThreshold">Minimum similarity score threshold (default: 0.0)</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Array of relevant memory objects with relevance scores</returns>
    [McpServerTool(Name = "memory_search"), Description("Searches for relevant memories using semantic similarity and full-text search")]
    public async Task<object> SearchMemoriesAsync(
        [Description("Search query text")] string? query,
        [Description("User identifier for session filtering")] string? userId = "",
        [Description("Agent identifier for session filtering")] string? agentId = "",
        [Description("Run identifier for session filtering")] string? runId = "",
        [Description("Maximum number of results (default: 10, max: 100)")] int limit = 10,
        [Description("Minimum similarity score threshold (default: 0.0)")] float scoreThreshold = 0.0f,
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new { success = false, error = "Query is required" };
            }

            // Apply limits
            limit = Math.Min(Math.Max(limit, 1), 100);
            scoreThreshold = Math.Max(scoreThreshold, 0.0f);

            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Search memories
            var results = await _memoryService.SearchMemoriesAsync(query, sessionContext, limit, scoreThreshold);

            _logger.LogInformation("Found {Count} memories for query '{Query}' in session {SessionContext}", 
                results.Count, query, sessionContext);

            return new
            {
                success = true,
                query = query,
                results = results.Select(m => new
                {
                    id = m.Id,
                    content = m.Content,
                    score = m.Score ?? 0.0f,
                    user_id = m.UserId,
                    agent_id = m.AgentId,
                    run_id = m.RunId,
                    created_at = m.CreatedAt,
                    updated_at = m.UpdatedAt,
                    metadata = m.Metadata
                }),
                total_results = results.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching memories");
            return new { success = false, error = $"Error searching memories: {ex.Message}" };
        }
    }

    /// <summary>
    /// Retrieves all memories for a specific session.
    /// </summary>
    /// <param name="userId">Optional user identifier for session filtering</param>
    /// <param name="agentId">Optional agent identifier for session filtering</param>
    /// <param name="runId">Optional run identifier for session filtering</param>
    /// <param name="limit">Maximum number of results (default: 100, max: 1000)</param>
    /// <param name="offset">Offset for pagination (default: 0)</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Array of memory objects</returns>
    [McpServerTool(Name = "memory_get_all"), Description("Retrieves all memories for a specific session")]
    public async Task<object> GetAllMemoriesAsync(
        [Description("User identifier for session filtering")] string? userId = "",
        [Description("Agent identifier for session filtering")] string? agentId = "",
        [Description("Run identifier for session filtering")] string? runId = "",
        [Description("Maximum number of results (default: 100, max: 1000)")] int limit = 100,
        [Description("Offset for pagination (default: 0)")] int offset = 0,
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            // Apply limits
            limit = Math.Min(Math.Max(limit, 1), 1000);
            offset = Math.Max(offset, 0);

            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Get all memories
            var memories = await _memoryService.GetAllMemoriesAsync(sessionContext, limit, offset);

            _logger.LogInformation("Retrieved {Count} memories for session {SessionContext}", 
                memories.Count, sessionContext);

            return new
            {
                success = true,
                memories = memories.Select(m => new
                {
                    id = m.Id,
                    content = m.Content,
                    user_id = m.UserId,
                    agent_id = m.AgentId,
                    run_id = m.RunId,
                    created_at = m.CreatedAt,
                    updated_at = m.UpdatedAt,
                    version = m.Version,
                    metadata = m.Metadata
                }),
                total_count = memories.Count,
                limit = limit,
                offset = offset
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all memories");
            return new { success = false, error = $"Error getting all memories: {ex.Message}" };
        }
    }

    /// <summary>
    /// Updates an existing memory by ID.
    /// </summary>
    /// <param name="id">Memory ID to update</param>
    /// <param name="content">New content for the memory</param>
    /// <param name="userId">Optional user identifier for session isolation</param>
    /// <param name="agentId">Optional agent identifier for session isolation</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="metadata">Optional additional metadata as JSON string</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Updated memory object</returns>
    [McpServerTool(Name = "memory_update"), Description("Updates an existing memory by ID")]
    public async Task<object> UpdateMemoryAsync(
        [Description("Memory ID to update")] int id,
        [Description("New content for the memory")] string? content,
        [Description("User identifier for session isolation")] string? userId = "",
        [Description("Agent identifier for session isolation")] string? agentId = "",
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Additional metadata as JSON string")] string? metadata = "",
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new { success = false, error = "Content is required" };
            }

            if (id <= 0)
            {
                return new { success = false, error = "Valid memory ID is required" };
            }

            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Parse metadata if provided
            Dictionary<string, object>? metadataDict = null;
            if (!string.IsNullOrWhiteSpace(metadata))
            {
                try
                {
                    metadataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(metadata);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse metadata JSON: {Metadata}", metadata);
                    return new { success = false, error = "Invalid metadata JSON format" };
                }
            }

            // Update memory
            var updatedMemory = await _memoryService.UpdateMemoryAsync(id, content, sessionContext, metadataDict);

            if (updatedMemory == null)
            {
                return new { success = false, error = $"Memory with ID {id} not found or access denied" };
            }

            _logger.LogInformation("Updated memory {MemoryId} for session {SessionContext}", id, sessionContext);

            return new
            {
                success = true,
                memory = new
                {
                    id = updatedMemory.Id,
                    content = updatedMemory.Content,
                    user_id = updatedMemory.UserId,
                    agent_id = updatedMemory.AgentId,
                    run_id = updatedMemory.RunId,
                    created_at = updatedMemory.CreatedAt,
                    updated_at = updatedMemory.UpdatedAt,
                    version = updatedMemory.Version,
                    metadata = updatedMemory.Metadata
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating memory {MemoryId}", id);
            return new { success = false, error = $"Error updating memory: {ex.Message}" };
        }
    }

    /// <summary>
    /// Deletes a memory by ID.
    /// </summary>
    /// <param name="id">Memory ID to delete</param>
    /// <param name="userId">Optional user identifier for session isolation</param>
    /// <param name="agentId">Optional agent identifier for session isolation</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Deletion result</returns>
    [McpServerTool(Name = "memory_delete"), Description("Deletes a memory by ID")]
    public async Task<object> DeleteMemoryAsync(
        [Description("Memory ID to delete")] int id,
        [Description("User identifier for session isolation")] string? userId = "",
        [Description("Agent identifier for session isolation")] string? agentId = "",
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            if (id <= 0)
            {
                return new { success = false, error = "Valid memory ID is required" };
            }

            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Delete memory
            var deleted = await _memoryService.DeleteMemoryAsync(id, sessionContext);

            if (!deleted)
            {
                return new { success = false, error = $"Memory with ID {id} not found or access denied" };
            }

            _logger.LogInformation("Deleted memory {MemoryId} for session {SessionContext}", id, sessionContext);

            return new
            {
                success = true,
                message = $"Memory {id} deleted successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting memory {MemoryId}", id);
            return new { success = false, error = $"Error deleting memory: {ex.Message}" };
        }
    }

    /// <summary>
    /// Deletes all memories for a session.
    /// </summary>
    /// <param name="userId">Optional user identifier for session isolation</param>
    /// <param name="agentId">Optional agent identifier for session isolation</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Deletion result with count</returns>
    [McpServerTool(Name = "memory_delete_all"), Description("Deletes all memories for a session")]
    public async Task<object> DeleteAllMemoriesAsync(
        [Description("User identifier for session isolation")] string? userId = "",
        [Description("Agent identifier for session isolation")] string? agentId = "",
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Delete all memories
            var deletedCount = await _memoryService.DeleteAllMemoriesAsync(sessionContext);

            _logger.LogInformation("Deleted {Count} memories for session {SessionContext}", deletedCount, sessionContext);

            return new
            {
                success = true,
                deleted_count = deletedCount,
                message = $"Deleted {deletedCount} memories successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting all memories");
            return new { success = false, error = $"Error deleting all memories: {ex.Message}" };
        }
    }

    /// <summary>
    /// Gets memory history for a specific memory ID.
    /// </summary>
    /// <param name="id">Memory ID to get history for</param>
    /// <param name="userId">Optional user identifier for session isolation</param>
    /// <param name="agentId">Optional agent identifier for session isolation</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="limit">Maximum number of history entries (default: 50, max: 100)</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Array of memory history entries</returns>
    [McpServerTool(Name = "memory_get_history"), Description("Gets memory history for a specific memory ID")]
    public async Task<object> GetMemoryHistoryAsync(
        [Description("Memory ID to get history for")] int id,
        [Description("User identifier for session isolation")] string? userId = "",
        [Description("Agent identifier for session isolation")] string? agentId = "",
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Maximum number of history entries (default: 50, max: 100)")] int limit = 50,
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            if (id <= 0)
            {
                return new { success = false, error = "Valid memory ID is required" };
            }

            // Apply limits
            limit = Math.Min(Math.Max(limit, 1), 100);

            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Get memory history
            var history = await _memoryService.GetMemoryHistoryAsync(id, sessionContext);

            _logger.LogInformation("Retrieved {Count} history entries for memory {MemoryId}", history.Count, id);

            return new
            {
                success = true,
                memory_id = id,
                history = history.Take(limit).Select(h => new
                {
                    memory_id = h.MemoryId,
                    version = h.Version,
                    content = h.Content,
                    created_at = h.CreatedAt
                }),
                total_entries = history.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting memory history for {MemoryId}", id);
            return new { success = false, error = $"Error getting memory history: {ex.Message}" };
        }
    }

    /// <summary>
    /// Provides memory usage statistics and analytics.
    /// </summary>
    /// <param name="userId">Optional user identifier for session filtering</param>
    /// <param name="agentId">Optional agent identifier for session filtering</param>
    /// <param name="runId">Optional run identifier for session filtering</param>
    /// <param name="connectionId">Optional connection identifier for session defaults</param>
    /// <returns>Memory count statistics, storage usage, and performance metrics</returns>
    [McpServerTool(Name = "memory_get_stats"), Description("Provides memory usage statistics and analytics")]
    public async Task<object> GetMemoryStatsAsync(
        [Description("User identifier for session filtering")] string? userId = "",
        [Description("Agent identifier for session filtering")] string? agentId = "",
        [Description("Run identifier for session filtering")] string? runId = "",
        [Description("Connection identifier for session defaults")] string? connectionId = "")
    {
        try
        {
            // Generate connection ID if not provided
            connectionId = string.IsNullOrWhiteSpace(connectionId) ? Guid.NewGuid().ToString() : connectionId;

            // Convert empty strings to null for session resolution
            var userIdParam = string.IsNullOrWhiteSpace(userId) ? null : userId;
            var agentIdParam = string.IsNullOrWhiteSpace(agentId) ? null : agentId;
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userIdParam,
                agentIdParam,
                runIdParam);

            // Get memory statistics
            var stats = await _memoryService.GetMemoryStatsAsync(sessionContext);

            _logger.LogInformation("Retrieved memory statistics for session {SessionContext}: {TotalMemories} memories", 
                sessionContext, stats.TotalMemories);

            return new
            {
                success = true,
                session_context = new
                {
                    user_id = sessionContext.UserId,
                    agent_id = sessionContext.AgentId,
                    run_id = sessionContext.RunId
                },
                statistics = new
                {
                    total_memories = stats.TotalMemories,
                    total_content_size = stats.TotalContentSize,
                    average_content_length = stats.AverageContentLength,
                    oldest_memory = stats.OldestMemory,
                    newest_memory = stats.NewestMemory,
                    memory_count_by_scope = stats.MemoryCountByScope
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting memory statistics");
            return new { success = false, error = $"Error getting memory statistics: {ex.Message}" };
        }
    }
} 