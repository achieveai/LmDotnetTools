using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Interface for unified multi-source search engine that searches across memories, entities, and relationships.
/// Provides parallel execution of all 6 search operations (Memory FTS5/Vector, Entity FTS5/Vector, Relationship FTS5/Vector).
/// </summary>
public interface IUnifiedSearchEngine
{
    /// <summary>
    /// Performs unified search across all sources (memories, entities, relationships) using both FTS5 and vector search.
    /// Executes all 6 search operations in parallel and returns normalized, aggregated results.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="options">Search configuration options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Unified search results with performance metrics.</returns>
    Task<UnifiedSearchResults> SearchAllSourcesAsync(
        string query,
        SessionContext sessionContext,
        UnifiedSearchOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs unified search with a pre-generated query embedding for vector searches.
    /// This overload is more efficient when the embedding is already available.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="queryEmbedding">Pre-generated embedding for the query.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="options">Search configuration options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Unified search results with performance metrics.</returns>
    Task<UnifiedSearchResults> SearchAllSourcesAsync(
        string query,
        float[] queryEmbedding,
        SessionContext sessionContext,
        UnifiedSearchOptions? options = null,
        CancellationToken cancellationToken = default);
} 