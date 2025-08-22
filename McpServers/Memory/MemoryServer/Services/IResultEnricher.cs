using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Interface for result enrichment engine that provides minimal enrichment to enhance
/// user understanding without overwhelming the results (max 2 items per result).
/// </summary>
public interface IResultEnricher
{
    /// <summary>
    /// Enriches unified search results with minimal additional context and related items.
    /// This method should be called as the final step in the search pipeline.
    /// </summary>
    /// <param name="results">The unified search results to enrich.</param>
    /// <param name="sessionContext">Session context for isolation.</param>
    /// <param name="options">Enrichment configuration options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Enriched results with performance metrics.</returns>
    Task<EnrichmentResults> EnrichResultsAsync(
        List<UnifiedSearchResult> results,
        SessionContext sessionContext,
        EnrichmentOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the enrichment service is available and configured.
    /// </summary>
    /// <returns>True if enrichment service is available, false otherwise.</returns>
    bool IsEnrichmentAvailable();
}