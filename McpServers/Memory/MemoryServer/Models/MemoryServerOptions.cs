using AchieveAi.LmDotnetTools.LmConfig.Models;

namespace MemoryServer.Models;

/// <summary>
/// MCP transport mode options.
/// </summary>
public enum TransportMode
{
    /// <summary>
    /// Server-Sent Events (SSE) transport over HTTP.
    /// </summary>
    SSE,

    /// <summary>
    /// Standard Input/Output (STDIO) transport.
    /// </summary>
    STDIO
}

/// <summary>
/// Configuration options for the Memory MCP Server.
/// </summary>
public class MemoryServerOptions
{
    /// <summary>
    /// MCP transport configuration options.
    /// </summary>
    public TransportOptions Transport { get; set; } = new();

    /// <summary>
    /// Database configuration options.
    /// </summary>
    public DatabaseOptions Database { get; set; } = new();

    /// <summary>
    /// LLM provider configuration options.
    /// </summary>
    public LLMOptions LLM { get; set; } = new();

    /// <summary>
    /// LmConfig integration options.
    /// </summary>
    public LmConfigOptions LmConfig { get; set; } = new();

    /// <summary>
    /// Memory-specific configuration options.
    /// </summary>
    public MemoryOptions Memory { get; set; } = new();

    /// <summary>
    /// Embedding and vector storage configuration options.
    /// </summary>
    public EmbeddingOptions Embedding { get; set; } = new();

    /// <summary>
    /// Server configuration options.
    /// </summary>
    public ServerOptions Server { get; set; } = new();

    /// <summary>
    /// Session defaults configuration options.
    /// </summary>
    public SessionDefaultsOptions SessionDefaults { get; set; } = new();

    /// <summary>
    /// Unified search configuration options for Phase 6.
    /// </summary>
    public UnifiedSearchOptions UnifiedSearch { get; set; } = new();
}

/// <summary>
/// MCP transport configuration options.
/// </summary>
public class TransportOptions
{
    /// <summary>
    /// Transport mode to use (SSE or STDIO).
    /// </summary>
    public TransportMode Mode { get; set; } = TransportMode.SSE;

    /// <summary>
    /// Port to listen on for SSE transport.
    /// </summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// Host to bind to for SSE transport.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Whether to enable CORS for SSE transport.
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// CORS origins to allow for SSE transport.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = ["http://localhost:3000", "http://127.0.0.1:3000"];
}

/// <summary>
/// LLM provider configuration options.
/// </summary>
public class LLMOptions
{
    /// <summary>
    /// Default LLM provider to use.
    /// </summary>
    public string DefaultProvider { get; set; } = "openai";

    /// <summary>
    /// Whether to enable LLM-powered graph processing for entity and relationship extraction.
    /// </summary>
    public bool EnableGraphProcessing { get; set; } = true;

    /// <summary>
    /// OpenAI provider configuration.
    /// </summary>
    public OpenAIOptions OpenAI { get; set; } = new();

    /// <summary>
    /// Anthropic provider configuration.
    /// </summary>
    public AnthropicOptions Anthropic { get; set; } = new();
}

/// <summary>
/// OpenAI provider configuration.
/// </summary>
public class OpenAIOptions
{
    /// <summary>
    /// OpenAI API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// OpenAI model to use.
    /// </summary>
    public string Model { get; set; } = "gpt-4";

    /// <summary>
    /// Temperature for OpenAI requests.
    /// </summary>
    public float Temperature { get; set; } = 0.0f;

    /// <summary>
    /// Maximum tokens for OpenAI requests.
    /// </summary>
    public int MaxTokens { get; set; } = 1000;

    /// <summary>
    /// Timeout for OpenAI requests in seconds.
    /// </summary>
    public int Timeout { get; set; } = 30;

    /// <summary>
    /// Maximum retries for OpenAI requests.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
}

/// <summary>
/// Anthropic provider configuration.
/// </summary>
public class AnthropicOptions
{
    /// <summary>
    /// Anthropic API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Anthropic model to use.
    /// </summary>
    public string Model { get; set; } = "claude-3-sonnet-20240229";

    /// <summary>
    /// Temperature for Anthropic requests.
    /// </summary>
    public float Temperature { get; set; } = 0.0f;

    /// <summary>
    /// Maximum tokens for Anthropic requests.
    /// </summary>
    public int MaxTokens { get; set; } = 1000;

