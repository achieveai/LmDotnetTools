# LMConfig Unified Design Document

## Executive Summary

This document presents a unified configuration system that integrates two complementary approaches:
1. **Infrastructure Layer** (existing): Model/provider availability, priority-based selection, and operational concerns
2. **Configuration Layer** (new): Type-safe, feature-rich request configuration with provider-specific capabilities

The unified system provides both production-ready provider management and excellent developer experience for configuring LLM requests, with comprehensive support for cost-aware and performance-based provider selection.

## Architecture Overview

### Two-Layer Configuration System

```
┌─────────────────────────────────────────────────────────────────┐
│                    Application Layer                            │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐    ┌──────────────────┐                   │
│  │   LMConfig      │    │  LMConfigBuilder │                   │
│  │ (Request Config)│───▶│  (Fluent API)    │                   │
│  │ - Features      │    │ - Type Safety    │                   │
│  │ - Parameters    │    │ - Cost Awareness │                   │
│  │ - Tags/Budget   │    │ - Tag Selection  │                   │
│  └─────────────────┘    └──────────────────┘                   │
├─────────────────────────────────────────────────────────────────┤
│                   Infrastructure Layer                          │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐    ┌──────────────────┐                   │
│  │   AppConfig     │    │ModelConfigSvc    │                   │
│  │ (Provider Setup)│───▶│(Provider Select) │                   │
│  │ - Availability  │    │ - Priority       │                   │
│  │ - Pricing       │    │ - Tags           │                   │
│  │ - Tags          │    │ - Cost Estimation│                   │
│  │ - Credentials   │    │ - Fallbacks      │                   │
│  └─────────────────┘    └──────────────────┘                   │
├─────────────────────────────────────────────────────────────────┤
│                      Agent Creation                             │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐    ┌──────────────────┐                   │
│  │DynamicProvider  │    │ Enhanced Agent   │                   │
│  │Agent            │───▶│ with Features    │                   │
│  │ - Base Agent    │    │ - Configured     │                   │
│  │ - Middleware    │    │ - Cost Tracking  │                   │
│  │ - Cost Tracking │    │ - Validated      │                   │
│  └─────────────────┘    └──────────────────┘                   │
└─────────────────────────────────────────────────────────────────┘
```

## Infrastructure Layer (Existing)

### Purpose
Handles operational concerns for production deployments:
- Provider availability and credentials
- Priority-based provider selection with fallbacks
- Pricing and usage tracking
- Model-to-provider mapping
- Tag-based provider categorization

### Supported Providers

#### Native Providers
- **OpenAI**: Native OpenAI API integration with full feature support
- **Anthropic**: Native Anthropic API integration with thinking capabilities

#### OpenAI-Compatible Providers
Our architecture supports multiple OpenAI-compatible providers through our existing OpenAI provider implementation. These providers use the OpenAI API format but with different endpoints and potentially different model names:

- **DeepInfra** (`https://api.deepinfra.com/v1/openai/`): Multi-vendor model hosting with competitive pricing
- **Cerebras** (`https://api.cerebras.ai/v1/`): High-performance inference with hardware acceleration
- **Groq** (`https://api.groq.com/openai/v1/`): Ultra-fast inference for various models
- **OpenRouter** (`https://openrouter.ai/api/v1/`): Provider aggregator with failover capabilities
- **Google Gemini** (`https://generativelanguage.googleapis.com/v1beta/openai/`): Google's Gemini models via OpenAI-compatible API

#### Provider Characteristics

| Provider | Strengths | Tags | Use Cases |
|----------|-----------|------|-----------|
| **OpenAI** | Native features, high quality | `reliable`, `high-quality`, `premium` | Production applications |
| **Anthropic** | Advanced thinking, safety | `thinking`, `reliable`, `high-quality` | Complex reasoning tasks |
| **DeepInfra** | Cost-effective, multi-vendor | `economic`, `multi-vendor`, `openai-compatible` | Budget-conscious deployments |
| **Cerebras** | Ultra-fast inference | `ultra-fast`, `high-performance`, `openai-compatible` | Real-time applications |
| **Groq** | Hardware acceleration | `ultra-fast`, `high-performance`, `openai-compatible` | Speed-critical applications |
| **OpenRouter** | Provider aggregation, fallback | `aggregator`, `fallback`, `reliable`, `openai-compatible` | High availability requirements |
| **Google Gemini** | Long context, multimodal | `long-context`, `multimodal`, `economic`, `openai-compatible` | Large document processing |

### Key Components

#### AppConfig (from appsettings.json)
```csharp
public record AppConfig
{
    public required IReadOnlyList<ModelConfig> Models { get; init; }
}

public record ModelConfig
{
    public required string Id { get; init; }                    // e.g., "gpt-4"
    public required bool IsReasoning { get; init; }
    public required ModelCapabilities Capabilities { get; init; }  // NEW: Model-specific capabilities
    public required IReadOnlyList<ProviderConfig> Providers { get; init; }
}

public record ProviderConfig  // Infrastructure-level provider config
{
    public required string Name { get; init; }                 // e.g., "OpenAI"
    public required string ModelName { get; init; }            // Provider-specific model name
    public int Priority { get; init; } = 1;                    // Higher = preferred
    public required PricingConfig Pricing { get; init; }
    public IReadOnlyList<SubProviderConfig>? SubProviders { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }          // e.g., ["economic", "fast", "reliable"]
}

public record PricingConfig
{
    public required double PromptPerMillion { get; init; }      // Cost per million prompt tokens
    public required double CompletionPerMillion { get; init; }  // Cost per million completion tokens
}
```

#### ProviderRegistry (from appsettings.json)
```csharp
/// <summary>
/// Infrastructure-level provider connection registry - maps provider names to connection details
/// </summary>
public record ProviderRegistryConfig
{
    public required Dictionary<string, ProviderConnectionInfo> Providers { get; init; }
}

/// <summary>
/// Connection information for a provider, configured at infrastructure level
/// </summary>
public record ProviderConnectionInfo
{
    /// <summary>
    /// Base URL for the provider's API endpoint
    /// </summary>
    public required string EndpointUrl { get; init; }
    
    /// <summary>
    /// Name of the environment variable containing the API key
    /// </summary>
    public required string ApiKeyEnvironmentVariable { get; init; }
    
    /// <summary>
    /// Provider compatibility type for API format
    /// </summary>
    public ProviderCompatibility Compatibility { get; init; } = ProviderCompatibility.OpenAI;
    
    /// <summary>
    /// Optional custom headers to include in requests
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }
    
    /// <summary>
    /// Request timeout for this provider
    /// </summary>
    public TimeSpan? Timeout { get; init; }
    
    /// <summary>
    /// Maximum retry attempts for this provider
    /// </summary>
    public int MaxRetries { get; init; } = 3;
    
    /// <summary>
    /// Human-readable description of the provider
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Validate the connection configuration
    /// </summary>
    public ValidationResult Validate()
    {
        var result = new ValidationResult();
        
        if (string.IsNullOrWhiteSpace(EndpointUrl))
            result.AddError("EndpointUrl is required");
        else if (!Uri.TryCreate(EndpointUrl, UriKind.Absolute, out _))
            result.AddError("EndpointUrl must be a valid absolute URL");
            
        if (string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
            result.AddError("ApiKeyEnvironmentVariable is required");
            
        if (Timeout.HasValue && Timeout.Value <= TimeSpan.Zero)
            result.AddError("Timeout must be positive");
            
        if (MaxRetries < 0)
            result.AddError("MaxRetries cannot be negative");
            
        return result;
    }
    
    /// <summary>
    /// Get the API key from the environment variable
    /// </summary>
    public string? GetApiKey()
    {
        return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
    }
    
    /// <summary>
    /// Convert to request-level connection config
    /// </summary>
    public ProviderConnectionConfig ToConnectionConfig()
    {
        return new ProviderConnectionConfig
        {
            EndpointUrl = EndpointUrl,
            ApiKeyEnvironmentVariable = ApiKeyEnvironmentVariable,
            Compatibility = Compatibility,
            Headers = Headers,
            Timeout = Timeout,
            MaxRetries = MaxRetries
        };
    }
}

/// <summary>
/// Service for resolving provider connections from the registry
/// </summary>
public interface IProviderRegistry
{
    /// <summary>
    /// Get connection info for a provider by name
    /// </summary>
    ProviderConnectionInfo? GetProviderConnection(string providerName);
    
    /// <summary>
    /// Get all registered provider names
    /// </summary>
    IReadOnlyList<string> GetRegisteredProviders();
    
    /// <summary>
    /// Check if a provider is registered
    /// </summary>
    bool IsProviderRegistered(string providerName);
    
    /// <summary>
    /// Validate that all required environment variables are set
    /// </summary>
    ValidationResult ValidateEnvironmentVariables();
}

public class ProviderRegistry : IProviderRegistry
{
    private readonly ProviderRegistryConfig _config;
    
    public ProviderRegistry(IOptions<ProviderRegistryConfig> config)
    {
        _config = config.Value;
    }
    
    public ProviderConnectionInfo? GetProviderConnection(string providerName)
    {
        return _config.Providers.TryGetValue(providerName, out var connectionInfo) 
            ? connectionInfo 
            : null;
    }
    
    public IReadOnlyList<string> GetRegisteredProviders()
    {
        return _config.Providers.Keys.ToList();
    }
    
    public bool IsProviderRegistered(string providerName)
    {
        return _config.Providers.ContainsKey(providerName);
    }
    
    public ValidationResult ValidateEnvironmentVariables()
    {
        var result = new ValidationResult();
        
        foreach (var provider in _config.Providers)
        {
            var apiKey = provider.Value.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                result.AddWarning($"Environment variable '{provider.Value.ApiKeyEnvironmentVariable}' for provider '{provider.Key}' is not set");
            }
        }
        
        return result;
    }
}

#### ModelConfigurationService
- Resolves model IDs to available providers
- Implements priority-based selection with fallbacks
- Validates provider credentials via environment variables
- Handles OpenRouter fallback strategies
- **New**: Tag-based provider filtering and selection
- **New**: Cost estimation and budget-aware selection

## Provider Tags and Cost Management

### Predefined Provider Tags
```csharp
public static class ProviderTags
{
    // Performance characteristics
    public const string Economic = "economic";        // Low cost providers
    public const string Fast = "fast";               // Low latency providers
    public const string Reliable = "reliable";       // High uptime providers
    public const string HighQuality = "high-quality"; // Best output quality
    
