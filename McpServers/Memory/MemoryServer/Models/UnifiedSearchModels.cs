using MemoryServer.Services;

namespace MemoryServer.Models;

/// <summary>
/// Represents the type of unified search result.
/// </summary>
public enum UnifiedResultType
{
    Memory,
    Entity,
    Relationship
}

/// <summary>
/// Unified search result that provides a common interface for all search result types.
/// </summary>
public class UnifiedSearchResult
{
    /// <summary>
    /// The type of result (Memory, Entity, or Relationship).
    /// </summary>
    public UnifiedResultType Type { get; set; }

    /// <summary>
    /// Unique identifier for the result.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Primary content or name of the result.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Secondary content or description.
    /// </summary>
    public string? SecondaryContent { get; set; }

    /// <summary>
    /// Relevance score from the search operation.
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    /// Source of the search result (FTS5 or Vector).
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// When the result was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Confidence level for the result (for entities and relationships).
    /// </summary>
    public float? Confidence { get; set; }

    /// <summary>
    /// Additional metadata as JSON.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Reference to the original memory object (if Type is Memory).
    /// </summary>
    public Memory? OriginalMemory { get; set; }

    /// <summary>
    /// Reference to the original entity object (if Type is Entity).
    /// </summary>
    public Entity? OriginalEntity { get; set; }

    /// <summary>
    /// Reference to the original relationship object (if Type is Relationship).
    /// </summary>
    public Relationship? OriginalRelationship { get; set; }
}

/// <summary>
/// Collection of unified search results with performance metrics.
/// </summary>
public class UnifiedSearchResults
{
    /// <summary>
    /// The search results.
    /// </summary>
    public List<UnifiedSearchResult> Results { get; set; } = new();

    /// <summary>
    /// Total number of results found across all sources.
    /// </summary>
    public int TotalResults => Results.Count;

    /// <summary>
    /// Number of memory results.
    /// </summary>
    public int MemoryResults => Results.Count(r => r.Type == UnifiedResultType.Memory);

    /// <summary>
    /// Number of entity results.
    /// </summary>
    public int EntityResults => Results.Count(r => r.Type == UnifiedResultType.Entity);

    /// <summary>
    /// Number of relationship results.
    /// </summary>
    public int RelationshipResults => Results.Count(r => r.Type == UnifiedResultType.Relationship);

    /// <summary>
    /// Performance metrics for the search operation.
    /// </summary>
    public UnifiedSearchMetrics Metrics { get; set; } = new();
}

/// <summary>
/// Performance metrics for unified search operations.
/// </summary>
public class UnifiedSearchMetrics
{
    /// <summary>
    /// Total time taken for the search operation.
    /// </summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Time taken for memory FTS5 search.
    /// </summary>
    public TimeSpan MemoryFtsSearchDuration { get; set; }

    /// <summary>
    /// Time taken for memory vector search.
    /// </summary>
    public TimeSpan MemoryVectorSearchDuration { get; set; }

    /// <summary>
    /// Time taken for entity FTS5 search.
    /// </summary>
    public TimeSpan EntityFtsSearchDuration { get; set; }

    /// <summary>
    /// Time taken for entity vector search.
    /// </summary>
    public TimeSpan EntityVectorSearchDuration { get; set; }

    /// <summary>
    /// Time taken for relationship FTS5 search.
    /// </summary>
    public TimeSpan RelationshipFtsSearchDuration { get; set; }

    /// <summary>
    /// Time taken for relationship vector search.
    /// </summary>
    public TimeSpan RelationshipVectorSearchDuration { get; set; }

    /// <summary>
    /// Number of results from memory FTS5 search.
    /// </summary>
    public int MemoryFtsResultCount { get; set; }

    /// <summary>
    /// Number of results from memory vector search.
    /// </summary>
    public int MemoryVectorResultCount { get; set; }

    /// <summary>
    /// Number of results from entity FTS5 search.
    /// </summary>
    public int EntityFtsResultCount { get; set; }

