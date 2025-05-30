using System.Text.Json.Serialization;

namespace MemoryServer.Models;

/// <summary>
/// Represents a stored memory with all associated metadata and content.
/// Uses integer IDs for better LLM integration and token efficiency.
/// </summary>
public class Memory
{
    /// <summary>
    /// Unique integer identifier for the memory.
    /// Generated using auto-incrementing sequence for better LLM comprehension.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The actual memory content/text.
    /// </summary>
    public string Content { get; set; } = string.Empty;

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
    /// Additional metadata stored as JSON.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// When the memory was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the memory was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Version number for optimistic concurrency control.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Similarity score when returned from search operations.
    /// Not stored in database.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Score { get; set; }

    /// <summary>
    /// Vector embedding for semantic search.
    /// Not included in JSON serialization by default.
    /// </summary>
    [JsonIgnore]
    public float[]? Embedding { get; set; }

    /// <summary>
    /// Creates a copy of the memory with updated timestamp.
    /// </summary>
    public Memory WithUpdatedTimestamp()
    {
        var copy = new Memory
        {
            Id = this.Id,
            Content = this.Content,
            UserId = this.UserId,
            AgentId = this.AgentId,
            RunId = this.RunId,
            Metadata = this.Metadata != null ? new Dictionary<string, object>(this.Metadata) : null,
            CreatedAt = this.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            Version = this.Version + 1,
            Embedding = this.Embedding
        };
        return copy;
    }

    /// <summary>
    /// Creates a memory for search results with score.
    /// </summary>
    public Memory WithScore(float score)
    {
        var copy = new Memory
        {
            Id = this.Id,
            Content = this.Content,
            UserId = this.UserId,
            AgentId = this.AgentId,
            RunId = this.RunId,
            Metadata = this.Metadata,
            CreatedAt = this.CreatedAt,
            UpdatedAt = this.UpdatedAt,
            Version = this.Version,
            Score = score
        };
        return copy;
    }

    /// <summary>
    /// Gets the session context for this memory.
    /// </summary>
    public SessionContext GetSessionContext()
    {
        return new SessionContext
        {
            UserId = UserId,
            AgentId = AgentId,
            RunId = RunId
        };
    }
} 