    // Capability tags
    public const string Reasoning = "reasoning";     // Good for complex reasoning
    public const string Chat = "chat";              // Optimized for conversations
    public const string Coding = "coding";          // Good for code generation
    public const string Creative = "creative";      // Good for creative tasks
    public const string Multimodal = "multimodal";  // Supports images/audio
    
    // Provider characteristics
    public const string OpenSource = "open-source";  // Open source models
    public const string Enterprise = "enterprise";   // Enterprise-grade SLA
    public const string Experimental = "experimental"; // Beta/experimental features
}
```

### Cost Estimation and Tracking
```csharp
public class CostEstimation
{
    public decimal EstimatedPromptCost { get; set; }
    public decimal EstimatedCompletionCost { get; set; }
    public decimal TotalEstimatedCost { get; set; }
    public string SelectedProvider { get; set; }
    public string SelectedModel { get; set; }
    public int EstimatedPromptTokens { get; set; }
    public int EstimatedCompletionTokens { get; set; }
    public PricingConfig PricingInfo { get; set; }
}

public class CostReport
{
    public decimal ActualPromptCost { get; set; }
    public decimal ActualCompletionCost { get; set; }
    public decimal TotalActualCost { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public string Provider { get; set; }
    public string Model { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum ProviderSelectionStrategy
{
    Priority,           // Use priority-based selection (default)
    Economic,          // Prefer lowest cost providers
    Fast,              // Prefer fastest providers
    Balanced,          // Balance cost and performance
    HighQuality        // Prefer highest quality providers
}
```

## Configuration Layer (Enhanced)

### Purpose
Provides type-safe, feature-rich configuration for individual requests:
- Provider-specific feature configuration
- Parameter validation and type safety
- Fluent API for developer experience
- Request-level customization
- **New**: Cost-aware provider selection
- **New**: Tag-based provider preferences
- **New**: Budget constraints and cost estimation

### Key Components

#### Enhanced LMConfig (Request Configuration)
```csharp
public class LMConfig
{
    // Basic parameters (provider-agnostic)
    public string ModelId { get; set; }
    public float Temperature { get; set; } = 0.7f;
    public int? MaxTokens { get; set; }
    public float? TopP { get; set; }
    public int? TopK { get; set; }
    public float? FrequencyPenalty { get; set; }
    public float? PresencePenalty { get; set; }
    public string? User { get; set; }
    public bool Stream { get; set; } = false;
    public List<string>? StopSequences { get; set; }
    public int? Seed { get; set; }
    
    // Provider-specific feature configuration
    public ProviderFeatureConfig? ProviderFeatures { get; set; }
    
    // Function calling
    public List<FunctionContract>? Functions { get; set; }
    public ToolChoiceStrategy ToolChoice { get; set; } = ToolChoiceStrategy.Auto;
    
    // Provider selection preferences
    public List<string>? RequiredTags { get; set; }              // Must have these tags
    public List<string>? PreferredTags { get; set; }             // Prefer these tags
    public ProviderSelectionStrategy SelectionStrategy { get; set; } = ProviderSelectionStrategy.Priority;
    
    // Cost management
    public decimal? BudgetLimit { get; set; }                    // Max cost per request
    public bool EnableCostEstimation { get; set; } = false;     // Estimate cost before execution
    public bool EnableCostTracking { get; set; } = false;       // Track actual costs
    
    // Performance preferences
    public TimeSpan? MaxLatency { get; set; }                    // Max acceptable response time
    
    // Provider connection configuration (NEW)
    public ProviderConnectionConfig? ProviderConnection { get; set; }
    public List<ProviderConnectionConfig>? FallbackConnections { get; set; }
    
    // Provider registry reference (NEW) - simpler than full connection config
    public string? PreferredProviderName { get; set; }           // Reference provider by name from registry
    public List<string>? FallbackProviderNames { get; set; }    // Fallback providers by name
    
    // Validation and conversion
    public GenerateReplyOptions ToGenerateReplyOptions();
    public void Validate();
}
```

#### Provider Connection Configuration (NEW)
```csharp
/// <summary>
/// Configuration for provider connection details including endpoint and authentication
/// </summary>
public record ProviderConnectionConfig
{
    /// <summary>
    /// Base URL for the provider's API endpoint
    /// </summary>
    public required string EndpointUrl { get; init; }
    
    /// <summary>
    /// Name of the environment variable containing the API key
    /// </summary>
    public required string ApiKeyEnvironmentVariable { get; init; }
    
    /// <summary>
    /// Provider compatibility type for API format
    /// </summary>
    public ProviderCompatibility Compatibility { get; init; } = ProviderCompatibility.OpenAI;
    
    /// <summary>
    /// Optional custom headers to include in requests
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }
    
    /// <summary>
    /// Request timeout for this provider
    /// </summary>
    public TimeSpan? Timeout { get; init; }
    
    /// <summary>
    /// Provider name for identification and logging
    /// </summary>
    public string? ProviderName { get; init; }
    
    /// <summary>
    /// Maximum retry attempts for this provider
    /// </summary>
    public int MaxRetries { get; init; } = 3;
    
    /// <summary>
    /// Validate the connection configuration
    /// </summary>
    public ValidationResult Validate()
    {
        var result = new ValidationResult();
        
        if (string.IsNullOrWhiteSpace(EndpointUrl))
            result.AddError("EndpointUrl is required");
        else if (!Uri.TryCreate(EndpointUrl, UriKind.Absolute, out _))
            result.AddError("EndpointUrl must be a valid absolute URL");
            
        if (string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable))
            result.AddError("ApiKeyEnvironmentVariable is required");
            
        if (Timeout.HasValue && Timeout.Value <= TimeSpan.Zero)
            result.AddError("Timeout must be positive");
            
        if (MaxRetries < 0)
            result.AddError("MaxRetries cannot be negative");
            
        return result;
    }
    
    /// <summary>
    /// Get the API key from the environment variable
    /// </summary>
    public string? GetApiKey()
    {
        return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
    }
}

/// <summary>
/// Provider API compatibility types
/// </summary>
public enum ProviderCompatibility
{
    /// <summary>
    /// OpenAI-compatible API format (most common)
    /// </summary>
    OpenAI,
    
    /// <summary>
    /// Anthropic-specific API format
    /// </summary>
    Anthropic,
    
    /// <summary>
    /// Custom provider format requiring special handling
    /// </summary>
    Custom
}

/// <summary>
/// Predefined provider connection configurations for common providers
/// </summary>
public static class WellKnownProviders
{
    public static readonly ProviderConnectionConfig OpenAI = new()
    {
        EndpointUrl = "https://api.openai.com/v1",
        ApiKeyEnvironmentVariable = "OPENAI_API_KEY",
        Compatibility = ProviderCompatibility.OpenAI,
        ProviderName = "OpenAI"
    };
    
