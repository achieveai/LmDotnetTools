namespace MemoryServer.Models;

/// <summary>
/// Summary of graph updates performed when processing a memory.
/// </summary>
public class GraphUpdateSummary
{
    /// <summary>
    /// Number of entities added to the graph.
    /// </summary>
    public int EntitiesAdded { get; set; }

    /// <summary>
    /// Number of entities updated in the graph.
    /// </summary>
    public int EntitiesUpdated { get; set; }

    /// <summary>
    /// Number of relationships added to the graph.
    /// </summary>
    public int RelationshipsAdded { get; set; }

    /// <summary>
    /// Number of relationships updated in the graph.
    /// </summary>
    public int RelationshipsUpdated { get; set; }

    /// <summary>
    /// Total processing time in milliseconds.
    /// </summary>
    public long ProcessingTimeMs { get; set; }

    /// <summary>
    /// List of entities that were processed.
    /// </summary>
    public List<string> ProcessedEntities { get; set; } = new();

    /// <summary>
    /// List of relationship types that were processed.
    /// </summary>
    public List<string> ProcessedRelationshipTypes { get; set; } = new();

    /// <summary>
    /// Any warnings or issues encountered during processing.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Results from hybrid search combining traditional and graph-based search.
/// </summary>
public class HybridSearchResults
{
    /// <summary>
    /// Traditional search results (vector similarity, FTS, etc.).
    /// </summary>
    public List<Memory> TraditionalResults { get; set; } = new();

    /// <summary>
    /// Graph-based search results from entity traversal.
    /// </summary>
    public List<Memory> GraphResults { get; set; } = new();

    /// <summary>
    /// Combined and ranked results.
    /// </summary>
    public List<HybridSearchResult> CombinedResults { get; set; } = new();

    /// <summary>
    /// Entities found that are relevant to the search query.
    /// </summary>
    public List<Entity> RelevantEntities { get; set; } = new();

    /// <summary>
    /// Relationships found that are relevant to the search query.
    /// </summary>
    public List<Relationship> RelevantRelationships { get; set; } = new();

    /// <summary>
    /// Total search time in milliseconds.
    /// </summary>
    public long SearchTimeMs { get; set; }
}

/// <summary>
/// Individual result from hybrid search with scoring information.
/// </summary>
public class HybridSearchResult
{
    /// <summary>
    /// The memory result.
    /// </summary>
    public Memory Memory { get; set; } = new();

    /// <summary>
    /// Traditional search score (0.0 to 1.0).
    /// </summary>
    public float TraditionalScore { get; set; }

    /// <summary>
    /// Graph-based search score (0.0 to 1.0).
    /// </summary>
    public float GraphScore { get; set; }

    /// <summary>
    /// Combined final score (0.0 to 1.0).
    /// </summary>
    public float CombinedScore { get; set; }

    /// <summary>
    /// Source of the result (Traditional, Graph, or Both).
    /// </summary>
    public SearchResultSource Source { get; set; }

    /// <summary>
    /// Entities in this memory that matched the search.
    /// </summary>
    public List<string> MatchingEntities { get; set; } = new();

    /// <summary>
    /// Relationships in this memory that matched the search.
    /// </summary>
    public List<string> MatchingRelationships { get; set; } = new();
}

/// <summary>
/// Source of a search result.
/// </summary>
public enum SearchResultSource
{
    /// <summary>
    /// Result came from traditional search methods.
    /// </summary>
    Traditional,

    /// <summary>
    /// Result came from graph traversal.
    /// </summary>
    Graph,

    /// <summary>
    /// Result came from both traditional and graph methods.
    /// </summary>
    Both,
}

/// <summary>
/// Result of graph traversal showing related entities and relationships.
/// </summary>
public class GraphTraversalResult
{
    /// <summary>
    /// The starting entity for traversal.
    /// </summary>
    public Entity StartEntity { get; set; } = new();

    /// <summary>
    /// Entities found during traversal with their depth.
    /// </summary>
    public List<(
        Entity Entity,
        Relationship? Relationship,
        int Depth
    )> TraversalResults { get; set; } = new();

    /// <summary>
    /// All unique entities found.
    /// </summary>
    public List<Entity> AllEntities { get; set; } = new();

    /// <summary>
    /// All unique relationships found.
    /// </summary>
    public List<Relationship> AllRelationships { get; set; } = new();

    /// <summary>
    /// Maximum depth reached during traversal.
    /// </summary>
    public int MaxDepthReached { get; set; }

    /// <summary>
    /// Total traversal time in milliseconds.
    /// </summary>
    public long TraversalTimeMs { get; set; }
}

/// <summary>
/// Summary of graph rebuild operation.
/// </summary>
public class GraphRebuildSummary
{
    /// <summary>
    /// Number of memories processed.
    /// </summary>
    public int MemoriesProcessed { get; set; }

