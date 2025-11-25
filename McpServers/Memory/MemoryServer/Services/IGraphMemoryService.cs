using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
///     Interface for the graph memory service that orchestrates graph processing and integrates with the memory system.
///     Implements the Facade pattern to provide a unified interface for graph operations.
/// </summary>
public interface IGraphMemoryService
{
    /// <summary>
    ///     Processes a memory and updates the knowledge graph with extracted entities and relationships.
    /// </summary>
    /// <param name="memory">The memory to process.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of graph updates performed.</returns>
    Task<GraphUpdateSummary> ProcessMemoryAsync(
        Memory memory,
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Searches for memories using both traditional search and graph traversal.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="useGraphTraversal">Whether to include graph traversal in search.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hybrid search results combining traditional and graph-based search.</returns>
    Task<HybridSearchResults> SearchMemoriesAsync(
        string query,
        SessionContext sessionContext,
        bool useGraphTraversal = true,
        int maxResults = 20,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets entities related to a specific entity through graph traversal.
    /// </summary>
    /// <param name="entityName">Name of the entity to start traversal from.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="maxDepth">Maximum depth for traversal.</param>
    /// <param name="relationshipTypes">Optional filter for relationship types.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Related entities and their relationships.</returns>
    Task<GraphTraversalResult> GetRelatedEntitiesAsync(
        string entityName,
        SessionContext sessionContext,
        int maxDepth = 3,
        IEnumerable<string>? relationshipTypes = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Gets comprehensive statistics about the knowledge graph.
    /// </summary>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph statistics and insights.</returns>
    Task<GraphStatistics> GetGraphStatisticsAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Rebuilds the knowledge graph from existing memories.
    /// </summary>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of the rebuild operation.</returns>
    Task<GraphRebuildSummary> RebuildGraphAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Validates the integrity of the knowledge graph.
    /// </summary>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Graph validation results.</returns>
    Task<GraphValidationResult> ValidateGraphIntegrityAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );
}
