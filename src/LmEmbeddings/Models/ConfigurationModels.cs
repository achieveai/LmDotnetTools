using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Comprehensive configuration for embedding services
/// </summary>
public record ServiceConfiguration
{
    /// <summary>
    /// Unique identifier for this service configuration
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable name for this configuration
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The service provider type (OpenAI, Jina, Cohere, etc.)
    /// </summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>
    /// Endpoint configuration for the service
    /// </summary>
    [JsonPropertyName("endpoint")]
    public required EndpointConfiguration Endpoint { get; init; }

    /// <summary>
    /// Default model settings
    /// </summary>
    [JsonPropertyName("default_model")]
    public ModelConfiguration? DefaultModel { get; init; }

    /// <summary>
    /// Available models for this service
    /// </summary>
    [JsonPropertyName("available_models")]
    public ImmutableList<ModelConfiguration>? AvailableModels { get; init; }

    /// <summary>
    /// Retry and resilience settings
    /// </summary>
    [JsonPropertyName("resilience")]
    public ResilienceConfiguration? Resilience { get; init; }

    /// <summary>
    /// Rate limiting configuration
    /// </summary>
    [JsonPropertyName("rate_limits")]
    public RateLimitConfiguration? RateLimits { get; init; }

    /// <summary>
    /// Service capabilities and features
    /// </summary>
    [JsonPropertyName("capabilities")]
    public ServiceCapabilities? Capabilities { get; init; }

    /// <summary>
    /// Configuration metadata
    /// </summary>
    [JsonPropertyName("metadata")]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// When this configuration was created
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When this configuration was last updated
    /// </summary>
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this configuration is currently active
    /// </summary>
    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// Configuration for API endpoints
/// </summary>
public record EndpointConfiguration
{
    /// <summary>
    /// The base URL for the API
    /// </summary>
    [JsonPropertyName("base_url")]
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Authentication configuration
    /// </summary>
    [JsonPropertyName("authentication")]
    public AuthenticationConfiguration? Authentication { get; init; }

    /// <summary>
    /// Timeout settings in milliseconds
    /// </summary>
    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 30000;

    /// <summary>
    /// Custom headers to include with requests
    /// </summary>
    [JsonPropertyName("headers")]
    public ImmutableDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Whether to use HTTPS
    /// </summary>
    [JsonPropertyName("use_https")]
    public bool UseHttps { get; init; } = true;

    /// <summary>
    /// API version to use
    /// </summary>
    [JsonPropertyName("api_version")]
    public string? ApiVersion { get; init; }
}

/// <summary>
/// Authentication configuration for API access
/// </summary>
public record AuthenticationConfiguration
{
    /// <summary>
    /// The authentication type (Bearer, ApiKey, Basic, etc.)
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// The API key or token (should be stored securely)
    /// </summary>
    [JsonPropertyName("credentials")]
    public string? Credentials { get; init; }

    /// <summary>
    /// Additional authentication headers
    /// </summary>
    [JsonPropertyName("headers")]
    public ImmutableDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Organization or tenant ID (if applicable)
    /// </summary>
    [JsonPropertyName("organization")]
    public string? Organization { get; init; }
}

/// <summary>
/// Configuration for a specific model
/// </summary>
public record ModelConfiguration
{
    /// <summary>
    /// The model identifier
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable model name
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// The size of embeddings this model produces
    /// </summary>
    [JsonPropertyName("embedding_size")]
    public int? EmbeddingSize { get; init; }

    /// <summary>
    /// Maximum input length in tokens
    /// </summary>
    [JsonPropertyName("max_input_tokens")]
    public int? MaxInputTokens { get; init; }

    /// <summary>
    /// Supported languages (ISO codes)
    /// </summary>
    [JsonPropertyName("supported_languages")]
    public ImmutableList<string>? SupportedLanguages { get; init; }

    /// <summary>
    /// Model capabilities and features
    /// </summary>
    [JsonPropertyName("capabilities")]
    public ModelCapabilities? Capabilities { get; init; }

