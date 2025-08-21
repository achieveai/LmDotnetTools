namespace MemoryServer.Models;

/// <summary>
/// Request model for adding a memory.
/// </summary>
public class AddMemoryRequest
{
    /// <summary>
    /// The content of the memory to add.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Optional connection ID for session context.
    /// </summary>
    public string? ConnectionId { get; set; }
}

/// <summary>
/// Request model for searching memories.
/// </summary>
public class SearchMemoryRequest
{
    /// <summary>
    /// The search query.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// Optional connection ID for session context.
    /// </summary>
    public string? ConnectionId { get; set; }
}

/// <summary>
/// Request model for traversing the graph.
/// </summary>
public class TraverseGraphRequest
{
    /// <summary>
    /// The entity name to start traversal from.
    /// </summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum depth for traversal.
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Optional filter for relationship types.
    /// </summary>
    public IEnumerable<string>? RelationshipTypes { get; set; }

    /// <summary>
    /// Optional connection ID for session context.
    /// </summary>
    public string? ConnectionId { get; set; }
}

/// <summary>
/// Request model for hybrid search.
/// </summary>
public class HybridSearchRequest
{
    /// <summary>
    /// The search query.
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Whether to use graph traversal in search.
    /// </summary>
    public bool? UseGraphTraversal { get; set; }

    /// <summary>
    /// Maximum number of results to return.
    /// </summary>
    public int? MaxResults { get; set; }

    /// <summary>
    /// Optional connection ID for session context.
    /// </summary>
    public string? ConnectionId { get; set; }
}