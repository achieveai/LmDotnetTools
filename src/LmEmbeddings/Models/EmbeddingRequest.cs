using LmEmbeddings.Models;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents a request to generate embeddings for text inputs
/// </summary>
public class EmbeddingRequest
{
    /// <summary>
    /// The text inputs to generate embeddings for
    /// </summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>
    /// The model to use for generating embeddings
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// The API type to use for the request
    /// </summary>
    public EmbeddingApiType ApiType { get; init; } = EmbeddingApiType.Default;

    /// <summary>
    /// Optional dimensions to reduce the embedding size
    /// </summary>
    public int? Dimensions { get; init; }

    /// <summary>
    /// Optional encoding format (base64, float, etc.)
    /// For Jina API: "float", "binary", "base64"
    /// For OpenAI API: "float", "base64"
    /// </summary>
    public string? EncodingFormat { get; init; }

    /// <summary>
    /// Optional user identifier for tracking
    /// </summary>
    public string? User { get; init; }

    /// <summary>
    /// Whether to normalize embeddings (Jina API specific)
    /// When true, scales the embedding so its Euclidean (L2) norm becomes 1
    /// </summary>
    public bool? Normalized { get; init; }

    /// <summary>
    /// Additional provider-specific options
    /// </summary>
    public Dictionary<string, object>? AdditionalOptions { get; init; }
} 