    /// <summary>
    /// Pricing information (if available)
    /// </summary>
    [JsonPropertyName("pricing")]
    public PricingInfo? Pricing { get; init; }

    /// <summary>
    /// Whether this model is currently available
    /// </summary>
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; init; } = true;
}

/// <summary>
/// Resilience and retry configuration
/// </summary>
public record ResilienceConfiguration
{
    /// <summary>
    /// Maximum number of retry attempts
    /// </summary>
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Base delay between retries in milliseconds
    /// </summary>
    [JsonPropertyName("base_delay_ms")]
    public int BaseDelayMs { get; init; } = 1000;

    /// <summary>
    /// The retry strategy (Linear, Exponential, Fixed)
    /// </summary>
    [JsonPropertyName("strategy")]
    public RetryStrategy Strategy { get; init; } = RetryStrategy.Linear;

    /// <summary>
    /// Maximum delay between retries in milliseconds
    /// </summary>
    [JsonPropertyName("max_delay_ms")]
    public int MaxDelayMs { get; init; } = 30000;

    /// <summary>
    /// Circuit breaker settings
    /// </summary>
    [JsonPropertyName("circuit_breaker")]
    public CircuitBreakerConfiguration? CircuitBreaker { get; init; }

    /// <summary>
    /// Which error types should trigger retries
    /// </summary>
    [JsonPropertyName("retryable_errors")]
    public ImmutableList<string>? RetryableErrors { get; init; }
}

/// <summary>
/// Rate limiting configuration
/// </summary>
public record RateLimitConfiguration
{
    /// <summary>
    /// Maximum requests per minute
    /// </summary>
    [JsonPropertyName("requests_per_minute")]
    public int? RequestsPerMinute { get; init; }

    /// <summary>
    /// Maximum tokens per minute
    /// </summary>
    [JsonPropertyName("tokens_per_minute")]
    public int? TokensPerMinute { get; init; }

    /// <summary>
    /// Maximum concurrent requests
    /// </summary>
    [JsonPropertyName("max_concurrent")]
    public int? MaxConcurrent { get; init; }

    /// <summary>
    /// Burst allowance for short periods
    /// </summary>
    [JsonPropertyName("burst_allowance")]
    public int? BurstAllowance { get; init; }
}

/// <summary>
/// Circuit breaker configuration
/// </summary>
public record CircuitBreakerConfiguration
{
    /// <summary>
    /// Number of failures before opening the circuit
    /// </summary>
    [JsonPropertyName("failure_threshold")]
    public int FailureThreshold { get; init; } = 5;

    /// <summary>
    /// Time to wait before attempting to close the circuit (in milliseconds)
    /// </summary>
    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 60000;

    /// <summary>
    /// Percentage of requests that must succeed to close the circuit
    /// </summary>
    [JsonPropertyName("success_threshold")]
    public double SuccessThreshold { get; init; } = 0.8;
}

/// <summary>
/// Service capabilities and feature support
/// </summary>
public record ServiceCapabilities
{
    /// <summary>
    /// Whether the service supports batch processing
    /// </summary>
    [JsonPropertyName("supports_batch")]
    public bool SupportsBatch { get; init; }

    /// <summary>
    /// Whether the service supports reranking
    /// </summary>
    [JsonPropertyName("supports_reranking")]
    public bool SupportsReranking { get; init; }

    /// <summary>
    /// Whether the service supports custom dimensions
    /// </summary>
    [JsonPropertyName("supports_custom_dimensions")]
    public bool SupportsCustomDimensions { get; init; }

    /// <summary>
    /// Whether the service supports normalized embeddings
    /// </summary>
    [JsonPropertyName("supports_normalization")]
    public bool SupportsNormalization { get; init; }

    /// <summary>
    /// Supported encoding formats
    /// </summary>
    [JsonPropertyName("encoding_formats")]
    public ImmutableList<string>? EncodingFormats { get; init; }