    /// <summary>
    /// Number of results from entity vector search.
    /// </summary>
    public int EntityVectorResultCount { get; set; }

    /// <summary>
    /// Number of results from relationship FTS5 search.
    /// </summary>
    public int RelationshipFtsResultCount { get; set; }

    /// <summary>
    /// Number of results from relationship vector search.
    /// </summary>
    public int RelationshipVectorResultCount { get; set; }

    /// <summary>
    /// Whether any search operations failed.
    /// </summary>
    public bool HasFailures { get; set; }

    /// <summary>
    /// List of any errors that occurred during search.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Time taken for reranking operation.
    /// </summary>
    public TimeSpan RerankingDuration { get; set; }

    /// <summary>
    /// Whether reranking was performed.
    /// </summary>
    public bool WasReranked { get; set; }

    /// <summary>
    /// Number of results that changed position after reranking.
    /// </summary>
    public int RerankingPositionChanges { get; set; }

    /// <summary>
    /// Time taken for deduplication operation.
    /// </summary>
    public TimeSpan DeduplicationDuration { get; set; }

    /// <summary>
    /// Number of duplicates removed during deduplication.
    /// </summary>
    public int DuplicatesRemoved { get; set; }

    /// <summary>
    /// Time taken for result enrichment operation.
    /// </summary>
    public TimeSpan EnrichmentDuration { get; set; }

    /// <summary>
    /// Number of results that were enriched.
    /// </summary>
    public int ItemsEnriched { get; set; }
}

/// <summary>
/// Configuration options for unified search operations.
/// </summary>
public class UnifiedSearchOptions
{
    /// <summary>
    /// Maximum number of results to return per source type.
    /// </summary>
    public int MaxResultsPerSource { get; set; } = 20;

    /// <summary>
    /// Timeout for search operations.
    /// </summary>
    public TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether to enable vector search.
    /// </summary>
    public bool EnableVectorSearch { get; set; } = true;

    /// <summary>
    /// Whether to enable FTS5 search.
    /// </summary>
    public bool EnableFtsSearch { get; set; } = true;

    /// <summary>
    /// Similarity threshold for vector search.
    /// </summary>
    public float VectorSimilarityThreshold { get; set; } = 0.7f;

    /// <summary>
    /// Type-based weights for different result types.
    /// </summary>
    public Dictionary<UnifiedResultType, float> TypeWeights { get; set; } = new()
    {
        { UnifiedResultType.Memory, 1.0f },
        { UnifiedResultType.Entity, 0.8f },
        { UnifiedResultType.Relationship, 0.7f }
    };

    /// <summary>
    /// Whether to enable graceful fallback on errors.
    /// </summary>
    public bool EnableGracefulFallback { get; set; } = true;
}

/// <summary>
/// Configuration options for intelligent reranking operations.
/// </summary>
public class RerankingOptions
{
    /// <summary>
    /// Whether to enable semantic reranking.
    /// </summary>
    public bool EnableReranking { get; set; } = true;

    /// <summary>
    /// Maximum number of candidates to send for reranking (to manage API costs).
    /// </summary>
    public int MaxCandidates { get; set; } = 100;

    /// <summary>
    /// Whether to fall back to local scoring when external reranking fails.
    /// </summary>
    public bool EnableGracefulFallback { get; set; } = true;

    /// <summary>
    /// Timeout for reranking operations.
    /// </summary>
    public TimeSpan RerankingTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Reranking service endpoint URL.
    /// </summary>
    public string RerankingEndpoint { get; set; } = "https://api.cohere.ai";

    /// <summary>
    /// Reranking model to use.
    /// </summary>
    public string RerankingModel { get; set; } = "rerank-v3.5";

    /// <summary>
    /// API key for reranking service.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Weight for semantic relevance score (0.0 to 1.0).
    /// </summary>
    public float SemanticRelevanceWeight { get; set; } = 0.7f;

