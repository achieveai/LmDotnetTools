namespace LmEmbeddings.Models;

/// <summary>
///     Specifies the API format type for embedding requests.
/// </summary>
public enum EmbeddingApiType
{
    /// <summary>
    ///     Default API format (OpenAI-compatible).
    ///     Uses standard OpenAI embedding API request/response format.
    /// </summary>
    Default = 0,

    /// <summary>
    ///     Jina AI API format.
    ///     Uses Jina AI specific request/response format with additional parameters
    ///     like 'normalized' and 'embedding_type'.
    /// </summary>
    Jina = 1,
}
