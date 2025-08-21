namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Configuration options for OpenAI embedding service
/// </summary>
public record RerankingOptions
{
    public string Provider { get; init; } = "jina";

    /// <summary>
    /// The OpenAI API key
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The base URL for the OpenAI API (defaults to https://api.openai.com)
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.jina.com/v1/rerank";

    /// <summary>
    /// The organization ID (optional)
    /// </summary>
    public string? Organization { get; init; }

    /// <summary>
    /// Default model to use for embeddings
    /// </summary>
    public string DefaultModel { get; init; } = "jina-reranker-m0";

    public Dictionary<string, RerankingModelConfig> AvailableModels { get; init; } = new()
    {
        ["jina-reranker-m0"] = new()
        {
            Model = "jina-reranker-m0",
            ChunkSize = 10240,
            IsMultiModal = true
        },
        ["jina-reranker-v2-base-multilingual"] = new()
        {
            Model = "jina-reranker-v2-base-multilingual",
            ChunkSize = 8192,
            IsMultiModal = false
        }
    };

    /// <summary>
    /// Maximum number of retries for failed requests
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
}

public record RerankingModelConfig
{
    public required int ChunkSize { get; init; }
    public required string Model { get; init; }
    public bool IsMultiModal { get; init; } = false;
}