using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Repository interface for memory operations with session isolation and integer IDs.
/// </summary>
public interface IMemoryRepository
{
    /// <summary>
    /// Adds a new memory to the repository.
    /// </summary>
    Task<Memory> AddAsync(string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a memory by its integer ID within the session context.
    /// </summary>
    Task<Memory?> GetByIdAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing memory.
    /// </summary>
    Task<Memory?> UpdateAsync(int id, string content, SessionContext sessionContext, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a memory by its integer ID within the session context.
    /// </summary>
    Task<bool> DeleteAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all memories for a session context with optional pagination.
    /// </summary>
    Task<List<Memory>> GetAllAsync(SessionContext sessionContext, int limit = 100, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches memories using full-text search within the session context.
    /// </summary>
    Task<List<Memory>> SearchAsync(string query, SessionContext sessionContext, int limit = 10, float scoreThreshold = 0.0f, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets memory statistics for a session context.
    /// </summary>
    Task<MemoryStats> GetStatsAsync(SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all memories for a session context.
    /// </summary>
    Task<int> DeleteAllAsync(SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets memory history/changes for a specific memory ID.
    /// </summary>
    Task<List<MemoryHistoryEntry>> GetHistoryAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about memories in a session context.
/// </summary>
public class MemoryStats
{
    /// <summary>
    /// Total number of memories.
    /// </summary>
    public int TotalMemories { get; set; }

    /// <summary>
    /// Total size of memory content in characters.
    /// </summary>
    public long TotalContentSize { get; set; }

    /// <summary>
    /// Average memory content length.
    /// </summary>
    public double AverageContentLength { get; set; }

    /// <summary>
    /// Oldest memory creation date.
    /// </summary>
    public DateTime? OldestMemory { get; set; }

    /// <summary>
    /// Newest memory creation date.
    /// </summary>
    public DateTime? NewestMemory { get; set; }

    /// <summary>
    /// Memory count by session scope.
    /// </summary>
    public Dictionary<string, int> MemoryCountByScope { get; set; } = new();
}

/// <summary>
/// Represents a memory history entry for tracking changes.
/// </summary>
public class MemoryHistoryEntry
{
    /// <summary>
    /// The memory ID.
    /// </summary>
    public int MemoryId { get; set; }

    /// <summary>
    /// The version number.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// The content at this version.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// When this version was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The type of change.
    /// </summary>
    public string ChangeType { get; set; } = "UPDATE";

    /// <summary>
    /// Additional metadata about the change.
    /// </summary>
    public Dictionary<string, object>? ChangeMetadata { get; set; }
} 