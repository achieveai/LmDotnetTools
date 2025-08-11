namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Configuration options for OpenAI embedding service
/// </summary>
public record EmbeddingOptions
{
    public string Provider { get; init; } = "jina";

    /// <summary>
    /// The OpenAI API key
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The base URL for the OpenAI API (defaults to https://api.openai.com)
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.jina.com/v1/embeddings";

    /// <summary>
    /// The organization ID (optional)
    /// </summary>
    public string? Organization { get; init; }

    /// <summary>
    /// Default model to use for embeddings
    /// </summary>
    public string DefaultModel { get; init; } = "jina-clip-v2";

    public Dictionary<string, EmbeddingModelConfig> AvailableModelsWithDimensions { get; init; } = new()
    {
        { "jina-clip-v2", new EmbeddingModelConfig { Model = "jina-clip-v2", Dimensions = 1024, IsMultiModal = true } },
        { "jina-embeddings-v3", new EmbeddingModelConfig { Model = "jina-embeddings-v3", Dimensions = 1024, IsMultiModal = false } },
        { "jina-embeddings-v4", new EmbeddingModelConfig { Model = "jina-embeddings-v4", Dimensions = 2048, IsMultiModal = true, ChunkSize = 32*1024 } }
    };

    /// <summary>
    /// Default encoding format for embeddings
    /// For Jina API: "float", "binary", "base64", "ubinary"
    /// </summary>
    public string DefaultEncodingFormat { get; init; } = "float";

    /// <summary>
    /// Maximum number of retries for failed requests
    /// </summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
}

public record EmbeddingModelConfig
{
    public string Model { get; set; } = "jina-clip-v2";
    public int Dimensions { get; set; } = 1024;
    public bool IsMultiModal { get; set; } = false;
    public int ChunkSize { get; set; } = 8*1024;
}