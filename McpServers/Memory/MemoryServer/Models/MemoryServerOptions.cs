namespace MemoryServer.Models;

/// <summary>
/// Configuration options for the Memory MCP Server.
/// </summary>
public class MemoryServerOptions
{
    /// <summary>
    /// Database configuration options.
    /// </summary>
    public DatabaseOptions Database { get; set; } = new();

    /// <summary>
    /// LLM provider configuration options.
    /// </summary>
    public LLMOptions LLM { get; set; } = new();

    /// <summary>
    /// Memory-specific configuration options.
    /// </summary>
    public MemoryOptions Memory { get; set; } = new();

    /// <summary>
    /// Server configuration options.
    /// </summary>
    public ServerOptions Server { get; set; } = new();

    /// <summary>
    /// Session defaults configuration options.
    /// </summary>
    public SessionDefaultsOptions SessionDefaults { get; set; } = new();
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