    /// <summary>
    /// Weight for content quality score (0.0 to 1.0).
    /// </summary>
    public float ContentQualityWeight { get; set; } = 0.1f;

    /// <summary>
    /// Weight for recency score (0.0 to 1.0).
    /// </summary>
    public float RecencyWeight { get; set; } = 0.1f;

    /// <summary>
    /// Weight for confidence score (0.0 to 1.0).
    /// </summary>
    public float ConfidenceWeight { get; set; } = 0.1f;

    /// <summary>
    /// Source-specific weights for hierarchical scoring.
    /// </summary>
    public Dictionary<UnifiedResultType, float> SourceWeights { get; set; } = new()
    {
        { UnifiedResultType.Memory, 1.0f },
        { UnifiedResultType.Entity, 0.8f },
        { UnifiedResultType.Relationship, 0.7f }
    };

    /// <summary>
    /// Whether to boost scores for recent content.
    /// </summary>
    public bool EnableRecencyBoost { get; set; } = true;

    /// <summary>
    /// Number of days for recency boost calculation.
    /// </summary>
    public int RecencyBoostDays { get; set; } = 30;
}

/// <summary>
/// Results from reranking operation with performance metrics.
/// </summary>
public class RerankingResults
{
    /// <summary>
    /// The reranked results with updated scores.
    /// </summary>
    public List<UnifiedSearchResult> Results { get; set; } = new();

    /// <summary>
    /// Performance metrics for the reranking operation.
    /// </summary>
    public RerankingMetrics Metrics { get; set; } = new();

    /// <summary>
    /// Whether reranking was actually performed or fell back to original scores.
    /// </summary>
    public bool WasReranked { get; set; }

    /// <summary>
    /// Reason for fallback if reranking was not performed.
    /// </summary>
    public string? FallbackReason { get; set; }
}

/// <summary>
/// Performance metrics for reranking operations.
/// </summary>
public class RerankingMetrics
{
    /// <summary>
    /// Total time taken for the reranking operation.
    /// </summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Time taken for semantic reranking API call.
    /// </summary>
    public TimeSpan SemanticRerankingDuration { get; set; }

    /// <summary>
    /// Time taken for local scoring calculations.
    /// </summary>
    public TimeSpan LocalScoringDuration { get; set; }

    /// <summary>
    /// Number of results sent for reranking.
    /// </summary>
    public int CandidateCount { get; set; }

    /// <summary>
    /// Number of results returned after reranking.
    /// </summary>
    public int RankedResultCount { get; set; }

    /// <summary>
    /// Whether any errors occurred during reranking.
    /// </summary>
    public bool HasFailures { get; set; }

    /// <summary>
    /// List of any errors that occurred during reranking.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Average score change after reranking.
    /// </summary>
    public float AverageScoreChange { get; set; }

    /// <summary>
    /// Number of results that changed position after reranking.
    /// </summary>
    public int PositionChanges { get; set; }
}

/// <summary>
/// Results from deduplication operation with performance metrics.
/// </summary>
public class DeduplicationResults
{
    /// <summary>
    /// The deduplicated results with duplicates removed.
    /// </summary>
    public List<UnifiedSearchResult> Results { get; set; } = new();

    /// <summary>
    /// Performance metrics for the deduplication operation.
    /// </summary>
    public DeduplicationMetrics Metrics { get; set; } = new();

    /// <summary>
    /// Whether deduplication was actually performed.
    /// </summary>
    public bool WasDeduplicationPerformed { get; set; }

    /// <summary>
    /// Reason for fallback if deduplication was not performed.
    /// </summary>
    public string? FallbackReason { get; set; }
}

/// <summary>
/// Performance metrics for deduplication operations.
/// </summary>
public class DeduplicationMetrics
{
    /// <summary>
    /// Total time taken for the deduplication operation.
    /// </summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Time taken for content similarity analysis.
    /// </summary>
    public TimeSpan SimilarityAnalysisDuration { get; set; }