    public static readonly ProviderConnectionConfig Anthropic = new()
    {
        EndpointUrl = "https://api.anthropic.com",
        ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY",
        Compatibility = ProviderCompatibility.Anthropic,
        ProviderName = "Anthropic"
    };
    
    public static readonly ProviderConnectionConfig OpenRouter = new()
    {
        EndpointUrl = "https://openrouter.ai/api/v1",
        ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
        Compatibility = ProviderCompatibility.OpenAI,
        ProviderName = "OpenRouter",
        Headers = new Dictionary<string, string>
        {
            ["HTTP-Referer"] = "https://github.com/your-org/your-app",
            ["X-Title"] = "LMConfig Application"
        }
    };
    
    public static readonly ProviderConnectionConfig DeepInfra = new()
    {
        EndpointUrl = "https://api.deepinfra.com/v1/openai",
        ApiKeyEnvironmentVariable = "DEEPINFRA_API_KEY",
        Compatibility = ProviderCompatibility.OpenAI,
        ProviderName = "DeepInfra"
    };
    
    public static readonly ProviderConnectionConfig Groq = new()
    {
        EndpointUrl = "https://api.groq.com/openai/v1",
        ApiKeyEnvironmentVariable = "GROQ_API_KEY",
        Compatibility = ProviderCompatibility.OpenAI,
        ProviderName = "Groq"
    };
    
    public static readonly ProviderConnectionConfig Cerebras = new()
    {
        EndpointUrl = "https://api.cerebras.ai/v1",
        ApiKeyEnvironmentVariable = "CEREBRAS_API_KEY",
        Compatibility = ProviderCompatibility.OpenAI,
        ProviderName = "Cerebras"
    };
    
    public static readonly ProviderConnectionConfig GoogleGemini = new()
    {
        EndpointUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
        ApiKeyEnvironmentVariable = "GEMINI_API_KEY",
        Compatibility = ProviderCompatibility.OpenAI,
        ProviderName = "GoogleGemini"
    };
}
```

#### Enhanced Fluent Configuration Builder
```csharp
public class LMConfigBuilder
{
    private LMConfig _config = new();
    
    // Basic configuration
    public LMConfigBuilder WithModel(string modelId)
    {
        _config.ModelId = modelId;
        return this;
    }
    
    public LMConfigBuilder WithTemperature(float temperature)
    {
        _config.Temperature = temperature;
        return this;
    }
    
    public LMConfigBuilder WithMaxTokens(int maxTokens)
    {
        _config.MaxTokens = maxTokens;
        return this;
    }
    
    // Provider selection strategies
    public LMConfigBuilder PreferEconomic()
    {
        _config.SelectionStrategy = ProviderSelectionStrategy.Economic;
        _config.PreferredTags = (_config.PreferredTags ?? new List<string>()).Concat([ProviderTags.Economic]).ToList();
        return this;
    }
    
    public LMConfigBuilder PreferFast()
    {
        _config.SelectionStrategy = ProviderSelectionStrategy.Fast;
        _config.PreferredTags = (_config.PreferredTags ?? new List<string>()).Concat([ProviderTags.Fast]).ToList();
        return this;
    }
    
    public LMConfigBuilder PreferHighQuality()
    {
        _config.SelectionStrategy = ProviderSelectionStrategy.HighQuality;
        _config.PreferredTags = (_config.PreferredTags ?? new List<string>()).Concat([ProviderTags.HighQuality]).ToList();
        return this;
    }
    
    public LMConfigBuilder PreferBalanced()
    {
        _config.SelectionStrategy = ProviderSelectionStrategy.Balanced;
        return this;
    }
    
    // Tag-based filtering
    public LMConfigBuilder RequireTags(params string[] tags)
    {
        _config.RequiredTags = tags.ToList();
        return this;
    }
    
    public LMConfigBuilder PreferTags(params string[] tags)
    {
        _config.PreferredTags = tags.ToList();
        return this;
    }
    
    // Cost management
    public LMConfigBuilder WithBudgetLimit(decimal maxCostPerRequest)
    {
        _config.BudgetLimit = maxCostPerRequest;
        _config.EnableCostEstimation = true;
        return this;
    }
    
    public LMConfigBuilder WithCostEstimation(bool enabled = true)
    {
        _config.EnableCostEstimation = enabled;
        return this;
    }
    
    public LMConfigBuilder WithCostTracking(bool enabled = true)
    {
        _config.EnableCostTracking = enabled;
        return this;
    }
    
    // Performance constraints
    public LMConfigBuilder WithMaxLatency(TimeSpan maxLatency)
    {
        _config.MaxLatency = maxLatency;
        return this;
    }
    
    // Provider connection configuration (NEW)
    public LMConfigBuilder WithProviderConnection(ProviderConnectionConfig connection)
    {
        _config.ProviderConnection = connection;
        return this;
    }
    
    public LMConfigBuilder WithProviderConnection(string endpointUrl, string apiKeyEnvVar, ProviderCompatibility compatibility = ProviderCompatibility.OpenAI)
    {
        _config.ProviderConnection = new ProviderConnectionConfig
        {
            EndpointUrl = endpointUrl,
            ApiKeyEnvironmentVariable = apiKeyEnvVar,
            Compatibility = compatibility
        };
        return this;
    }
    
    public LMConfigBuilder WithWellKnownProvider(ProviderConnectionConfig providerConfig)
    {
        _config.ProviderConnection = providerConfig;
        return this;
    }
    
    public LMConfigBuilder WithFallbackConnections(params ProviderConnectionConfig[] connections)
    {
        _config.FallbackConnections = connections.ToList();
        return this;
    }
    
    // Provider registry methods (NEW) - reference providers by name
    public LMConfigBuilder WithProvider(string providerName)
    {
        _config.PreferredProviderName = providerName;
        return this;
    }
    
    public LMConfigBuilder WithProviderFallbacks(params string[] providerNames)
    {
        _config.FallbackProviderNames = providerNames.ToList();
        return this;
    }
    
    public LMConfigBuilder WithProvider(string primaryProvider, params string[] fallbackProviders)
    {
        _config.PreferredProviderName = primaryProvider;
        _config.FallbackProviderNames = fallbackProviders.ToList();
        return this;
    }
    
    public LMConfigBuilder WithOpenRouterConnection(string? httpReferer = null, string? xTitle = null)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(httpReferer))
            headers["HTTP-Referer"] = httpReferer;
        if (!string.IsNullOrEmpty(xTitle))
            headers["X-Title"] = xTitle;
            
        _config.ProviderConnection = new ProviderConnectionConfig
        {
            EndpointUrl = WellKnownProviders.OpenRouter.EndpointUrl,
            ApiKeyEnvironmentVariable = WellKnownProviders.OpenRouter.ApiKeyEnvironmentVariable,
            Compatibility = WellKnownProviders.OpenRouter.Compatibility,
            ProviderName = WellKnownProviders.OpenRouter.ProviderName,
            Headers = headers.Any() ? headers : null
        };
        return this;
    }
    
    // Provider-specific configuration
    public LMConfigBuilder WithOpenRouter(Action<OpenRouterFeatureConfigBuilder> configure)
    {
        var builder = new OpenRouterFeatureConfigBuilder();
        configure(builder);
        _config.ProviderFeatures = builder.Build();
        return this;
    }
    
    public LMConfigBuilder WithAnthropic(Action<AnthropicFeatureConfigBuilder> configure)
    {
        var builder = new AnthropicFeatureConfigBuilder();
        configure(builder);
        _config.ProviderFeatures = builder.Build();
        return this;
    }
    
    public LMConfigBuilder WithOpenAI(Action<OpenAIFeatureConfigBuilder> configure)
    {
        var builder = new OpenAIFeatureConfigBuilder();
        configure(builder);
        _config.ProviderFeatures = builder.Build();
        return this;
    }
    
    public LMConfig Build()
    {
        _config.Validate();
        return _config;
    }
}
```

#### Enhanced ModelConfigurationService
```csharp
public interface IModelConfigurationService
{
    // Existing methods
    ProviderConfig SelectProviderForModel(string modelId, IReadOnlyList<string>? requiredTags = null);
    
    // New cost-aware methods
    Task<CostEstimation> EstimateCostAsync(LMConfig config, int estimatedPromptTokens, int estimatedCompletionTokens = 0);
    Task<CostEstimation> EstimateCostAsync(string modelId, int estimatedPromptTokens, int estimatedCompletionTokens = 0, ProviderSelectionStrategy strategy = ProviderSelectionStrategy.Priority);
    
