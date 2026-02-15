using System.Text.Json.Serialization;

namespace MemoryServer.Models;

/// <summary>
///     Represents an instruction for updating relationships in the knowledge graph.
///     Used by the graph decision engine to determine what operations to perform.
/// </summary>
public class GraphUpdateInstruction
{
    /// <summary>
    ///     The action to perform on the relationship.
    /// </summary>
    [JsonPropertyName("action")]
    public GraphUpdateAction Action { get; set; }

    /// <summary>
    ///     The source entity name.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    ///     The target entity name.
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>
    ///     The new or updated relationship type.
    /// </summary>
    [JsonPropertyName("relationship")]
    public string Relationship { get; set; } = string.Empty;

    /// <summary>
    ///     The old relationship type (for UPDATE operations).
    /// </summary>
    [JsonPropertyName("old_relationship")]
    public string? OldRelationship { get; set; }

    /// <summary>
    ///     Confidence score for this update instruction (0.0 to 1.0).
    /// </summary>
    [JsonPropertyName("confidence")]
    public float? Confidence { get; set; }

    /// <summary>
    ///     Reasoning for why this update should be performed.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}

/// <summary>
///     Represents a collection of graph update instructions with metadata.
/// </summary>
public class GraphUpdateInstructions
{
    /// <summary>
    ///     The list of update instructions to execute.
    /// </summary>
    [JsonPropertyName("updates")]
    public List<GraphUpdateInstruction> Updates { get; set; } = [];

    /// <summary>
    ///     Metadata about the processing of these instructions.
    /// </summary>
    [JsonPropertyName("metadata")]
    public GraphUpdateMetadata Metadata { get; set; } = new();
}

/// <summary>
///     Metadata about the processing of graph update instructions.
/// </summary>
public class GraphUpdateMetadata
{
    /// <summary>
    ///     When the instructions were processed.
    /// </summary>
    [JsonPropertyName("processing_time")]
    public DateTime ProcessingTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The LLM model used to generate the instructions.
    /// </summary>
    [JsonPropertyName("model_used")]
    public string? ModelUsed { get; set; }

    /// <summary>
    ///     Total number of update instructions generated.
    /// </summary>
    [JsonPropertyName("total_updates")]
    public int TotalUpdates { get; set; }
}

/// <summary>
///     Enumeration of possible graph update actions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphUpdateAction
{
    /// <summary>
    ///     Update an existing relationship.
    /// </summary>
    UPDATE,

    /// <summary>
    ///     Add a new relationship.
    /// </summary>
    ADD,

    /// <summary>
    ///     Delete an existing relationship.
    /// </summary>
    DELETE,

    /// <summary>
    ///     No action needed for this relationship.
    /// </summary>
    NONE,
}