    /// <summary>
    /// Timeout for Anthropic requests in seconds.
    /// </summary>
    public int Timeout { get; set; } = 30;

    /// <summary>
    /// Maximum retries for Anthropic requests.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
}

/// <summary>
/// Memory-specific configuration options.
/// </summary>
public class MemoryOptions
{
    /// <summary>
    /// Maximum length of memory content.
    /// </summary>
    public int MaxMemoryLength { get; set; } = 10000;

    /// <summary>
    /// Default limit for search results.
    /// </summary>
    public int DefaultSearchLimit { get; set; } = 10;

    /// <summary>
    /// Default score threshold for search results.
    /// </summary>
    public float DefaultScoreThreshold { get; set; } = 0.7f;

    /// <summary>
    /// Cache size for memory operations.
    /// </summary>
    public int CacheSize { get; set; } = 1000;

    /// <summary>
    /// Whether to enable caching.
    /// </summary>
    public bool EnableCaching { get; set; } = true;
}

/// <summary>
/// Server configuration options.
/// </summary>
public class ServerOptions
{
    /// <summary>
    /// Port to listen on.
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Host to bind to.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Whether to enable CORS.
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// Maximum concurrent connections.
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = 100;
}

/// <summary>
/// Session defaults configuration options.
/// </summary>
public class SessionDefaultsOptions
{
    /// <summary>
    /// Default user ID for sessions.
    /// </summary>
    public string DefaultUserId { get; set; } = "default_user";

    /// <summary>
    /// Cleanup interval for session defaults in minutes.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum age for session defaults in minutes.
    /// </summary>
    public int MaxSessionAge { get; set; } = 1440; // 24 hours
}

/// <summary>
/// LmConfig integration configuration options.
/// </summary>
public class LmConfigOptions
{
    /// <summary>
    /// Direct AppConfig instance. If provided, this takes precedence over loading from file.
    /// </summary>
    public AppConfig? AppConfig { get; set; }

    /// <summary>
    /// Path to the LmConfig models.json file. Used only if AppConfig is not provided.
    /// </summary>
    public string ConfigPath { get; set; } = "config/models.json";

    /// <summary>
    /// Default fallback strategy for model selection.
    /// </summary>
    public string FallbackStrategy { get; set; } = "cost-optimized";

    /// <summary>
    /// Cost optimization settings.
    /// </summary>
    public CostOptimizationOptions CostOptimization { get; set; } = new();
}

/// <summary>
/// Cost optimization configuration options.
/// </summary>
public class CostOptimizationOptions
{
    /// <summary>
    /// Whether cost optimization is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum cost per request in USD.
    /// </summary>
    public decimal MaxCostPerRequest { get; set; } = 0.01m;

    /// <summary>
    /// Daily cost limit in USD.
    /// </summary>
    public decimal DailyCostLimit { get; set; } = 10.00m;
}

/// <summary>
/// Embedding and vector storage configuration options.
/// </summary>
public class EmbeddingOptions
{
    /// <summary>
    /// Whether to enable vector storage and semantic search.
    /// </summary>
    public bool EnableVectorStorage { get; set; } = true;

    /// <summary>
    /// Cache expiration time in hours for generated embeddings.
    /// </summary>
    public int CacheExpirationHours { get; set; } = 24;

    /// <summary>
    /// Maximum size of the embedding cache in entries.
    /// </summary>
    public int MaxCacheSize { get; set; } = 10000;

    /// <summary>
    /// Default similarity threshold for vector search (0.0 to 1.0).
    /// </summary>
    public float DefaultSimilarityThreshold { get; set; } = 0.7f;

    /// <summary>
    /// Maximum number of results to return from vector search.
    /// </summary>
    public int MaxVectorSearchResults { get; set; } = 50;

    /// <summary>
    /// Weight for traditional search in hybrid search (0.0 to 1.0).
    /// </summary>
    public float TraditionalSearchWeight { get; set; } = 0.3f;

    /// <summary>
    /// Weight for vector search in hybrid search (0.0 to 1.0).
    /// </summary>
    public float VectorSearchWeight { get; set; } = 0.7f;

    /// <summary>
    /// Batch size for processing multiple embeddings at once.
    /// </summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Whether to generate embeddings automatically when adding memories.
    /// </summary>
    public bool AutoGenerateEmbeddings { get; set; } = true;

    /// <summary>
    /// Whether to use hybrid search (combining FTS5 and vector search) by default.
    /// </summary>
    public bool UseHybridSearch { get; set; } = true;
} 