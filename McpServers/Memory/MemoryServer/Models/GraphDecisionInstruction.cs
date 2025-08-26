namespace MemoryServer.Models;

/// <summary>
/// Represents an instruction for updating the knowledge graph (entities or relationships).
/// Used by the graph decision engine to determine what operations to perform.
/// </summary>
public class GraphDecisionInstruction
{
    /// <summary>
    /// The operation to perform.
    /// </summary>
    public GraphDecisionOperation Operation { get; set; }

    /// <summary>
    /// Entity data for entity operations (null for relationship operations).
    /// </summary>
    public Entity? EntityData { get; set; }

    /// <summary>
    /// Relationship data for relationship operations (null for entity operations).
    /// </summary>
    public Relationship? RelationshipData { get; set; }

    /// <summary>
    /// Confidence score for this instruction (0.0 to 1.0).
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// Reasoning for why this operation should be performed.
    /// </summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>
    /// Session context for this operation.
    /// </summary>
    public SessionContext SessionContext { get; set; } = new();

    /// <summary>
    /// When this instruction was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Enumeration of possible graph decision operations.
/// </summary>
public enum GraphDecisionOperation
{
    /// <summary>
    /// Add a new entity or relationship.
    /// </summary>
    ADD,

    /// <summary>
    /// Update an existing entity or relationship.
    /// </summary>
    UPDATE,

    /// <summary>
    /// Delete an existing entity or relationship.
    /// </summary>
    DELETE,

    /// <summary>
    /// No action needed.
    /// </summary>
    NONE,
}
