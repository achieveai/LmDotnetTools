using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
///     Interface for intelligent reranking engine that applies semantic reranking to unified search results.
///     Provides multi-dimensional scoring combining semantic relevance, content quality, recency, and source weighting.
/// </summary>
public interface IRerankingEngine
{
    /// <summary>
    ///     Reranks unified search results using semantic reranking and multi-dimensional scoring.
    ///     This method should be called BEFORE result cutoffs to ensure the best results surface.
    /// </summary>
    /// <param name="query">The original search query.</param>
    /// <param name="results">The unified search results to rerank.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="options">Reranking configuration options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reranked results with updated scores and performance metrics.</returns>
    Task<RerankingResults> RerankResultsAsync(
        string query,
        List<UnifiedSearchResult> results,
        SessionContext sessionContext,
        RerankingOptions? options = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Checks if the reranking service is available and configured.
    /// </summary>
    /// <returns>True if reranking service is available, false otherwise.</returns>
    bool IsRerankingAvailable();
}
