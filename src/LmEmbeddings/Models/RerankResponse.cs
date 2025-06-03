namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents the response from a reranking request
/// </summary>
public class RerankResponse
{
    /// <summary>
    /// The reranked documents with relevance scores
    /// </summary>
    public required IReadOnlyList<RerankResult> Results { get; init; }

    /// <summary>
    /// The model used for reranking
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// Usage statistics for the request
    /// </summary>
    public RerankUsage? Usage { get; init; }

    /// <summary>
    /// Additional metadata from the provider
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Represents a single reranked document result
/// </summary>
public class RerankResult
{
    /// <summary>
    /// The relevance score (higher is more relevant)
    /// </summary>
    public required double RelevanceScore { get; init; }

    /// <summary>
    /// The original index of this document in the input list
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// The document text (if requested)
    /// </summary>
    public string? Document { get; init; }
}

/// <summary>
/// Usage statistics for reranking requests
/// </summary>
public class RerankUsage
{
    /// <summary>
    /// Number of search units used
    /// </summary>
    public int SearchUnits { get; init; }
} 