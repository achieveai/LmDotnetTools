using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Service interface for memory operations with business logic and validation.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Adds a new memory from content.
    /// </summary>
    Task<Memory> AddMemoryAsync(
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Searches memories using text query.
    /// </summary>
    Task<List<Memory>> SearchMemoriesAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        float scoreThreshold = 0.7f,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets all memories for a session.
    /// </summary>
    Task<List<Memory>> GetAllMemoriesAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Updates an existing memory.
    /// </summary>
    Task<Memory?> UpdateMemoryAsync(
        int id,
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Deletes a memory by ID.
    /// </summary>
    Task<bool> DeleteMemoryAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all memories for a session.
    /// </summary>
    Task<int> DeleteAllMemoriesAsync(SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets memory statistics for a session.
    /// </summary>
    Task<MemoryStats> GetMemoryStatsAsync(SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets memory history for a specific memory ID.
    /// </summary>
    Task<List<MemoryHistoryEntry>> GetMemoryHistoryAsync(
        int id,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets all agents for a specific user.
    /// </summary>
    Task<List<string>> GetAgentsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all run IDs for a specific user and agent.
    /// </summary>
    Task<List<string>> GetRunsAsync(string userId, string agentId, CancellationToken cancellationToken = default);
}