    /// <summary>
    /// Maximum batch size supported
    /// </summary>
    [JsonPropertyName("max_batch_size")]
    public int? MaxBatchSize { get; init; }

    /// <summary>
    /// Whether the service supports streaming responses
    /// </summary>
    [JsonPropertyName("supports_streaming")]
    public bool SupportsStreaming { get; init; }
}

/// <summary>
/// Model-specific capabilities
/// </summary>
public record ModelCapabilities
{
    /// <summary>
    /// Whether this model supports multilingual input
    /// </summary>
    [JsonPropertyName("multilingual")]
    public bool IsMultilingual { get; init; }

    /// <summary>
    /// Whether this model supports custom dimensions
    /// </summary>
    [JsonPropertyName("custom_dimensions")]
    public bool SupportsCustomDimensions { get; init; }

    /// <summary>
    /// Whether this model supports document reranking
    /// </summary>
    [JsonPropertyName("reranking")]
    public bool SupportsReranking { get; init; }

    /// <summary>
    /// Domain specializations for this model
    /// </summary>
    [JsonPropertyName("domains")]
    public ImmutableList<string>? Domains { get; init; }
}

/// <summary>
/// Pricing information for models or services
/// </summary>
public record PricingInfo
{
    /// <summary>
    /// Cost per 1000 tokens
    /// </summary>
    [JsonPropertyName("cost_per_1k_tokens")]
    public decimal? CostPer1KTokens { get; init; }

    /// <summary>
    /// Currency for pricing
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Pricing tier or plan name
    /// </summary>
    [JsonPropertyName("tier")]
    public string? Tier { get; init; }
}

/// <summary>
/// Health check result for a service
/// </summary>
public record HealthCheckResult
{
    /// <summary>
    /// The service being checked
    /// </summary>
    [JsonPropertyName("service")]
    public required string Service { get; init; }

    /// <summary>
    /// Overall health status
    /// </summary>
    [JsonPropertyName("status")]
    public HealthStatus Status { get; init; }

    /// <summary>
    /// Timestamp of the health check
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Response time in milliseconds
    /// </summary>
    [JsonPropertyName("response_time_ms")]
    public double ResponseTimeMs { get; init; }

    /// <summary>
    /// Detailed check results
    /// </summary>
    [JsonPropertyName("checks")]
    public ImmutableList<ComponentHealth>? Checks { get; init; }

    /// <summary>
    /// Additional health information
    /// </summary>
    [JsonPropertyName("details")]
    public ImmutableDictionary<string, object>? Details { get; init; }

    /// <summary>
    /// Error message if unhealthy
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Health status for individual components
/// </summary>
public record ComponentHealth
{
    /// <summary>
    /// Name of the component being checked
    /// </summary>
    [JsonPropertyName("component")]
    public required string Component { get; init; }

    /// <summary>
    /// Health status of this component
    /// </summary>
    [JsonPropertyName("status")]
    public HealthStatus Status { get; init; }

    /// <summary>
    /// Component-specific details
    /// </summary>
    [JsonPropertyName("details")]
    public ImmutableDictionary<string, object>? Details { get; init; }

    /// <summary>
    /// Error message if unhealthy
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Health status enumeration
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus
{
    /// <summary>
    /// Service is healthy and operational
    /// </summary>
    Healthy,

    /// <summary>
    /// Service is operational but with degraded performance
    /// </summary>
    Degraded,

    /// <summary>
    /// Service is not responding or failing
    /// </summary>
    Unhealthy,

    /// <summary>
    /// Health status is unknown
    /// </summary>
    Unknown
}

/// <summary>
/// Retry strategy enumeration
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RetryStrategy
{
    /// <summary>
    /// Fixed delay between retries
    /// </summary>
    Fixed,

    /// <summary>
    /// Linear increase in delay (delay × retry_count)
    /// </summary>
    Linear,

    /// <summary>
    /// Exponential backoff (delay × 2^retry_count)
    /// </summary>
    Exponential,

    /// <summary>
    /// Custom strategy defined by the implementation
    /// </summary>
    Custom
}