    // New tag-aware selection
    ProviderConfig SelectProviderForModel(LMConfig config);
    IReadOnlyList<ProviderConfig> GetProvidersWithTags(string modelId, IReadOnlyList<string> tags);
    IReadOnlyList<ProviderConfig> GetProvidersByStrategy(string modelId, ProviderSelectionStrategy strategy);
    
    // Cost reporting
    Task<CostReport> TrackCostAsync(string provider, string model, int promptTokens, int completionTokens);
    Task<IReadOnlyList<CostReport>> GetCostHistoryAsync(DateTime from, DateTime to);
}
```

## Enhanced Usage Examples with Provider Registry

### Using Provider Registry (Recommended Approach)

```csharp
// Simple provider selection by name - connection details resolved from registry
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider("OpenAI")                    // References ProviderRegistry["OpenAI"]
    .PreferHighQuality()
    .Build();

var response = await agent.GenerateReplyAsync(messages, config);
```

### Free Models with Provider Registry

```csharp
// Use OpenRouter for free models - connection details from registry
var freeConfig = new LMConfigBuilder()
    .WithModel("deepseek-r1-free")
    .WithProvider("OpenRouter")                // Uses registry configuration for OpenRouter
    .RequireTags("free")
    .Build();

var response = await agent.GenerateReplyAsync(messages, freeConfig);
```

### Provider Fallback Chain with Registry

```csharp
// Primary provider with automatic fallbacks using registry
var robustConfig = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider("OpenAI", "OpenRouter", "DeepInfra")  // Primary + fallbacks from registry
    .WithBudgetLimit(0.50m)
    .Build();

// System will try: OpenAI -> OpenRouter -> DeepInfra (all connection details from registry)
var response = await agent.GenerateReplyAsync(messages, robustConfig);
```

### Ultra-Fast Provider Selection

```csharp
// Use Groq for ultra-fast inference - connection details from registry
var fastConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .WithProvider("Groq")                      // Registry resolves to https://api.groq.com/openai/v1
    .PreferFast()
    .WithMaxLatency(TimeSpan.FromSeconds(3))
    .Build();

var response = await agent.GenerateReplyAsync(messages, fastConfig);
```

### Economic Provider Selection

```csharp
// Use DeepInfra for cost-effective requests - connection details from registry
var economicConfig = new LMConfigBuilder()
    .WithModel("gpt-4o-mini")
    .WithProvider("DeepInfra")                 // Registry resolves connection details
    .PreferEconomic()
    .WithBudgetLimit(0.10m)
    .Build();

var response = await agent.GenerateReplyAsync(messages, economicConfig);
```

### Environment-Specific Configuration

```csharp
// Different providers for different environments
var providerName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") switch
{
    "Development" => "OpenRouter",  // Free models for dev
    "Testing" => "DeepInfra",       // Economic for testing
    "Production" => "OpenAI",       // High-quality for prod
    _ => "OpenRouter"
};

var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider(providerName)               // Provider resolved from registry
    .Build();
```

### Custom Provider Override (when needed)

```csharp
// Override registry with custom connection for special cases
var customConfig = new LMConfigBuilder()
    .WithModel("custom-model")
    .WithProviderConnection(
        endpointUrl: "https://api.internal-company.com/llm/v1",
        apiKeyEnvVar: "INTERNAL_LLM_API_KEY")
    .Build();

// This bypasses the registry for custom endpoints
var response = await agent.GenerateReplyAsync(messages, customConfig);
```

### Validating Provider Registry Setup

```csharp
// Check that all required environment variables are configured
public async Task<bool> ValidateProviderSetup(IProviderRegistry registry)
{
    var validation = registry.ValidateEnvironmentVariables();
    
    if (!validation.IsValid)
    {
        foreach (var error in validation.Errors)
        {
            Console.WriteLine($"❌ {error}");
        }
        return false;
    }
    
    foreach (var warning in validation.Warnings)
    {
        Console.WriteLine($"⚠️ {warning}");
    }
    
    var providers = registry.GetRegisteredProviders();
    Console.WriteLine($"✅ Provider registry configured with: {string.Join(", ", providers)}");
    
    return true;
}
```

## Provider Registry Configuration Guide

### Overview

The Provider Registry is an infrastructure-level configuration system that maps provider names to their connection details. This allows you to:

- **Centralize provider configuration** in `appsettings.json`
- **Reference providers by name** in your code
- **Manage API keys via environment variables**
- **Configure provider-specific settings** (timeouts, headers, etc.)
- **Easily switch between providers** without code changes

### Configuration Structure

#### appsettings.json Structure
```json
{
  "AppConfig": {
    "Models": [
      // Model configurations with provider references
    ]
  },
  "ProviderRegistry": {
    "OpenAI": {
      "EndpointUrl": "https://api.openai.com/v1",
      "ApiKeyEnvironmentVariable": "OPENAI_API_KEY",
      "Compatibility": "OpenAI",
      "Timeout": "00:01:00",
      "MaxRetries": 3,
      "Description": "Official OpenAI API endpoint"
    },
    "OpenRouter": {
      "EndpointUrl": "https://openrouter.ai/api/v1",
      "ApiKeyEnvironmentVariable": "OPENROUTER_API_KEY",
      "Compatibility": "OpenAI",
      "Headers": {
        "HTTP-Referer": "https://github.com/your-org/lm-dotnet-tools",
        "X-Title": "LMConfig Application"
      },
      "Timeout": "00:02:00",
      "MaxRetries": 3,
      "Description": "OpenRouter aggregator with multiple provider fallback"
    }
  }
}
```

#### Environment Variables (.env file)
```bash
# Provider API Keys
OPENAI_API_KEY=sk-your-openai-key-here
ANTHROPIC_API_KEY=sk-ant-your-anthropic-key-here
OPENROUTER_API_KEY=sk-or-your-openrouter-key-here
DEEPINFRA_API_KEY=your-deepinfra-key-here
GROQ_API_KEY=gsk_your-groq-key-here
CEREBRAS_API_KEY=your-cerebras-key-here
GEMINI_API_KEY=your-gemini-key-here
```

### Usage Patterns

#### 1. Simple Provider Selection by Name
```csharp
// Use OpenAI - connection details resolved from registry
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider("OpenAI")
    .Build();

var response = await agent.GenerateReplyAsync(messages, config);
```

#### 2. Free Provider Selection with Tags
```csharp
// Use the same model but select free provider
var freeConfig = new LMConfigBuilder()
    .WithModel("deepseek-r1-distill-llama-70b")  // Same model ID
    .RequireTags("free")                         // Select free provider
    .Build();

// This will automatically select the OpenRouter free provider for this model
var response = await agent.GenerateReplyAsync(messages, freeConfig);
```

#### 2a. Alternative Free Models for Different Use Cases
```csharp
// High-performance free reasoning model
var reasoningConfig = new LMConfigBuilder()
    .WithModel("deepseek-r1-distill-llama-70b")
    .RequireTags("free", "reasoning")
    .Build();

// Multimodal free model for image processing  
var multimodalConfig = new LMConfigBuilder()
    .WithModel("gpt-4o")                        // Same model ID as paid version
    .RequireTags("free", "multimodal")          // Select free multimodal provider
    .Build();

// Ultra-fast free model for simple tasks
var fastConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .RequireTags("free", "ultra-fast")
    .Build();

// All of these use the same model IDs but select different providers based on tags
```

#### 3. Provider Fallback Chain
```csharp
// Primary provider with automatic fallbacks
var robustConfig = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider("OpenAI", "OpenRouter", "DeepInfra")  // Fallback chain
    .Build();

// System tries: OpenAI → OpenRouter → DeepInfra (all from registry)
var response = await agent.GenerateReplyAsync(messages, robustConfig);
```

#### 4. Environment-Specific Provider Selection
```csharp
// Different providers for different environments
var providerName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") switch
{
    "Development" => "OpenRouter",  // Free models for development
    "Testing" => "DeepInfra",       // Economic for testing
    "Production" => "OpenAI",       // High-quality for production
    _ => "OpenRouter"
};

var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider(providerName)     // Provider resolved from registry
    .Build();
```

#### 5. Tag-Based Provider Selection
```csharp
// Select provider based on requirements, not specific provider name
var economicConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .RequireTags("free")            // Must be free
    .PreferTags("ultra-fast")       // Prefer fast if available
    .Build();

