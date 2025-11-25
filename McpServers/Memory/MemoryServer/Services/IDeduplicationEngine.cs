using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
///     Interface for smart deduplication engine that intelligently removes overlapping results
///     while preserving valuable context and complementary information.
/// </summary>
public interface IDeduplicationEngine
{
    /// <summary>
    ///     Deduplicates unified search results using content similarity and source relationship analysis.
    ///     This method should be called AFTER reranking but BEFORE final result cutoffs.
    /// </summary>
    /// <param name="results">The unified search results to deduplicate.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="options">Deduplication configuration options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deduplicated results with performance metrics.</returns>
    Task<DeduplicationResults> DeduplicateResultsAsync(
        List<UnifiedSearchResult> results,
        SessionContext sessionContext,
        DeduplicationOptions? options = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Checks if the deduplication service is available and configured.
    /// </summary>
    /// <returns>True if deduplication service is available, false otherwise.</returns>
    bool IsDeduplicationAvailable();
}
