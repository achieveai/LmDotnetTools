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
}

/// <summary>
/// Configuration options for unified search operations.
/// </summary>
public class UnifiedSearchOptions
{
    /// <summary>
    /// Maximum number of results to return per source (default: 20).
    /// </summary>
    public int MaxResultsPerSource { get; set; } = 20;

    /// <summary>
    /// Timeout for each individual search operation (default: 5 seconds).
    /// </summary>
    public TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether to enable vector search operations.
    /// </summary>
    public bool EnableVectorSearch { get; set; } = true;

    /// <summary>
    /// Whether to enable FTS5 search operations.
    /// </summary>
    public bool EnableFtsSearch { get; set; } = true;

    /// <summary>
    /// Minimum similarity threshold for vector searches (0.0 to 1.0).
    /// </summary>
    public float VectorSimilarityThreshold { get; set; } = 0.7f;

    /// <summary>
    /// Hierarchical weights for different result types.
    /// </summary>
    public Dictionary<UnifiedResultType, float> TypeWeights { get; set; } = new()
    {
        { UnifiedResultType.Memory, 1.0f },
        { UnifiedResultType.Entity, 0.8f },
        { UnifiedResultType.Relationship, 0.7f }
    };

    /// <summary>
    /// Whether to enable graceful fallback when search operations fail.
    /// </summary>
    public bool EnableGracefulFallback { get; set; } = true;
} 