// This will select the best free provider for this model
var response = await agent.GenerateReplyAsync(messages, economicConfig);
```

### Provider Registry Validation

#### Startup Validation
```csharp
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Register provider registry
        services.Configure<ProviderRegistryConfig>(Configuration.GetSection("ProviderRegistry"));
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        
        // Validate on startup
        var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IProviderRegistry>();
        
        var validation = registry.ValidateEnvironmentVariables();
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                Console.WriteLine($"❌ {error}");
            }
            throw new InvalidOperationException("Provider registry validation failed");
        }
        
        foreach (var warning in validation.Warnings)
        {
            Console.WriteLine($"⚠️ {warning}");
        }
        
        var providers = registry.GetRegisteredProviders();
        Console.WriteLine($"✅ Provider registry configured with: {string.Join(", ", providers)}");
    }
}
```

#### Runtime Validation
```csharp
public class ProviderHealthService
{
    private readonly IProviderRegistry _registry;
    
    public ProviderHealthService(IProviderRegistry registry)
    {
        _registry = registry;
    }
    
    public async Task<Dictionary<string, bool>> CheckProviderHealth()
    {
        var results = new Dictionary<string, bool>();
        var providers = _registry.GetRegisteredProviders();
        
        foreach (var providerName in providers)
        {
            var connectionInfo = _registry.GetProviderConnection(providerName);
            if (connectionInfo == null)
            {
                results[providerName] = false;
                continue;
            }
            
            // Check if API key is available
            var apiKey = connectionInfo.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                results[providerName] = false;
                continue;
            }
            
            // Optionally: Make a test API call to verify connectivity
            results[providerName] = true;
        }
        
        return results;
    }
}
```

### Advanced Configuration Scenarios

#### Custom Provider with Special Headers
```csharp
// Add custom provider to registry (via configuration)
{
  "ProviderRegistry": {
    "CompanyInternal": {
      "EndpointUrl": "https://api.company.com/llm/v1",
      "ApiKeyEnvironmentVariable": "COMPANY_LLM_API_KEY",
      "Compatibility": "OpenAI",
      "Headers": {
        "X-Company-App": "LMConfig Integration",
        "X-Version": "1.0.0",
        "X-Department": "Engineering"
      },
      "Timeout": "00:02:00",
      "MaxRetries": 5,
      "Description": "Company internal LLM service"
    }
  }
}

// Use in code
var config = new LMConfigBuilder()
    .WithModel("company-gpt-4")
    .WithProvider("CompanyInternal")
    .Build();
```

#### Provider-Specific Timeouts and Retries
```csharp
// Different providers with different performance characteristics
{
  "ProviderRegistry": {
    "Groq": {
      "EndpointUrl": "https://api.groq.com/openai/v1",
      "ApiKeyEnvironmentVariable": "GROQ_API_KEY",
      "Compatibility": "OpenAI",
      "Timeout": "00:00:30",    // Fast timeout for ultra-fast provider
      "MaxRetries": 2,          // Fewer retries since it's fast
      "Description": "Groq ultra-fast inference platform"
    },
    "OpenRouter": {
      "EndpointUrl": "https://openrouter.ai/api/v1",
      "ApiKeyEnvironmentVariable": "OPENROUTER_API_KEY",
      "Compatibility": "OpenAI",
      "Timeout": "00:02:00",    // Longer timeout for aggregator
      "MaxRetries": 3,          // More retries for reliability
      "Description": "OpenRouter aggregator with multiple provider fallback"
    }
  }
}
```

### Migration from Hardcoded Providers

#### Before (Hardcoded)
```csharp
// Old approach - hardcoded connection details
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProviderConnection(
        endpointUrl: "https://api.openai.com/v1",
        apiKeyEnvVar: "OPENAI_API_KEY")
    .Build();
```

#### After (Registry-Based)
```csharp
// New approach - provider by name from registry
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider("OpenAI")     // Connection details from registry
    .Build();
```

#### Hybrid Approach (Override when needed)
```csharp
// Use registry by default, override for special cases
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithProvider("OpenAI")     // Default from registry
    .WithProviderConnection(    // Override for this request
        endpointUrl: "https://api.special-endpoint.com/v1",
        apiKeyEnvVar: "SPECIAL_API_KEY")
    .Build();
```

### Best Practices

#### 1. Environment Variable Naming
- Use consistent naming: `{PROVIDER_NAME}_API_KEY`
- Use uppercase for environment variables
- Include provider name for clarity

#### 2. Provider Configuration
- Set appropriate timeouts based on provider characteristics
- Configure retry counts based on provider reliability
- Include descriptive names for documentation

#### 3. Security
- Never store API keys in configuration files
- Use environment variables or secure key management
- Validate environment variables on startup

#### 4. Monitoring
- Log provider selection decisions
- Monitor provider health and availability
- Track provider performance metrics

#### 5. Testing
- Use free providers for development and testing
- Validate provider configurations in CI/CD
- Test provider fallback scenarios

This Provider Registry system provides a clean separation between infrastructure concerns (provider connections) and application concerns (model selection and configuration), making your LLM applications more maintainable and flexible.

## Enhanced Usage Examples with Provider Connection Configuration

### Using Well-Known Provider Connections

```csharp
// Use OpenRouter with free models
var freeModelConfig = new LMConfigBuilder()
    .WithModel("deepseek-r1-free")
    .WithWellKnownProvider(WellKnownProviders.OpenRouter)
    .WithOpenRouterConnection(
        httpReferer: "https://myapp.example.com",
        xTitle: "My LLM Application")
    .RequireTags("free")
    .Build();

var response = await agent.GenerateReplyAsync(messages, freeModelConfig);
```

### Custom Provider Connection

```csharp
// Configure a custom provider endpoint
var customConfig = new LMConfigBuilder()
    .WithModel("custom-model")
    .WithProviderConnection(
        endpointUrl: "https://api.custom-provider.com/v1",
        apiKeyEnvVar: "CUSTOM_PROVIDER_API_KEY",
        compatibility: ProviderCompatibility.OpenAI)
    .Build();

var response = await agent.GenerateReplyAsync(messages, customConfig);
```

### Multiple Provider Fallback Configuration

```csharp
// Configure primary provider with automatic fallbacks
var fallbackConfig = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithWellKnownProvider(WellKnownProviders.OpenAI)  // Primary
    .WithFallbackConnections(
        WellKnownProviders.OpenRouter,                  // First fallback
        WellKnownProviders.DeepInfra)                   // Second fallback
    .WithCostEstimation(true)
    .Build();

// The system will automatically try fallbacks if primary provider fails
var response = await agent.GenerateReplyAsync(messages, fallbackConfig);
```

### Free Models with Provider Selection

```csharp
// Use free models for development and testing
var developmentConfig = new LMConfigBuilder()
    .WithModel("llama-4-maverick-free")
    .WithWellKnownProvider(WellKnownProviders.OpenRouter)
    .RequireTags("free", "multimodal")
    .WithMaxLatency(TimeSpan.FromSeconds(10))
    .Build();

// Cost tracking will show $0.00 for free models
var response = await agent.GenerateReplyAsync(messages, developmentConfig);
```

### OpenRouter Free Models for Different Use Cases

```csharp
// High-performance free model for coding
var codingConfig = new LMConfigBuilder()
    .WithModel("llama-4-scout-free")
    .WithWellKnownProvider(WellKnownProviders.OpenRouter)
    .RequireTags("free", "long-context")
    .WithFunctions(codeAnalysisFunction)
    .Build();

// Lightweight free model for simple tasks
var lightweightConfig = new LMConfigBuilder()
    .WithModel("qwen3-0.6b-free")
    .WithWellKnownProvider(WellKnownProviders.OpenRouter)
    .RequireTags("free", "ultra-fast")
    .WithBudgetLimit(0.001m)  // Will enforce free models
    .Build();

// Reasoning free model for complex problems
var reasoningConfig = new LMConfigBuilder()
    .WithModel("deepseek-r1-free")
    .WithWellKnownProvider(WellKnownProviders.OpenRouter)
    .RequireTags("free", "reasoning")
    .WithThinking(budgetTokens: 2048)
    .Build();
```

### Environment-Specific Provider Configuration

```csharp
// Development environment - use free models
var devConfig = new LMConfigBuilder()
    .WithModel("gemini-2.5-pro-exp-free")
    .WithWellKnownProvider(WellKnownProviders.OpenRouter)
    .RequireTags("free", "experimental")
    .WithCostTracking(true)
    .Build();