    /// <summary>
    /// Number of entities created.
    /// </summary>
    public int EntitiesCreated { get; set; }

    /// <summary>
    /// Number of relationships created.
    /// </summary>
    public int RelationshipsCreated { get; set; }

    /// <summary>
    /// Number of entities that were merged during rebuild.
    /// </summary>
    public int EntitiesMerged { get; set; }

    /// <summary>
    /// Number of relationships that were merged during rebuild.
    /// </summary>
    public int RelationshipsMerged { get; set; }

    /// <summary>
    /// Total rebuild time in milliseconds.
    /// </summary>
    public long RebuildTimeMs { get; set; }

    /// <summary>
    /// Any errors encountered during rebuild.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Warnings encountered during rebuild.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// When the rebuild was started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the rebuild was completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Result of graph validation operation.
/// </summary>
public class GraphValidationResult
{
    /// <summary>
    /// Whether the graph passed validation.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Number of entities validated.
    /// </summary>
    public int EntitiesValidated { get; set; }

    /// <summary>
    /// Number of relationships validated.
    /// </summary>
    public int RelationshipsValidated { get; set; }

    /// <summary>
    /// Validation errors found.
    /// </summary>
    public List<GraphValidationError> Errors { get; set; } = new();

    /// <summary>
    /// Validation warnings found.
    /// </summary>
    public List<GraphValidationWarning> Warnings { get; set; } = new();

    /// <summary>
    /// Orphaned entities (entities with no relationships).
    /// </summary>
    public List<Entity> OrphanedEntities { get; set; } = new();

    /// <summary>
    /// Broken relationships (relationships referencing non-existent entities).
    /// </summary>
    public List<Relationship> BrokenRelationships { get; set; } = new();

    /// <summary>
    /// Total validation time in milliseconds.
    /// </summary>
    public long ValidationTimeMs { get; set; }
}

/// <summary>
/// Graph validation error.
/// </summary>
public class GraphValidationError
{
    /// <summary>
    /// Type of validation error.
    /// </summary>
    public GraphValidationErrorType Type { get; set; }

    /// <summary>
    /// Description of the error.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Entity ID associated with the error (if applicable).
    /// </summary>
    public int? EntityId { get; set; }

    /// <summary>
    /// Relationship ID associated with the error (if applicable).
    /// </summary>
    public int? RelationshipId { get; set; }

    /// <summary>
    /// Severity of the error.
    /// </summary>
    public ValidationSeverity Severity { get; set; }
}

/// <summary>
/// Graph validation warning.
/// </summary>
public class GraphValidationWarning
{
    /// <summary>
    /// Type of validation warning.
    /// </summary>
    public GraphValidationWarningType Type { get; set; }

    /// <summary>
    /// Description of the warning.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Entity ID associated with the warning (if applicable).
    /// </summary>
    public int? EntityId { get; set; }

    /// <summary>
    /// Relationship ID associated with the warning (if applicable).
    /// </summary>
    public int? RelationshipId { get; set; }
}

/// <summary>
/// Types of graph validation errors.
/// </summary>
public enum GraphValidationErrorType
{
    /// <summary>
    /// Relationship references non-existent entity.
    /// </summary>
    BrokenReference,

    /// <summary>
    /// Duplicate entity with same name.
    /// </summary>
    DuplicateEntity,

    /// <summary>
    /// Duplicate relationship.
    /// </summary>
    DuplicateRelationship,

    /// <summary>
    /// Invalid data format.
    /// </summary>
    InvalidData,

    /// <summary>
    /// Constraint violation.
    /// </summary>
    ConstraintViolation,
}

/// <summary>
/// Types of graph validation warnings.
/// </summary>
public enum GraphValidationWarningType
{
    /// <summary>
    /// Entity has no relationships.
    /// </summary>
    OrphanedEntity,

    /// <summary>
    /// Low confidence score.
    /// </summary>
    LowConfidence,

    /// <summary>
    /// Missing metadata.
    /// </summary>
    MissingMetadata,

    /// <summary>
    /// Potential duplicate.
    /// </summary>
    PotentialDuplicate,
}

/// <summary>
/// Severity levels for validation issues.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>
    /// Low severity - informational.
    /// </summary>
    Low,

    /// <summary>
    /// Medium severity - should be addressed.
    /// </summary>
    Medium,

    /// <summary>
    /// High severity - must be fixed.
    /// </summary>
    High,

    /// <summary>
    /// Critical severity - system integrity at risk.
    /// </summary>
    Critical,
}
