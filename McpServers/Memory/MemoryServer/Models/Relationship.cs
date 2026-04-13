using System.Text.Json.Serialization;

namespace MemoryServer.Models;

/// <summary>
///     Represents a relationship between entities in the knowledge graph.
///     Uses integer IDs for better LLM integration and token efficiency.
/// </summary>
public class Relationship
{
    /// <summary>
    ///     Unique integer identifier for the relationship.
    ///     Generated using auto-incrementing sequence for better LLM comprehension.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     The source entity name in the relationship.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    ///     The type of relationship (e.g., "prefers", "works_at", "lives_in").
    /// </summary>
    public string RelationshipType { get; set; } = string.Empty;

    /// <summary>
    ///     The target entity name in the relationship.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    ///     User identifier for session isolation.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    ///     Optional agent identifier for finer session control.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    ///     Optional run identifier for conversation-level isolation.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    ///     When the relationship was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     When the relationship was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Confidence score for the relationship extraction (0.0 to 1.0).
    /// </summary>
    public float Confidence { get; set; } = 1.0f;

    /// <summary>
    ///     Optional memory ID that this relationship was extracted from.
    /// </summary>
    public int? SourceMemoryId { get; set; }

    /// <summary>
    ///     Optional temporal context for the relationship (e.g., "current", "past", "future").
    /// </summary>
    public string? TemporalContext { get; set; }

    /// <summary>
    ///     Additional metadata stored as JSON.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    ///     Version number for optimistic concurrency control.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    ///     Similarity score when returned from search operations.
    ///     Not stored in database.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Score { get; set; }

    /// <summary>
    ///     Gets the session context for this relationship.
    /// </summary>
    /// <returns>SessionContext containing the relationship's session information.</returns>
    public SessionContext GetSessionContext()
    {
        return new SessionContext
        {
            UserId = UserId,
            AgentId = string.IsNullOrEmpty(AgentId) ? null : AgentId,
            RunId = string.IsNullOrEmpty(RunId) ? null : RunId,
        };
    }
}