// Production environment - use paid, reliable providers
var prodConfig = new LMConfigBuilder()
    .WithModel("gpt-4o")
    .WithWellKnownProvider(WellKnownProviders.OpenAI)
    .WithFallbackConnections(WellKnownProviders.OpenRouter)
    .PreferHighQuality()
    .WithBudgetLimit(0.50m)
    .Build();

// Select configuration based on environment
var config = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development" 
    ? devConfig 
    : prodConfig;
```

### Ultra-Fast Provider Configuration

```csharp
// Configure Groq for ultra-fast inference
var groqConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .WithWellKnownProvider(WellKnownProviders.Groq)
    .PreferFast()
    .WithMaxLatency(TimeSpan.FromSeconds(2))
    .Build();

// Configure Cerebras for high-performance tasks
var cerebrasConfig = new LMConfigBuilder()
    .WithModel("llama-4-scout-17b-16e-instruct")
    .WithWellKnownProvider(WellKnownProviders.Cerebras)
    .RequireTags("ultra-fast", "high-performance")
    .Build();
```

### Provider Connection with Custom Headers and Timeout

```csharp
// Custom provider with specific headers and timeout
var customProviderConfig = new LMConfigBuilder()
    .WithModel("custom-model")
    .WithProviderConnection(new ProviderConnectionConfig
    {
        EndpointUrl = "https://api.mycompany.com/llm/v1",
        ApiKeyEnvironmentVariable = "COMPANY_LLM_API_KEY",
        Compatibility = ProviderCompatibility.OpenAI,
        ProviderName = "Company Internal LLM",
        Headers = new Dictionary<string, string>
        {
            ["X-Company-App"] = "LMConfig Integration",
            ["X-Version"] = "1.0.0"
        },
        Timeout = TimeSpan.FromSeconds(30),
        MaxRetries = 5
    })
    .Build();
```

## Enhanced Usage Examples with Model Capabilities

### Capability-Aware Thinking Configuration

#### Claude (Anthropic Thinking)
```csharp
// Claude supports configurable thinking with budget tokens
var config = new LMConfigBuilder()
    .WithModel("claude-3-sonnet")
    .WithThinking(budgetTokens: 2048, type: "enabled")  // Uses Anthropic thinking API
    .Build();

// The system automatically adapts to Claude's thinking capability:
// - Uses "thinking" parameter in API call
// - Includes budget_tokens: 2048
// - Sets thinking type to "enabled"
```

#### DeepSeek Thinking Model
```csharp
// DeepSeek has built-in reasoning, thinking config is adapted automatically
var config = new LMConfigBuilder()
    .WithModel("deepseek-r1")
    .WithThinking(budgetTokens: 2048)  // Budget tokens ignored for DeepSeek
    .Build();

// The system adapts for DeepSeek:
// - No special thinking parameters needed
// - Reasoning is built into the model
// - Budget tokens are ignored
// - Thinking content may be included in response
```

#### OpenAI O1 (Built-in Reasoning)
```csharp
// O1 has built-in reasoning that cannot be configured
var config = new LMConfigBuilder()
    .WithModel("o1-preview")
    .WithThinking(budgetTokens: 2048)  // Configuration ignored for O1
    .Build();

// The system adapts for O1:
// - No thinking parameters sent to API
// - Reasoning is always enabled and built-in
// - No thinking content exposed in response
// - Higher cost automatically factored in
```

#### Regular GPT-4 (No Thinking)
```csharp
// Regular GPT-4 doesn't support thinking
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithThinking(budgetTokens: 2048)  // Will cause validation error or be ignored
    .Build();

// Options:
// 1. Validation error: "Model gpt-4 does not support thinking capabilities"
// 2. Silent adaptation: thinking config is ignored
// 3. Automatic fallback: suggest o1-preview or claude-3-sonnet
```

### Multimodal Capability Examples

#### GPT-4 Vision
```csharp
var config = new LMConfigBuilder()
    .WithModel("gpt-4-vision-preview")
    .WithImages(enabled: true, quality: "high")
    .RequireCapabilities("multimodal")
    .Build();

// Validates that gpt-4-vision supports images
// Configures image processing with high quality
```

#### Claude 3 with Images
```csharp
var config = new LMConfigBuilder()
    .WithModel("claude-3-sonnet")
    .WithImages(enabled: true)
    .WithThinking(budgetTokens: 1024)  // Can combine thinking + images
    .Build();

// Claude 3 supports both images and thinking simultaneously
```

#### Audio-Capable Model
```csharp
var config = new LMConfigBuilder()
    .WithModel("gpt-4-audio")
    .WithAudio(enabled: true)
    .RequireCapabilities("multimodal", "audio")
    .Build();

// Only selects models that support audio processing
```

### Capability-Based Model Selection

#### Select Best Model for Thinking Tasks
```csharp
var config = new LMConfigBuilder()
    .RequireCapabilities("thinking")
    .PreferHighQuality()
    .WithThinking(budgetTokens: 4096)
    .Build();

// Will select from: claude-3-sonnet, o1-preview, deepseek-r1
// Prefers highest quality thinking model available
```

#### Select Economic Model with Basic Capabilities
```csharp
var config = new LMConfigBuilder()
    .RequireCapabilities("function-calling")
    .PreferEconomic()
    .WithFunctions(weatherFunction, calculatorFunction)
    .Build();

// Selects cheapest model that supports function calling
```

#### Select Fast Model for Real-time Applications
```csharp
var config = new LMConfigBuilder()
    .PreferFast()
    .WithMaxLatency(TimeSpan.FromSeconds(3))
    .RequireCapabilities("streaming")
    .Build();

// Selects fastest model with streaming support
```

### Automatic Capability Adaptation

#### Cross-Model Thinking Configuration
```csharp
// Same configuration works across different thinking models
var thinkingConfig = new LMConfigBuilder()
    .WithThinking(budgetTokens: 2048)
    .PreferBalanced();

// For Claude
var claudeConfig = thinkingConfig.WithModel("claude-3-sonnet").Build();
// Result: Uses Anthropic thinking API with budget tokens

// For O1  
var o1Config = thinkingConfig.WithModel("o1-preview").Build();
// Result: Ignores budget tokens, uses built-in reasoning

