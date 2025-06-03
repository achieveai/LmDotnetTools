namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents a request to rerank documents based on a query
/// </summary>
public class RerankRequest
{
    /// <summary>
    /// The query to rank documents against
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// The documents to rerank
    /// </summary>
    public required IReadOnlyList<string> Documents { get; init; }

    /// <summary>
    /// The model to use for reranking
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Maximum number of documents to return (top-k)
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Whether to return the document text in the response
    /// </summary>
    public bool ReturnDocuments { get; init; } = true;

    /// <summary>
    /// Additional provider-specific options
    /// </summary>
    public Dictionary<string, object>? AdditionalOptions { get; init; }
} 