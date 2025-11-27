using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
///     Repository interface for memory operations with session isolation and integer IDs.
/// </summary>
public interface IMemoryRepository
{
    /// <summary>
    ///     Adds a new memory to the repository.
    /// </summary>
    Task<Memory> AddAsync(
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets a memory by its integer ID within the session context.
    /// </summary>
    Task<Memory?> GetByIdAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Updates an existing memory.
    /// </summary>
    Task<Memory?> UpdateAsync(
        int id,
        string content,
        SessionContext sessionContext,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Deletes a memory by its integer ID within the session context.
    /// </summary>
    Task<bool> DeleteAsync(int id, SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all memories for a session context with optional pagination.
    /// </summary>
    Task<List<Memory>> GetAllAsync(
        SessionContext sessionContext,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Searches memories using full-text search within the session context.
    /// </summary>
    Task<List<Memory>> SearchAsync(
        string query,
        SessionContext sessionContext,
        int limit = 10,
        float scoreThreshold = 0.0f,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets memory statistics for a session context.
    /// </summary>
    Task<MemoryStats> GetStatsAsync(SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes all memories for a session context.
    /// </summary>
    Task<int> DeleteAllAsync(SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets memory history entries for a specific memory ID.
    /// </summary>
    Task<List<MemoryHistoryEntry>> GetHistoryAsync(
        int id,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets all agents for a specific user.
    /// </summary>
    Task<List<string>> GetAgentsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all run IDs for a specific user and agent.
    /// </summary>
    Task<List<string>> GetRunsAsync(string userId, string agentId, CancellationToken cancellationToken = default);

    // Vector storage and search methods

    /// <summary>
    ///     Stores an embedding for a memory.
    /// </summary>
    /// <param name="memoryId">The ID of the memory.</param>
    /// <param name="embedding">The embedding vector.</param>
    /// <param name="modelName">The name of the embedding model used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StoreEmbeddingAsync(
        int memoryId,
        float[] embedding,
        string modelName,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets the embedding for a specific memory.
    /// </summary>
    /// <param name="memoryId">The ID of the memory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector or null if not found.</returns>
    Task<float[]?> GetEmbeddingAsync(int memoryId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Performs vector similarity search to find memories similar to the query embedding.
    /// </summary>
    /// <param name="queryEmbedding">The query embedding vector.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="threshold">Minimum similarity threshold (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of memories with similarity scores.</returns>
    Task<List<VectorSearchResult>> SearchVectorAsync(
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float threshold = 0.7f,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Performs hybrid search combining FTS5 and vector similarity search.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="queryEmbedding">The query embedding vector.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="traditionalWeight">Weight for traditional search results (0.0 to 1.0).</param>
    /// <param name="vectorWeight">Weight for vector search results (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of memories with combined scores.</returns>
    Task<List<Memory>> SearchHybridAsync(
        string query,
        float[] queryEmbedding,
        SessionContext sessionContext,
        int limit = 10,
        float traditionalWeight = 0.3f,
        float vectorWeight = 0.7f,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Statistics about memories in a session context.
/// </summary>
public class MemoryStats
{
    /// <summary>
    ///     Total number of memories.
    /// </summary>
    public int TotalMemories { get; set; }

    /// <summary>
    ///     Total size of memory content in characters.
    /// </summary>
    public long TotalContentSize { get; set; }

    /// <summary>
    ///     Average memory content length.
    /// </summary>
    public double AverageContentLength { get; set; }

    /// <summary>
    ///     Oldest memory creation date.
    /// </summary>
    public DateTime? OldestMemory { get; set; }

    /// <summary>
    ///     Newest memory creation date.
    /// </summary>
    public DateTime? NewestMemory { get; set; }

    /// <summary>
    ///     Memory count by session scope.
    /// </summary>
    public Dictionary<string, int> MemoryCountByScope { get; set; } = [];
}

/// <summary>
///     Represents a memory history entry for tracking changes.
/// </summary>
public class MemoryHistoryEntry
{
    /// <summary>
    ///     The memory ID.
    /// </summary>
    public int MemoryId { get; set; }

    /// <summary>
    ///     The version number.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    ///     The content at this version.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    ///     User identifier for the memory.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    ///     Agent identifier for the memory.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    ///     Run identifier for the memory.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    ///     When this version was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    ///     The type of change.
    /// </summary>
    public string ChangeType { get; set; } = "UPDATE";

    /// <summary>
    ///     Memory metadata at this version.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    ///     Additional metadata about the change.
    /// </summary>
    public Dictionary<string, object>? ChangeMetadata { get; set; }
}

/// <summary>
///     Result of a vector similarity search.
/// </summary>
public class VectorSearchResult
{
    /// <summary>
    ///     The memory that matched the search.
    /// </summary>
    public Memory Memory { get; set; } = new();

    /// <summary>
    ///     Similarity score (0.0 to 1.0, higher is more similar).
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    ///     Distance value from the vector search.
    /// </summary>
    public float Distance { get; set; }
}