// For DeepSeek
var deepseekConfig = thinkingConfig.WithModel("deepseek-r1").Build();
// Result: Uses DeepSeek's built-in reasoning approach
```

#### Capability Validation and Suggestions
```csharp
try 
{
    var config = new LMConfigBuilder()
        .WithModel("gpt-3.5-turbo")
        .WithThinking(budgetTokens: 2048)  // Not supported
        .WithImages(enabled: true)         // Not supported
        .Build();
}
catch (InvalidOperationException ex)
{
    // "Configuration incompatible with model capabilities: 
    //  - Model does not support thinking
    //  - Model does not support multimodal input
    //  Suggested alternatives: claude-3-sonnet, gpt-4-vision-preview"
}
```

## Enhanced appsettings.json Configuration with Model Capabilities

```json
{
  "AppConfig": {
    "Models": [
      {
        "Id": "claude-3-sonnet",
        "IsReasoning": true,
        "Capabilities": {
          "Thinking": {
            "Type": "Anthropic",
            "SupportsBudgetTokens": true,
            "SupportsThinkingType": true,
            "MaxThinkingTokens": 8192,
            "IsBuiltIn": false,
            "IsExposed": true,
            "ParameterName": "thinking"
          },
          "Multimodal": {
            "SupportsImages": true,
            "SupportsAudio": false,
            "SupportsVideo": false,
            "SupportedImageFormats": ["jpeg", "png", "webp", "gif"],
            "MaxImageSize": 5242880,
            "MaxImagesPerMessage": 20
          },
          "FunctionCalling": {
            "SupportsTools": true,
            "SupportsParallelCalls": false,
            "SupportsToolChoice": true,
            "MaxToolsPerRequest": 64,
            "SupportedToolTypes": ["function"]
          },
          "TokenLimits": {
            "MaxContextTokens": 200000,
            "MaxOutputTokens": 8192,
            "RecommendedMaxPromptTokens": 190000
          },
          "ResponseFormats": {
            "SupportsJsonMode": false,
            "SupportsStructuredOutput": false,
            "SupportsJsonSchema": false
          },
          "SupportsStreaming": true,
          "SupportedFeatures": ["thinking", "multimodal", "function-calling", "long-context"],
          "Performance": {
            "TypicalLatency": "00:00:03",
            "MaxLatency": "00:00:15",
            "TokensPerSecond": 50.0,
            "QualityTier": "high"
          }
        },
        "Providers": [
          {
            "Name": "Anthropic",
            "ModelName": "claude-3-sonnet-20240229",
            "Priority": 3,
            "Pricing": {
              "PromptPerMillion": 15.0,
              "CompletionPerMillion": 75.0
            },
            "Tags": ["high-quality", "reasoning", "fast", "reliable", "multimodal"]
          }
        ]
      },
      {
        "Id": "o1-preview",
        "IsReasoning": true,
        "Capabilities": {
          "Thinking": {
            "Type": "OpenAI",
            "SupportsBudgetTokens": false,
            "SupportsThinkingType": false,
            "MaxThinkingTokens": null,
            "IsBuiltIn": true,
            "IsExposed": false,
            "ParameterName": null
          },
          "Multimodal": null,
          "FunctionCalling": {
            "SupportsTools": false,
            "SupportsParallelCalls": false,
            "SupportsToolChoice": false
          },
          "TokenLimits": {
            "MaxContextTokens": 128000,
            "MaxOutputTokens": 32768,
            "RecommendedMaxPromptTokens": 120000
          },
          "ResponseFormats": {
            "SupportsJsonMode": false,
            "SupportsStructuredOutput": false
          },
          "SupportsStreaming": false,
          "SupportedFeatures": ["reasoning", "complex-problem-solving"],
          "Performance": {
            "TypicalLatency": "00:00:15",
            "MaxLatency": "00:01:00",
            "TokensPerSecond": 20.0,
            "QualityTier": "high"
          }
        },
        "Providers": [
          {
            "Name": "OpenAI",
            "ModelName": "o1-preview",
            "Priority": 3,
            "Pricing": {
              "PromptPerMillion": 150.0,
              "CompletionPerMillion": 600.0
            },
            "Tags": ["high-quality", "reasoning", "expensive", "slow"]
          }
        ]
      },
      {
        "Id": "deepseek-r1",
        "IsReasoning": true,
        "Capabilities": {
          "Thinking": {
            "Type": "DeepSeek",
            "SupportsBudgetTokens": false,
            "SupportsThinkingType": false,
            "MaxThinkingTokens": null,
            "IsBuiltIn": true,
            "IsExposed": true,
            "ParameterName": null
          },
          "Multimodal": null,
          "FunctionCalling": {
            "SupportsTools": true,
            "SupportsParallelCalls": true,
            "SupportsToolChoice": true,
            "MaxToolsPerRequest": 32
          },
          "TokenLimits": {
            "MaxContextTokens": 64000,
            "MaxOutputTokens": 8192,
            "RecommendedMaxPromptTokens": 60000
          },
          "ResponseFormats": {
            "SupportsJsonMode": true,
            "SupportsStructuredOutput": false
          },
          "SupportsStreaming": true,
          "SupportedFeatures": ["reasoning", "function-calling", "json-mode"],
          "Performance": {
            "TypicalLatency": "00:00:05",
            "MaxLatency": "00:00:20",
            "TokensPerSecond": 40.0,
            "QualityTier": "high"
          }
        },
        "Providers": [
          {
            "Name": "DeepSeek",
            "ModelName": "deepseek-r1",
            "Priority": 3,
            "Pricing": {
              "PromptPerMillion": 5.5,
              "CompletionPerMillion": 22.0
            },
            "Tags": ["economic", "reasoning", "fast", "reliable"]
          }
        ]
      },
      {
        "Id": "gpt-4",
        "IsReasoning": false,
        "Capabilities": {
          "Thinking": null,
          "Multimodal": null,
          "FunctionCalling": {
            "SupportsTools": true,
            "SupportsParallelCalls": true,
            "SupportsToolChoice": true,
            "MaxToolsPerRequest": 128,
            "SupportedToolTypes": ["function"]
          },
          "TokenLimits": {
            "MaxContextTokens": 128000,
            "MaxOutputTokens": 4096,
            "RecommendedMaxPromptTokens": 120000
          },
          "ResponseFormats": {
            "SupportsJsonMode": true,
            "SupportsStructuredOutput": true,
            "SupportsJsonSchema": true,
            "SupportedFormats": ["json", "text"]
          },
          "SupportsStreaming": true,
          "SupportedFeatures": ["function-calling", "json-mode", "structured-output"],
          "Performance": {
            "TypicalLatency": "00:00:02",
            "MaxLatency": "00:00:10",
            "TokensPerSecond": 60.0,
            "QualityTier": "high"
          }
        },
        "Providers": [
          {
            "Name": "OpenAI",
            "ModelName": "gpt-4",
            "Priority": 3,
            "Pricing": {
              "PromptPerMillion": 30.0,
              "CompletionPerMillion": 60.0
            },
            "Tags": ["high-quality", "reliable", "function-calling"]
          }
        ]
      },
      {
        "Id": "gpt-4-vision-preview",
        "IsReasoning": false,
        "Capabilities": {
          "Thinking": null,
          "Multimodal": {
            "SupportsImages": true,
            "SupportsAudio": false,
            "SupportsVideo": false,
            "SupportedImageFormats": ["jpeg", "png", "webp", "gif"],
            "MaxImageSize": 20971520,
            "MaxImagesPerMessage": 10
          },
          "FunctionCalling": {
            "SupportsTools": true,
            "SupportsParallelCalls": true,
            "SupportsToolChoice": true,
            "MaxToolsPerRequest": 128
          },
          "TokenLimits": {
            "MaxContextTokens": 128000,
            "MaxOutputTokens": 4096,
            "RecommendedMaxPromptTokens": 120000
          },
          "ResponseFormats": {
            "SupportsJsonMode": true,
            "SupportsStructuredOutput": true
          },
          "SupportsStreaming": true,
          "SupportedFeatures": ["multimodal", "function-calling", "vision"],
          "Performance": {
            "TypicalLatency": "00:00:04",
            "MaxLatency": "00:00:15",
            "TokensPerSecond": 45.0,
            "QualityTier": "high"
          }
        },
        "Providers": [
          {
            "Name": "OpenAI",
            "ModelName": "gpt-4-vision-preview",
            "Priority": 3,
            "Pricing": {
              "PromptPerMillion": 100.0,
              "CompletionPerMillion": 300.0
            },
            "Tags": ["high-quality", "multimodal", "vision", "expensive"]
          }
        ]
      }
    ]
  }
}
```

### Capability-Based Provider Selection Examples

#### Automatic Model Selection Based on Requirements
```csharp
// Select best thinking model within budget
var config = new LMConfigBuilder()
    .RequireCapabilities("thinking")
    .WithBudgetLimit(0.50m)
    .PreferBalanced()
    .WithThinking(budgetTokens: 2048)
    .Build();

// Result: Selects deepseek-r1 (economic + thinking) over o1-preview (expensive)
```

#### Multimodal + Thinking Combination
```csharp
// Find model that supports both images and thinking
var config = new LMConfigBuilder()
    .RequireCapabilities("thinking", "multimodal")
    .WithThinking(budgetTokens: 1024)
    .WithImages(enabled: true)
    .Build();

// Result: Selects claude-3-sonnet (only model with both capabilities)
```

#### Function Calling with Performance Requirements
```csharp
// Fast function calling for real-time applications
var config = new LMConfigBuilder()
    .RequireCapabilities("function-calling")
    .PreferFast()
    .WithMaxLatency(TimeSpan.FromSeconds(5))
    .WithFunctions(realtimeFunction)
    .Build();

// Result: Selects gpt-4 (fast + function calling) over o1-preview (no functions)
```

## Migration Strategy

### Phase 1: Infrastructure Integration
1. Enhance `DynamicProviderAgent` to accept `LMConfig`
2. Add conversion methods between `LMConfig` and `GenerateReplyOptions`
3. Maintain full backward compatibility

### Phase 2: Feature Configuration
1. Implement provider feature configuration classes
2. Add fluent builder API
3. Create comprehensive validation system

### Phase 3: Enhanced Capabilities
1. Add tag-based provider filtering
2. Implement feature-aware provider selection
3. Add advanced configuration scenarios

### Phase 4: Developer Experience
1. Create configuration templates and presets
2. Add IDE support and documentation
3. Implement configuration management tools

## Testing Strategy

### Infrastructure Layer Tests
- Provider selection algorithm validation
- Fallback strategy testing
- Credential validation testing
- Pricing calculation verification

### Configuration Layer Tests
- Type safety validation
- Feature configuration testing
- Builder pattern validation
- Cross-provider compatibility testing

### Integration Tests
- End-to-end configuration scenarios
- Provider switching and fallback testing
- Performance impact assessment
- Backward compatibility validation

## Conclusion

This unified design leverages the strengths of both approaches:
- **Production-ready infrastructure** for operational concerns
- **Type-safe configuration** for developer experience
- **Seamless integration** without breaking existing code
- **Extensible architecture** for future enhancements

The two-layer system provides both the robustness needed for production deployments and the flexibility required for complex LLM application development. By building on the existing infrastructure rather than replacing it, we ensure a smooth migration path while significantly enhancing capabilities. 

## Model Capabilities System

### Model Capabilities Definition
```csharp
public record ModelCapabilities
{
    // Thinking/Reasoning capabilities
    public ThinkingCapability? Thinking { get; init; }
    
