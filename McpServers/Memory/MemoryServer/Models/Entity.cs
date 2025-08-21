namespace MemoryServer.Models;

/// <summary>
/// Represents an entity extracted from conversations for knowledge graph construction.
/// Uses integer IDs for better LLM integration and token efficiency.
/// </summary>
public class Entity
{
    /// <summary>
    /// Unique integer identifier for the entity.
    /// Generated using auto-incrementing sequence for better LLM comprehension.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the entity (e.g., "USER_ID", "Italian cuisine", "John").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional type classification for the entity (e.g., "person", "place", "concept").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Alternative names or aliases for this entity.
    /// Stored as JSON array in database.
    /// </summary>
    public List<string>? Aliases { get; set; }

    /// <summary>
    /// User identifier for session isolation.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Optional agent identifier for finer session control.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Optional run identifier for conversation-level isolation.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    /// When the entity was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the entity was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Confidence score for this entity (0.0 to 1.0).
    /// Higher values indicate more certain extraction.
    /// </summary>
    public float Confidence { get; set; } = 1.0f;

    /// <summary>
    /// List of memory IDs that mentioned this entity.
    /// Stored as JSON array in database.
    /// </summary>
    public List<int>? SourceMemoryIds { get; set; }

    /// <summary>
    /// Additional metadata stored as JSON.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Version number for optimistic concurrency control.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets the session context for this entity.
    /// </summary>
    /// <returns>SessionContext containing the entity's session information.</returns>
    public SessionContext GetSessionContext()
    {
        return new SessionContext
        {
            UserId = UserId,
            AgentId = string.IsNullOrEmpty(AgentId) ? null : AgentId,
            RunId = string.IsNullOrEmpty(RunId) ? null : RunId
        };
    }
}