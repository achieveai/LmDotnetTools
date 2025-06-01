using MemoryServer.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
/// Service for memory operations with business logic and validation.
/// </summary>
public class MemoryService : IMemoryService
{
    private readonly IMemoryRepository _memoryRepository;
    private readonly ILogger<MemoryService> _logger;
    private readonly MemoryOptions _options;

    public MemoryService(
        IMemoryRepository memoryRepository,
        ILogger<MemoryService> logger,
        IOptions<MemoryServerOptions> options)
    {
        _memoryRepository = memoryRepository;
        _logger = logger;
        _options = options.Value.Memory;
    }

    /// <summary>
    /// Adds a new memory from content.
    /// </summary>
    public async Task<Memory> AddMemoryAsync(string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Memory content cannot be empty", nameof(content));

        if (content.Length > _options.MaxMemoryLength)
            throw new ArgumentException($"Memory content cannot exceed {_options.MaxMemoryLength} characters", nameof(content));

        _logger.LogDebug("Adding memory for session {SessionContext}, content length: {Length}", sessionContext, content.Length);

        var memory = await _memoryRepository.AddAsync(content, sessionContext, metadata, cancellationToken);
        
        _logger.LogInformation("Added memory {Id} for session {SessionContext}", memory.Id, sessionContext);
        return memory;
    }

    /// <summary>
    /// Searches memories using text query.
    /// </summary>
    public async Task<List<Memory>> SearchMemoriesAsync(string query, SessionContext sessionContext, int limit = 10, float scoreThreshold = 0.7f, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<Memory>();

        // Apply configured limits
        limit = Math.Min(limit, _options.DefaultSearchLimit * 2); // Allow up to 2x default limit
        scoreThreshold = Math.Max(scoreThreshold, 0.0f);

        _logger.LogDebug("Searching memories for session {SessionContext}, query: '{Query}', limit: {Limit}, threshold: {Threshold}", 
            sessionContext, query, limit, scoreThreshold);

        var memories = await _memoryRepository.SearchAsync(query, sessionContext, limit, scoreThreshold, cancellationToken);
        
        _logger.LogInformation("Found {Count} memories for query '{Query}' in session {SessionContext}", memories.Count, query, sessionContext);
        return memories;
    }

    /// <summary>
    /// Gets all memories for a session.
    /// </summary>
    public async Task<List<Memory>> GetAllMemoriesAsync(SessionContext sessionContext, int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        // Apply reasonable limits
        limit = Math.Min(limit, 1000); // Maximum 1000 memories at once
        offset = Math.Max(offset, 0);

        _logger.LogDebug("Getting all memories for session {SessionContext}, limit: {Limit}, offset: {Offset}", 
            sessionContext, limit, offset);

        var memories = await _memoryRepository.GetAllAsync(sessionContext, limit, offset, cancellationToken);
        
        _logger.LogInformation("Retrieved {Count} memories for session {SessionContext}", memories.Count, sessionContext);
        return memories;
    }

    /// <summary>
    /// Updates an existing memory.
    /// </summary>
    public async Task<Memory?> UpdateMemoryAsync(int id, string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Memory content cannot be empty", nameof(content));

        if (content.Length > _options.MaxMemoryLength)
            throw new ArgumentException($"Memory content cannot exceed {_options.MaxMemoryLength} characters", nameof(content));

        _logger.LogDebug("Updating memory {Id} for session {SessionContext}, content length: {Length}", 
            id, sessionContext, content.Length);

        var memory = await _memoryRepository.UpdateAsync(id, content, sessionContext, metadata, cancellationToken);
        
        if (memory != null)
        {
            _logger.LogInformation("Updated memory {Id} for session {SessionContext}", id, sessionContext);
        }
        else
        {
            _logger.LogWarning("Failed to update memory {Id} for session {SessionContext} - not found or access denied", id, sessionContext);
        }

        return memory;
    }

    /// <summary>
    /// Deletes a memory by ID.
    /// </summary>
    public async Task<bool> DeleteMemoryAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting memory {Id} for session {SessionContext}", id, sessionContext);

        var deleted = await _memoryRepository.DeleteAsync(id, sessionContext, cancellationToken);
        
        if (deleted)
        {
            _logger.LogInformation("Deleted memory {Id} for session {SessionContext}", id, sessionContext);
        }
        else
        {
            _logger.LogWarning("Failed to delete memory {Id} for session {SessionContext} - not found or access denied", id, sessionContext);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes all memories for a session.
    /// </summary>
    public async Task<int> DeleteAllMemoriesAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting all memories for session {SessionContext}", sessionContext);

        var deletedCount = await _memoryRepository.DeleteAllAsync(sessionContext, cancellationToken);
        
        _logger.LogInformation("Deleted {Count} memories for session {SessionContext}", deletedCount, sessionContext);
        return deletedCount;
    }

    /// <summary>
    /// Gets memory statistics for a session.
    /// </summary>
    public async Task<MemoryStats> GetMemoryStatsAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting memory statistics for session {SessionContext}", sessionContext);

        var stats = await _memoryRepository.GetStatsAsync(sessionContext, cancellationToken);
        
        _logger.LogDebug("Retrieved memory statistics for session {SessionContext}: {TotalMemories} memories", 
            sessionContext, stats.TotalMemories);
        
        return stats;
    }

    /// <summary>
    /// Gets memory history for a specific memory ID.
    /// </summary>
    public async Task<List<MemoryHistoryEntry>> GetMemoryHistoryAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting memory history for memory {Id} in session {SessionContext}", id, sessionContext);

        var history = await _memoryRepository.GetHistoryAsync(id, sessionContext, cancellationToken);
        
        _logger.LogDebug("Retrieved {Count} history entries for memory {Id} in session {SessionContext}", 
            history.Count, id, sessionContext);
        
        return history;
    }
} 