    // Multimodal capabilities  
    public MultimodalCapability? Multimodal { get; init; }
    
    // Function calling capabilities
    public FunctionCallingCapability? FunctionCalling { get; init; }
    
    // Context and token limits
    public required TokenLimits TokenLimits { get; init; }
    
    // Response format capabilities
    public ResponseFormatCapability? ResponseFormats { get; init; }
    
    // Streaming capabilities
    public bool SupportsStreaming { get; init; } = true;
    
    // Additional model-specific features
    public IReadOnlyList<string> SupportedFeatures { get; init; } = [];
    
    // Model performance characteristics
    public PerformanceCharacteristics? Performance { get; init; }
}

public record ThinkingCapability
{
    public required ThinkingType Type { get; init; }
    public bool SupportsBudgetTokens { get; init; } = false;
    public bool SupportsThinkingType { get; init; } = false;
    public int? MaxThinkingTokens { get; init; }
    public bool IsBuiltIn { get; init; } = false;              // For models like O1 where thinking is always on
    public bool IsExposed { get; init; } = false;              // Whether thinking content is returned
    public string? ParameterName { get; init; }                // API parameter name (e.g., "thinking")
}

public enum ThinkingType
{
    None,           // No thinking support
    Anthropic,      // Claude-style thinking with budget and type parameters
    DeepSeek,       // DeepSeek-style built-in reasoning
    OpenAI,         // O1-style reasoning (built-in, not configurable)
    Custom          // Provider-specific implementation
}

public record MultimodalCapability
{
    public bool SupportsImages { get; init; } = false;
    public bool SupportsAudio { get; init; } = false;
    public bool SupportsVideo { get; init; } = false;
    public IReadOnlyList<string> SupportedImageFormats { get; init; } = [];
    public IReadOnlyList<string> SupportedAudioFormats { get; init; } = [];
    public long? MaxImageSize { get; init; }                   // Max image size in bytes
    public int? MaxImagesPerMessage { get; init; }
}

public record FunctionCallingCapability
{
    public bool SupportsTools { get; init; } = false;
    public bool SupportsParallelCalls { get; init; } = false;
    public bool SupportsToolChoice { get; init; } = false;
    public int? MaxToolsPerRequest { get; init; }
    public IReadOnlyList<string> SupportedToolTypes { get; init; } = [];
}

public record TokenLimits
{
    public required int MaxContextTokens { get; init; }
    public required int MaxOutputTokens { get; init; }
    public int? RecommendedMaxPromptTokens { get; init; }       // Recommended to leave room for response
}

public record ResponseFormatCapability
{
    public bool SupportsJsonMode { get; init; } = false;
    public bool SupportsStructuredOutput { get; init; } = false;
    public bool SupportsJsonSchema { get; init; } = false;
    public IReadOnlyList<string> SupportedFormats { get; init; } = [];
}

public record PerformanceCharacteristics
{
    public TimeSpan? TypicalLatency { get; init; }             // Typical response time
    public TimeSpan? MaxLatency { get; init; }                 // Maximum expected response time
    public double? TokensPerSecond { get; init; }              // Typical generation speed
    public string? QualityTier { get; init; }                  // "high", "medium", "fast"
}
```

### Capability-Aware Configuration

#### Enhanced LMConfig with Capability Validation
```csharp
public class LMConfig
{
    // ... existing properties ...
    
    // Capability-specific configuration
    public ThinkingConfig? ThinkingConfig { get; set; }
    public MultimodalConfig? MultimodalConfig { get; set; }
    
    // Capability requirements for provider selection
    public List<string>? RequiredCapabilities { get; set; }    // e.g., ["thinking", "multimodal"]
    
    // Validation with capability awareness
    public ValidationResult ValidateWithCapabilities(ModelCapabilities capabilities);
    public void AdaptToCapabilities(ModelCapabilities capabilities);  // Auto-adapt config to model
}

public record ThinkingConfig
{
    public int? BudgetTokens { get; init; }
    public string? Type { get; init; }                          // "enabled", "disabled" for Anthropic
    public bool? EnableThinking { get; init; }                 // Generic enable/disable
}

public record MultimodalConfig
{
    public bool ProcessImages { get; init; } = false;
    public bool ProcessAudio { get; init; } = false;
    public string? ImageQuality { get; init; }                 // "high", "low", "auto"
    public int? MaxImages { get; init; }
}
```

#### Enhanced LMConfigBuilder with Capability Awareness
```csharp
public class LMConfigBuilder
{
    private LMConfig _config = new();
    private ModelCapabilities? _modelCapabilities;
    
    // ... existing methods ...
    
    // Capability-aware thinking configuration
    public LMConfigBuilder WithThinking(ThinkingConfig thinkingConfig)
    {
        _config.ThinkingConfig = thinkingConfig;
        return this;
    }
    
    public LMConfigBuilder WithThinking(int budgetTokens = 2048, string? type = null)
    {
        _config.ThinkingConfig = new ThinkingConfig 
        { 
            BudgetTokens = budgetTokens, 
            Type = type,
            EnableThinking = true
        };
        return this;
    }
    
    // Capability-aware multimodal configuration
    public LMConfigBuilder WithImages(bool enabled = true, string? quality = null)
    {
        _config.MultimodalConfig = new MultimodalConfig 
        { 
            ProcessImages = enabled, 
            ImageQuality = quality 
        };
        _config.RequiredCapabilities = (_config.RequiredCapabilities ?? []).Concat(["multimodal"]).ToList();
        return this;
    }
    
    // Capability requirements
    public LMConfigBuilder RequireCapabilities(params string[] capabilities)
    {
        _config.RequiredCapabilities = capabilities.ToList();
        return this;
    }
    
    // Enhanced build with capability validation
    public LMConfig Build()
    {
        _config.Validate();
        
        // If we have model capabilities, validate and adapt
        if (_modelCapabilities != null)
        {
            var validation = _config.ValidateWithCapabilities(_modelCapabilities);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"Configuration incompatible with model capabilities: {string.Join(", ", validation.Errors)}");
            }
            
            _config.AdaptToCapabilities(_modelCapabilities);
        }
        
        return _config;
    }
    
    // Internal method to set capabilities (called by ModelConfigurationService)
    internal LMConfigBuilder WithCapabilities(ModelCapabilities capabilities)
    {
        _modelCapabilities = capabilities;
        return this;
    }
}
```

#### Enhanced ModelConfigurationService with Capability Support
```csharp
public interface IModelConfigurationService
{
    // ... existing methods ...
    
    // Capability-aware selection
    ProviderConfig SelectProviderForModel(LMConfig config);
    ModelCapabilities GetModelCapabilities(string modelId);
    IReadOnlyList<string> GetModelsWithCapabilities(params string[] requiredCapabilities);
    
    // Capability validation
    ValidationResult ValidateConfigurationCapabilities(LMConfig config, string modelId);
    LMConfig AdaptConfigurationToModel(LMConfig config, string modelId);
    
    // Enhanced cost estimation with capability awareness
    Task<CostEstimation> EstimateCostAsync(LMConfig config, int estimatedPromptTokens, int estimatedCompletionTokens = 0);
}

public class ModelConfigurationService : IModelConfigurationService
{
    public ProviderConfig SelectProviderForModel(LMConfig config)
    {
        // 1. Get models that support required capabilities
        var compatibleModels = GetModelsWithCapabilities(config.RequiredCapabilities?.ToArray() ?? []);
        
        // 2. Filter by model ID if specified
        if (!string.IsNullOrEmpty(config.ModelId))
        {
            compatibleModels = compatibleModels.Where(m => m == config.ModelId).ToList();
        }
        
        // 3. Apply existing selection logic (tags, cost, priority)
        return SelectProviderForModel(compatibleModels.First(), config.RequiredTags);
    }
    
    public LMConfig AdaptConfigurationToModel(LMConfig config, string modelId)
    {
        var capabilities = GetModelCapabilities(modelId);
        config.AdaptToCapabilities(capabilities);
        return config;
    }
}
``` 