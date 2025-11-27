namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
///     Represents the response from an embedding generation request
/// </summary>
public class EmbeddingResponse
{
    /// <summary>
    ///     The generated embeddings
    /// </summary>
    public required IReadOnlyList<EmbeddingItem> Embeddings { get; init; }

    /// <summary>
    ///     The model used to generate the embeddings
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    ///     Usage statistics for the request
    /// </summary>
    public EmbeddingUsage? Usage { get; init; }

    /// <summary>
    ///     Additional metadata from the provider
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
///     Represents a single embedding item
/// </summary>
public class EmbeddingItem
{
    /// <summary>
    ///     The embedding vector as float array
    /// </summary>
    public required float[] Vector { get; init; }

    /// <summary>
    ///     The index of this embedding in the original request
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    ///     The original text that was embedded
    /// </summary>
    public string? Text { get; init; }
}

/// <summary>
///     Usage statistics for embedding requests
/// </summary>
public class EmbeddingUsage
{
    /// <summary>
    ///     Number of tokens in the input
    /// </summary>
    public int PromptTokens { get; init; }

    /// <summary>
    ///     Total number of tokens used
    /// </summary>
    public int TotalTokens { get; init; }
}
