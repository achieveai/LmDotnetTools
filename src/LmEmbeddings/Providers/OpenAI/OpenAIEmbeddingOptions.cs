namespace AchieveAi.LmDotnetTools.LmEmbeddings.Providers.OpenAI;

/// <summary>
/// Configuration options for OpenAI embedding service
/// </summary>
public class OpenAIEmbeddingOptions
{
    /// <summary>
    /// The OpenAI API key
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The base URL for the OpenAI API (defaults to https://api.openai.com)
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com";

    /// <summary>
    /// The organization ID (optional)
    /// </summary>
    public string? Organization { get; set; }

    /// <summary>
    /// Default model to use for embeddings
    /// </summary>
    public string DefaultModel { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Default encoding format for embeddings
    /// </summary>
    public string DefaultEncodingFormat { get; set; } = "base64";

    /// <summary>
    /// Maximum number of retries for failed requests
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
} 