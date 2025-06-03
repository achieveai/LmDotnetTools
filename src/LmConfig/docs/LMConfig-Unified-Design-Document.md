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
    
    // Validation and conversion
    public GenerateReplyOptions ToGenerateReplyOptions();
    public void Validate();
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

## Enhanced Usage Examples

### Economic Provider Selection
```csharp
// Prefer the most economical provider for gpt-4
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .PreferEconomic()                    // Prefer providers tagged as "economic"
    .WithBudgetLimit(0.10m)              // Max $0.10 per request
    .WithCostEstimation(true)            // Get cost estimate before execution
    .Build();

// Get cost estimation
var estimation = await modelConfigService.EstimateCostAsync(config, estimatedPromptTokens: 1000);
Console.WriteLine($"Estimated cost: ${estimation.TotalEstimatedCost:F4} using {estimation.SelectedProvider}");

var agent = new EnhancedDynamicProviderAgent(modelConfigService, serviceProvider, logger);
var response = await agent.GenerateReplyAsync(messages, config);
```

### Fast Provider Selection
```csharp
// Prefer the fastest provider for real-time applications
var config = new LMConfigBuilder()
    .WithModel("claude-3-sonnet")
    .PreferFast()                        // Prefer providers tagged as "fast"
    .WithMaxLatency(TimeSpan.FromSeconds(5))  // Max 5 second response time
    .RequireTags(ProviderTags.Reliable)  // Must be reliable
    .Build();

var response = await agent.GenerateReplyAsync(messages, config);
```

### High-Quality Provider Selection
```csharp
// Prefer highest quality for important tasks, cost is secondary
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .PreferHighQuality()                 // Prefer providers tagged as "high-quality"
    .RequireTags(ProviderTags.Reasoning) // Must support reasoning
    .WithCostTracking(true)              // Track actual costs for analysis
    .Build();

var response = await agent.GenerateReplyAsync(messages, config);
```

### Balanced Selection with Budget Constraints
```csharp
// Balance cost and performance with budget constraints
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .PreferBalanced()                    // Balance cost and performance
    .WithBudgetLimit(0.25m)              // Max $0.25 per request
    .PreferTags(ProviderTags.Fast, ProviderTags.Reliable)  // Prefer fast and reliable
    .WithCostEstimation(true)
    .Build();

// Check if request fits budget
var estimation = await modelConfigService.EstimateCostAsync(config, estimatedPromptTokens: 2000);
if (estimation.TotalEstimatedCost <= config.BudgetLimit)
{
    var response = await agent.GenerateReplyAsync(messages, config);
}
else
{
    Console.WriteLine($"Request exceeds budget: ${estimation.TotalEstimatedCost:F4} > ${config.BudgetLimit:F2}");
}
```

### Multi-Modal with Cost Tracking
```csharp
// Multi-modal request with comprehensive cost tracking
var config = new LMConfigBuilder()
    .WithModel("gpt-4-vision")
    .RequireTags(ProviderTags.Multimodal) // Must support images
    .PreferTags(ProviderTags.HighQuality) // Prefer high quality
    .WithCostEstimation(true)
    .WithCostTracking(true)
    .Build();

var response = await agent.GenerateReplyAsync(messages, config);

// Get cost report after execution
var costReport = await modelConfigService.TrackCostAsync(
    response.First().FromAgent, 
    config.ModelId, 
    promptTokens: 1500, 
    completionTokens: 300);
    
Console.WriteLine($"Actual cost: ${costReport.TotalActualCost:F4}");
```

### Provider Selection by Specific Tags
```csharp
// Select provider for coding tasks with specific requirements
var config = new LMConfigBuilder()
    .WithModel("claude-3-sonnet")
    .RequireTags(ProviderTags.Coding, ProviderTags.Fast)  // Must be good for coding and fast
    .PreferTags(ProviderTags.Economic)                    // Prefer economical if available
    .WithBudgetLimit(0.15m)
    .Build();

var response = await agent.GenerateReplyAsync(messages, config);
```

### Cost Analysis and Reporting
```csharp
// Get cost history for analysis
var costHistory = await modelConfigService.GetCostHistoryAsync(
    DateTime.Today.AddDays(-30), 
    DateTime.Today);

var totalCost = costHistory.Sum(c => c.TotalActualCost);
var avgCostPerRequest = costHistory.Average(c => c.TotalActualCost);

Console.WriteLine($"Last 30 days: ${totalCost:F2} total, ${avgCostPerRequest:F4} average per request");

// Group by provider to see cost distribution
var costByProvider = costHistory
    .GroupBy(c => c.Provider)
    .Select(g => new { Provider = g.Key, TotalCost = g.Sum(c => c.TotalActualCost) })
    .OrderByDescending(x => x.TotalCost);

foreach (var item in costByProvider)
{
    Console.WriteLine($"{item.Provider}: ${item.TotalCost:F2}");
}
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