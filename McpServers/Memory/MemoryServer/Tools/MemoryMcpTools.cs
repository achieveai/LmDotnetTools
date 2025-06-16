using System.ComponentModel;
using System.Text.Json;
using MemoryServer.Services;
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
    /// UserId and AgentId are automatically extracted from JWT token.
    /// </summary>
    /// <param name="content">The content to add as a memory</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="metadata">Optional additional metadata as JSON string</param>
    /// <returns>Created memory with integer ID</returns>
    [McpServerTool(Name = "memory_add"), Description("Adds new memories from conversation messages or direct content")]
    public async Task<object> AddMemoryAsync(
        [Description("The content to add as a memory")] string? content,
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Additional metadata as JSON string")] string? metadata = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new { success = false, error = "Content is required" };
            }

            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context - userId and agentId come from JWT token only
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,  // Get from JWT token
                explicitAgentId: null, // Get from JWT token
                explicitRunId: runIdParam);

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
                    updated_at = memory.UpdatedAt,
                    version = memory.Version,
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
    /// UserId is automatically extracted from JWT token. AgentId behavior: if not provided, uses JWT token agentId; if "all", searches all agents.
    /// </summary>
    /// <param name="query">Search query text</param>
    /// <param name="agentId">Agent identifier for session filtering - if not provided, uses JWT token agentId; if "all", searches all agents</param>
    /// <param name="runId">Optional run identifier for session filtering</param>
    /// <param name="limit">Maximum number of results (default: 10, max: 100)</param>
    /// <param name="scoreThreshold">Minimum similarity score threshold (default: 0.0)</param>
    /// <returns>Array of relevant memory objects with relevance scores</returns>
    [McpServerTool(Name = "memory_search"), Description("Searches for relevant memories using semantic similarity and full-text search")]
    public async Task<object> SearchMemoriesAsync(
        [Description("Search query text")] string? query,
        [Description("Agent identifier for session filtering - if not provided, uses JWT token agentId; if \"all\", searches all agents")] string? agentId = "",
        [Description("Run identifier for session filtering")] string? runId = "",
        [Description("Maximum number of results (default: 10, max: 100)")] int limit = 10,
        [Description("Minimum similarity score threshold (default: 0.0)")] float scoreThreshold = 0.0f)
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

            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Handle agentId logic:
            // - If empty/null: use JWT token agentId (pass null to get from JWT)
            // - If "all": search all agents (pass null to session resolver)
            string? agentIdParam = null;
            if (!string.IsNullOrWhiteSpace(agentId))
            {
                if (agentId.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    agentIdParam = null; // Search all agents
                }
                else
                {
                    agentIdParam = agentId; // Use specific agent
                }
            }
            // If agentId is null/empty, agentIdParam stays null and JWT token agentId will be used

            // Resolve session context - userId comes from JWT token, agentId logic as above
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,     // Get from JWT token
                explicitAgentId: agentIdParam, // null = use JWT token agentId, or search all if "all" was specified
                explicitRunId: runIdParam);

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
    /// UserId is automatically extracted from JWT token. AgentId behavior: if not provided, uses JWT token agentId; if "all", gets all agents.
    /// </summary>
    /// <param name="agentId">Agent identifier for session filtering - if not provided, uses JWT token agentId; if "all", gets all agents</param>
    /// <param name="runId">Optional run identifier for session filtering</param>
    /// <param name="limit">Maximum number of results (default: 100, max: 1000)</param>
    /// <param name="offset">Offset for pagination (default: 0)</param>
    /// <returns>Array of memory objects</returns>
    [McpServerTool(Name = "memory_get_all"), Description("Retrieves all memories for a specific session")]
    public async Task<object> GetAllMemoriesAsync(
        [Description("Agent identifier for session filtering - if not provided, uses JWT token agentId; if \"all\", gets all agents")] string? agentId = "",
        [Description("Run identifier for session filtering")] string? runId = "",
        [Description("Maximum number of results (default: 100, max: 1000)")] int limit = 100,
        [Description("Offset for pagination (default: 0)")] int offset = 0)
    {
        try
        {
            // Apply limits
            limit = Math.Min(Math.Max(limit, 1), 1000);
            offset = Math.Max(offset, 0);

            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Handle agentId logic:
            // - If empty/null: use JWT token agentId (pass null to get from JWT)
            // - If "all": get all agents (pass null to session resolver)
            string? agentIdParam = null;
            if (!string.IsNullOrWhiteSpace(agentId))
            {
                if (agentId.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    agentIdParam = null; // Get all agents
                }
                else
                {
                    agentIdParam = agentId; // Use specific agent
                }
            }
            // If agentId is null/empty, agentIdParam stays null and JWT token agentId will be used

            // Resolve session context - userId comes from JWT token, agentId logic as above
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,     // Get from JWT token
                explicitAgentId: agentIdParam, // null = use JWT token agentId, or get all if "all" was specified
                explicitRunId: runIdParam);

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
    /// UserId and AgentId are automatically extracted from JWT token for session validation.
    /// </summary>
    /// <param name="id">Memory ID to update</param>
    /// <param name="content">New content for the memory</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="metadata">Optional additional metadata as JSON string</param>
    /// <returns>Updated memory object</returns>
    [McpServerTool(Name = "memory_update"), Description("Updates an existing memory by ID")]
    public async Task<object> UpdateMemoryAsync(
        [Description("Memory ID to update")] int id,
        [Description("New content for the memory")] string? content,
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Additional metadata as JSON string")] string? metadata = "")
    {
        try
        {
            if (id <= 0)
            {
                return new { success = false, error = "Valid memory ID is required" };
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return new { success = false, error = "Content is required" };
            }

            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context - userId and agentId come from JWT token only
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,  // Get from JWT token
                explicitAgentId: null, // Get from JWT token
                explicitRunId: runIdParam);

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
            var memory = await _memoryService.UpdateMemoryAsync(id, content, sessionContext, metadataDict);

            if (memory == null)
            {
                return new { success = false, error = "Memory not found or access denied" };
            }

            _logger.LogInformation("Updated memory {MemoryId} for session {SessionContext}", memory.Id, sessionContext);

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
                    updated_at = memory.UpdatedAt,
                    version = memory.Version,
                    metadata = memory.Metadata
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
    /// UserId and AgentId are automatically extracted from JWT token for session validation.
    /// </summary>
    /// <param name="id">Memory ID to delete</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <returns>Deletion confirmation</returns>
    [McpServerTool(Name = "memory_delete"), Description("Deletes a memory by ID")]
    public async Task<object> DeleteMemoryAsync(
        [Description("Memory ID to delete")] int id,
        [Description("Run identifier for session isolation")] string? runId = "")
    {
        try
        {
            if (id <= 0)
            {
                return new { success = false, error = "Valid memory ID is required" };
            }

            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context - userId and agentId come from JWT token only
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,  // Get from JWT token
                explicitAgentId: null, // Get from JWT token
                explicitRunId: runIdParam);

            // Delete memory
            var deleted = await _memoryService.DeleteMemoryAsync(id, sessionContext);

            if (!deleted)
            {
                return new { success = false, error = "Memory not found or access denied" };
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
    /// UserId and AgentId are automatically extracted from JWT token for session targeting.
    /// </summary>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <returns>Deletion summary</returns>
    [McpServerTool(Name = "memory_delete_all"), Description("Deletes all memories for a session")]
    public async Task<object> DeleteAllMemoriesAsync(
        [Description("Run identifier for session isolation")] string? runId = "")
    {
        try
        {
            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Resolve session context - userId and agentId come from JWT token only
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,  // Get from JWT token
                explicitAgentId: null, // Get from JWT token
                explicitRunId: runIdParam);

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
    /// UserId is automatically extracted from JWT token. AgentId behavior: if not provided, uses JWT token agentId; if "all", searches all agents.
    /// </summary>
    /// <param name="id">Memory ID to get history for</param>
    /// <param name="agentId">Agent identifier for session isolation - if not provided, uses JWT token agentId; if "all", searches all agents</param>
    /// <param name="runId">Optional run identifier for session isolation</param>
    /// <param name="limit">Maximum number of history entries (default: 50, max: 100)</param>
    /// <returns>Array of memory history entries</returns>
    [McpServerTool(Name = "memory_get_history"), Description("Gets memory history for a specific memory ID")]
    public async Task<object> GetMemoryHistoryAsync(
        [Description("Memory ID to get history for")] int id,
        [Description("Agent identifier for session isolation - if not provided, uses JWT token agentId; if \"all\", searches all agents")] string? agentId = "",
        [Description("Run identifier for session isolation")] string? runId = "",
        [Description("Maximum number of history entries (default: 50, max: 100)")] int limit = 50)
    {
        try
        {
            // Apply limits
            limit = Math.Min(Math.Max(limit, 1), 100);

            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Handle agentId logic:
            // - If empty/null: use JWT token agentId (pass null to get from JWT)
            // - If "all": search all agents (pass null to session resolver)
            string? agentIdParam = null;
            if (!string.IsNullOrWhiteSpace(agentId))
            {
                if (agentId.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    agentIdParam = null; // Search all agents
                }
                else
                {
                    agentIdParam = agentId; // Use specific agent
                }
            }
            // If agentId is null/empty, agentIdParam stays null and JWT token agentId will be used

            // Resolve session context - userId comes from JWT token, agentId logic as above
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,     // Get from JWT token
                explicitAgentId: agentIdParam, // null = use JWT token agentId, or search all if "all" was specified
                explicitRunId: runIdParam);

            // Get memory history
            var history = await _memoryService.GetMemoryHistoryAsync(id, sessionContext);

            _logger.LogInformation("Retrieved {Count} history entries for memory {Id} in session {SessionContext}", 
                history.Count, id, sessionContext);

            return new
            {
                success = true,
                memory_id = id,
                history = history.Take(limit).Select(h => new
                {
                    version = h.Version,
                    content = h.Content,
                    user_id = h.UserId,
                    agent_id = h.AgentId,
                    run_id = h.RunId,
                    created_at = h.CreatedAt,
                    metadata = h.Metadata
                }),
                total_entries = history.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting memory history");
            return new { success = false, error = $"Error getting memory history: {ex.Message}" };
        }
    }

    /// <summary>
    /// Provides memory usage statistics and analytics.
    /// UserId is automatically extracted from JWT token. AgentId behavior: if not provided, uses JWT token agentId; if "all", gets stats for all agents.
    /// </summary>
    /// <param name="agentId">Agent identifier for session filtering - if not provided, uses JWT token agentId; if "all", gets stats for all agents</param>
    /// <param name="runId">Optional run identifier for session filtering</param>
    /// <returns>Memory usage statistics</returns>
    [McpServerTool(Name = "memory_get_stats"), Description("Provides memory usage statistics and analytics")]
    public async Task<object> GetMemoryStatsAsync(
        [Description("Agent identifier for session filtering - if not provided, uses JWT token agentId; if \"all\", gets stats for all agents")] string? agentId = "",
        [Description("Run identifier for session filtering")] string? runId = "")
    {
        try
        {
            // Convert empty strings to null for session resolution
            var runIdParam = string.IsNullOrWhiteSpace(runId) ? null : runId;

            // Handle agentId logic:
            // - If empty/null: use JWT token agentId (pass null to get from JWT)
            // - If "all": get stats for all agents (pass null to session resolver)
            string? agentIdParam = null;
            if (!string.IsNullOrWhiteSpace(agentId))
            {
                if (agentId.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    agentIdParam = null; // Get stats for all agents
                }
                else
                {
                    agentIdParam = agentId; // Use specific agent
                }
            }
            // If agentId is null/empty, agentIdParam stays null and JWT token agentId will be used

            // Resolve session context - userId comes from JWT token, agentId logic as above
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,     // Get from JWT token
                explicitAgentId: agentIdParam, // null = use JWT token agentId, or get all if "all" was specified
                explicitRunId: runIdParam);

            // Get memory statistics
            var stats = await _memoryService.GetMemoryStatsAsync(sessionContext);

            _logger.LogInformation("Retrieved memory statistics for session {SessionContext}: {TotalMemories} memories", 
                sessionContext, stats.TotalMemories);

            return new
            {
                success = true,
                stats = new
                {
                    total_memories = stats.TotalMemories,
                    total_content_size = stats.TotalContentSize,
                    average_content_length = stats.AverageContentLength,
                    oldest_memory = stats.OldestMemory,
                    newest_memory = stats.NewestMemory
                },
                session_context = new
                {
                    user_id = sessionContext.UserId,
                    agent_id = sessionContext.AgentId,
                    run_id = sessionContext.RunId
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting memory statistics");
            return new { success = false, error = $"Error getting memory statistics: {ex.Message}" };
        }
    }

    /// <summary>
    /// Gets all agents for the current user.
    /// UserId is automatically extracted from JWT token.
    /// </summary>
    /// <returns>Array of agent identifiers</returns>
    [McpServerTool(Name = "memory_get_agents"), Description("Gets all agents for the current user")]
    public async Task<object> GetAgentsAsync()
    {
        try
        {
            // Resolve session context - userId comes from JWT token
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,     // Get from JWT token
                explicitAgentId: null,    // Not needed for this query
                explicitRunId: null);     // Not needed for this query

            // Get all agents for the user
            var agents = await _memoryService.GetAgentsAsync(sessionContext.UserId);

            _logger.LogInformation("Retrieved {Count} agents for user {UserId}", 
                agents.Count, sessionContext.UserId);

            return new
            {
                success = true,
                user_id = sessionContext.UserId,
                agents = agents,
                total_count = agents.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agents");
            return new { success = false, error = $"Error getting agents: {ex.Message}" };
        }
    }

    /// <summary>
    /// Gets all run IDs for a specific agent and user.
    /// UserId is automatically extracted from JWT token.
    /// </summary>
    /// <param name="agentId">Agent identifier to get runs for</param>
    /// <returns>Array of run identifiers</returns>
    [McpServerTool(Name = "memory_get_runs"), Description("Gets all run IDs for a specific agent and user")]
    public async Task<object> GetRunsAsync(
        [Description("Agent identifier to get runs for")] string? agentId = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(agentId))
            {
                return new { success = false, error = "AgentId is required" };
            }

            // Resolve session context - userId comes from JWT token
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                explicitUserId: null,     // Get from JWT token
                explicitAgentId: null,    // Not needed for this query
                explicitRunId: null);     // Not needed for this query

            // Get all runs for the user and agent
            var runs = await _memoryService.GetRunsAsync(sessionContext.UserId, agentId);

            _logger.LogInformation("Retrieved {Count} runs for user {UserId} and agent {AgentId}", 
                runs.Count, sessionContext.UserId, agentId);

            return new
            {
                success = true,
                user_id = sessionContext.UserId,
                agent_id = agentId,
                runs = runs,
                total_count = runs.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting runs");
            return new { success = false, error = $"Error getting runs: {ex.Message}" };
        }
    }
} 