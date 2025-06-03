namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents a document with its relevance score after reranking
/// </summary>
public class RankedDocument
{
    /// <summary>
    /// The original index of this document in the input list
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// The relevance score (higher values indicate higher relevance)
    /// </summary>
    public required float Score { get; init; }

    /// <summary>
    /// The original document text (optional, for reference)
    /// </summary>
    public string? Document { get; init; }
} 