    /// <summary>
    /// Time taken for source relationship analysis.
    /// </summary>
    public TimeSpan SourceAnalysisDuration { get; set; }

    /// <summary>
    /// Number of potential duplicates identified.
    /// </summary>
    public int PotentialDuplicatesFound { get; set; }

    /// <summary>
    /// Number of duplicates actually removed.
    /// </summary>
    public int DuplicatesRemoved { get; set; }

    /// <summary>
    /// Number of duplicates preserved due to complementary information.
    /// </summary>
    public int DuplicatesPreserved { get; set; }

    /// <summary>
    /// Whether any errors occurred during deduplication.
    /// </summary>
    public bool HasFailures { get; set; }

    /// <summary>
    /// List of any errors that occurred during deduplication.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Results from enrichment operation with performance metrics.
/// </summary>
public class EnrichmentResults
{
    /// <summary>
    /// The enriched results with additional context.
    /// </summary>
    public List<EnrichedSearchResult> Results { get; set; } = new();

    /// <summary>
    /// Performance metrics for the enrichment operation.
    /// </summary>
    public EnrichmentMetrics Metrics { get; set; } = new();

    /// <summary>
    /// Whether enrichment was actually performed.
    /// </summary>
    public bool WasEnrichmentPerformed { get; set; }

    /// <summary>
    /// Reason for fallback if enrichment was not performed.
    /// </summary>
    public string? FallbackReason { get; set; }
}

/// <summary>
/// Performance metrics for enrichment operations.
/// </summary>
public class EnrichmentMetrics
{
    /// <summary>
    /// Total time taken for the enrichment operation.
    /// </summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// Time taken for relationship discovery.
    /// </summary>
    public TimeSpan RelationshipDiscoveryDuration { get; set; }

    /// <summary>
    /// Time taken for context analysis.
    /// </summary>
    public TimeSpan ContextAnalysisDuration { get; set; }

    /// <summary>
    /// Number of results that were enriched.
    /// </summary>
    public int ResultsEnriched { get; set; }

    /// <summary>
    /// Total number of related items added across all results.
    /// </summary>
    public int RelatedItemsAdded { get; set; }

    /// <summary>
    /// Whether any errors occurred during enrichment.
    /// </summary>
    public bool HasFailures { get; set; }

    /// <summary>
    /// List of any errors that occurred during enrichment.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Enriched search result with additional context and related items.
/// </summary>
public class EnrichedSearchResult : UnifiedSearchResult
{
    /// <summary>
    /// Related entities for this result (max 2 per minimal enrichment principle).
    /// </summary>
    public List<RelatedItem> RelatedEntities { get; set; } = new();

    /// <summary>
    /// Related relationships for this result (max 2 per minimal enrichment principle).
    /// </summary>
    public List<RelatedItem> RelatedRelationships { get; set; } = new();

    /// <summary>
    /// Relevance explanation for why this result matches the query.
    /// </summary>
    public string? RelevanceExplanation { get; set; }

    /// <summary>
    /// Connection paths showing how this result relates to the query terms.
    /// </summary>
    public List<string> ConnectionPaths { get; set; } = new();
}

/// <summary>
/// Related item for enrichment with relevance scoring.
/// </summary>
public class RelatedItem
{
    /// <summary>
    /// Type of the related item.
    /// </summary>
    public UnifiedResultType Type { get; set; }

    /// <summary>
    /// Unique identifier for the related item.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Content or name of the related item.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Relevance score for this related item (0.0 to 1.0).
    /// </summary>
    public float RelevanceScore { get; set; }

    /// <summary>
    /// Confidence score for this related item (if applicable).
    /// </summary>
    public float? Confidence { get; set; }

    /// <summary>
    /// Explanation of how this item relates to the main result.
    /// </summary>
    public string? RelationshipExplanation { get; set; }
} 