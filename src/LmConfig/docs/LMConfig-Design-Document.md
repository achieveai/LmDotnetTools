# LMConfig Module Design Document

## Executive Summary

The LMConfig module is a unified configuration system designed to abstract and manage the complexities of different Language Model providers (OpenRouter, Anthropic, OpenAI) while providing type-safe, extensible configuration for agent creation. This module will serve as the foundation for an enhanced Agent Factory that can seamlessly work with multiple providers and their unique features.

## API Research Summary

### OpenRouter API Key Features
- **Provider Routing**: `provider` parameter with `order` and `allow_fallbacks` options
- **Model Routing**: `models` array for fallback model selection
- **Non-standard Parameters**: `top_a`, `min_p`, `repetition_penalty` alongside standard ones
- **OpenRouter-specific Features**: `transforms`, `reasoning` configuration
- **Provider Pass-through**: Supports provider-specific parameters (e.g., `safe_prompt` for Mistral)
- **Normalized Responses**: Standardizes responses across different underlying providers

### Anthropic API Key Features
- **System Prompts**: Separate `system` parameter (not in messages array)
- **Extended Thinking**: `thinking` parameter with `budget_tokens` and `type`
- **Required Parameters**: `max_tokens` is mandatory
- **API Versioning**: Requires `anthropic-version` header
- **Multimodal Support**: Image content blocks in messages
- **Tool Configuration**: `tool_choice` with `disable_parallel_tool_use` option

### OpenAI API Key Features
- **Standard Chat Format**: System messages within the messages array
- **Structured Outputs**: `response_format` with JSON schema support
- **Function Calling**: `tools` and `tool_choice` parameters
- **Streaming Options**: `stream_options` with usage inclusion
- **Token Management**: `max_tokens`, `max_completion_tokens` distinction
- **Deterministic Outputs**: `seed` parameter for reproducible results

## Current Architecture Analysis

### Strengths
1. **Clean Abstractions**: `IAgent`, `IMessage`, `IStreamingAgent` provide good separation
2. **Provider Implementations**: Existing `AnthropicAgent` and `OpenClientAgent` work well
3. **Configuration Base**: `GenerateReplyOptions` provides foundation for configuration
4. **Factory Pattern**: Basic factory exists in `ServiceCollectionExtensions.cs`

### Gaps
1. **Provider-specific Features**: No unified way to access unique provider capabilities
2. **Configuration Complexity**: No type-safe way to configure provider-specific parameters
3. **Model Selection**: Limited support for advanced routing and fallback strategies
4. **Parameter Validation**: No validation for provider-specific parameter combinations

## LMConfig Module Design

### Core Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   LMConfig      │    │  AgentFactory    │    │   IAgent        │
│                 │───▶│                  │───▶│                 │
│ - BaseConfig    │    │ - CreateAgent()  │    │ - GenerateReply │
│ - ProviderConfig│    │ - ValidateConfig │    │ - Streaming     │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │
         ▼
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│ProviderConfig   │    │ OpenRouterConfig │    │ AnthropicConfig │
│                 │    │                  │    │                 │
│ - Validate()    │    │ - Providers      │    │ - Thinking      │
│ - ToOptions()   │    │ - Transforms     │    │ - SystemPrompt  │
└─────────────────┘    │ - ModelRouting   │    │ - ToolChoice    │
                       └──────────────────┘    └─────────────────┘
```

### Core Components

#### 1. LMConfig (Main Configuration Class)

```csharp
public class LMConfig
{
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
    
    // Provider-specific configuration
    public ProviderConfig? ProviderConfig { get; set; }
    
    // Function calling
    public List<FunctionContract>? Functions { get; set; }
    public ToolChoiceStrategy ToolChoice { get; set; } = ToolChoiceStrategy.Auto;
    
    // Validation and conversion
    public GenerateReplyOptions ToGenerateReplyOptions();
    public void Validate();
}
```

#### 2. Provider Configuration Hierarchy

```csharp
public abstract class ProviderConfig
{
    public abstract ProviderType Type { get; }
    public abstract void Validate();
    public abstract void ApplyToOptions(GenerateReplyOptions options);
}

public class OpenRouterConfig : ProviderConfig
{
    public override ProviderType Type => ProviderType.OpenRouter;
    
    // Provider routing
    public List<string>? ProviderOrder { get; set; }
    public bool AllowFallbacks { get; set; } = true;
    
    // Model routing
    public List<string>? FallbackModels { get; set; }
    
    // OpenRouter-specific parameters
    public float? TopA { get; set; }
    public float? MinP { get; set; }
    public float? RepetitionPenalty { get; set; }
    
    // OpenRouter features
    public List<string>? Transforms { get; set; }
    public ReasoningConfig? Reasoning { get; set; }
    
    // Provider-specific parameters
    public Dictionary<string, object>? ProviderSpecificParams { get; set; }
}

public class AnthropicConfig : ProviderConfig
{
    public override ProviderType Type => ProviderType.Anthropic;
    
    // Anthropic-specific features
    public string? SystemPrompt { get; set; }
    public ThinkingConfig? Thinking { get; set; }
    public bool DisableParallelToolUse { get; set; } = false;
    
    // Anthropic parameters
    public int? TopK { get; set; }
    public string? AnthropicVersion { get; set; } = "2023-06-01";
}

public class OpenAIConfig : ProviderConfig
{
    public override ProviderType Type => ProviderType.OpenAI;
    
    // OpenAI-specific features
    public ResponseFormat? ResponseFormat { get; set; }
    public int? MaxCompletionTokens { get; set; }
    public Dictionary<string, int>? LogitBias { get; set; }
    public bool LogProbs { get; set; } = false;
    public int? TopLogProbs { get; set; }
    public StreamOptions? StreamOptions { get; set; }
}
```

#### 3. Configuration Builder (Fluent API)

```csharp
public class LMConfigBuilder
{
    private LMConfig _config = new();
    
    // Basic configuration
    public LMConfigBuilder WithModel(string modelId);
    public LMConfigBuilder WithTemperature(float temperature);
    public LMConfigBuilder WithMaxTokens(int maxTokens);
    public LMConfigBuilder WithTopP(float topP);
    public LMConfigBuilder WithSeed(int seed);
    
    // Provider-specific configuration
    public LMConfigBuilder WithOpenRouter(Action<OpenRouterConfigBuilder> configure);
    public LMConfigBuilder WithAnthropic(Action<AnthropicConfigBuilder> configure);
    public LMConfigBuilder WithOpenAI(Action<OpenAIConfigBuilder> configure);
    
    // Function calling
    public LMConfigBuilder WithFunctions(params FunctionContract[] functions);
    public LMConfigBuilder WithToolChoice(ToolChoiceStrategy strategy);
    
    // Build and validate
    public LMConfig Build();
}

public class OpenRouterConfigBuilder
{
    public OpenRouterConfigBuilder WithProviderOrder(params string[] providers);
    public OpenRouterConfigBuilder WithFallbackModels(params string[] models);
    public OpenRouterConfigBuilder WithTopA(float topA);
    public OpenRouterConfigBuilder WithMinP(float minP);
    public OpenRouterConfigBuilder WithRepetitionPenalty(float penalty);
    public OpenRouterConfigBuilder WithTransforms(params string[] transforms);
    public OpenRouterConfigBuilder WithReasoning(ReasoningConfig reasoning);
    public OpenRouterConfigBuilder WithProviderParam(string key, object value);
}
```

#### 4. Enhanced Agent Factory

```csharp
public interface IAgentFactory
{
    IAgent CreateAgent(LMConfig config);
    IAgent CreateAgent(string name, LMConfig config);
    Task<IAgent> CreateAgentAsync(LMConfig config, CancellationToken cancellationToken = default);
}

public class AgentFactory : IAgentFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentFactory> _logger;
    
    public IAgent CreateAgent(LMConfig config)
    {
        config.Validate();
        
        var providerType = DetermineProvider(config);
        
        return providerType switch
        {
            ProviderType.OpenRouter => CreateOpenRouterAgent(config),
            ProviderType.Anthropic => CreateAnthropicAgent(config),
            ProviderType.OpenAI => CreateOpenAIAgent(config),
            _ => throw new NotSupportedException($"Provider {providerType} not supported")
        };
    }
    
    private ProviderType DetermineProvider(LMConfig config)
    {
        // Provider determination logic based on:
        // 1. Explicit provider config
        // 2. Model ID patterns (e.g., "anthropic/claude-3", "openai/gpt-4")
        // 3. Configuration features used
    }
}
```

#### 5. Configuration Validation System

```csharp
public class ConfigurationValidator
{
    public static ValidationResult Validate(LMConfig config)
    {
        var result = new ValidationResult();
        
        // Basic validation
        ValidateBasicParameters(config, result);
        
        // Provider-specific validation
        if (config.ProviderConfig != null)
        {
            config.ProviderConfig.Validate();
        }
        
        // Cross-parameter validation
        ValidateParameterCombinations(config, result);
        
        return result;
    }
    
    private static void ValidateBasicParameters(LMConfig config, ValidationResult result)
    {
        if (string.IsNullOrEmpty(config.ModelId))
            result.AddError("ModelId is required");
            
        if (config.Temperature < 0 || config.Temperature > 2)
            result.AddError("Temperature must be between 0 and 2");
            
        if (config.MaxTokens.HasValue && config.MaxTokens <= 0)
            result.AddError("MaxTokens must be positive");
    }
}
```

### Integration with Existing Code

#### 1. Backward Compatibility Adapter

```csharp
public static class LMConfigExtensions
{
    public static GenerateReplyOptions ToGenerateReplyOptions(this LMConfig config)
    {
        var options = new GenerateReplyOptions
        {
            ModelId = config.ModelId,
            Temperature = config.Temperature,
            MaxToken = config.MaxTokens,
            TopP = config.TopP,
            Functions = config.Functions?.ToArray(),
            // ... other mappings
        };
        
        // Apply provider-specific configurations
        config.ProviderConfig?.ApplyToOptions(options);
        
        return options;
    }
    
    public static LMConfig ToLMConfig(this GenerateReplyOptions options)
    {
        // Reverse conversion for migration scenarios
        return new LMConfig
        {
            ModelId = options.ModelId ?? "",
            Temperature = options.Temperature ?? 0.7f,
            MaxTokens = options.MaxToken,
            // ... other mappings
        };
    }
}
```

#### 2. Enhanced Service Registration

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLMConfig(this IServiceCollection services)
    {
        services.AddSingleton<IAgentFactory, AgentFactory>();
        services.AddTransient<LMConfigBuilder>();
        services.AddSingleton<ConfigurationValidator>();
        
        // Register provider-specific clients
        services.AddHttpClient<IAnthropicClient, AnthropicClient>();
        services.AddHttpClient<IOpenClient, OpenClient>();
        
        return services;
    }
    
    public static IServiceCollection AddLMConfigFromConfiguration(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        services.AddLMConfig();
        
        // Bind configuration sections
        services.Configure<LMConfigDefaults>(
            configuration.GetSection("LMConfig:Defaults"));
        services.Configure<ProviderSettings>(
            configuration.GetSection("LMConfig:Providers"));
            
        return services;
    }
}
```

## Implementation Plan

### Phase 1: Core Infrastructure (Week 1-2)
1. Create base `LMConfig` class with common parameters
2. Implement `ProviderConfig` hierarchy
3. Create basic `LMConfigBuilder` with fluent API
4. Implement configuration validation system

### Phase 2: Provider-Specific Configurations (Week 3-4)
1. Implement `OpenRouterConfig` with provider routing and non-standard parameters
2. Implement `AnthropicConfig` with thinking and system prompt support
3. Implement `OpenAIConfig` with structured outputs and advanced features
4. Create provider-specific builders

### Phase 3: Agent Factory Enhancement (Week 5-6)
1. Enhance `AgentFactory` to use `LMConfig`
2. Implement provider detection logic
3. Create configuration-to-options adapters
4. Add comprehensive error handling and logging

### Phase 4: Integration and Testing (Week 7-8)
1. Create backward compatibility adapters
2. Update existing code to use new factory
3. Implement comprehensive test suite
4. Create migration documentation

### Phase 5: Advanced Features (Week 9-10)
1. Add configuration serialization/deserialization
2. Implement configuration templates and presets
3. Add runtime configuration validation
4. Create configuration management tools

## Usage Examples

### Basic Usage

```csharp
// Simple configuration
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithTemperature(0.7f)
    .WithMaxTokens(1000)
    .Build();

var agent = agentFactory.CreateAgent(config);
```

### OpenRouter with Provider Routing

```csharp
var config = new LMConfigBuilder()
    .WithModel("anthropic/claude-3-sonnet")
    .WithOpenRouter(or => or
        .WithProviderOrder("anthropic", "openai")
        .WithFallbackModels("anthropic/claude-3-haiku", "openai/gpt-3.5-turbo")
        .WithTopA(0.8f)
        .WithMinP(0.1f)
        .WithTransforms("middle-out"))
    .Build();

var agent = agentFactory.CreateAgent(config);
```

### Anthropic with Extended Thinking

```csharp
var config = new LMConfigBuilder()
    .WithModel("claude-3-sonnet-20240229")
    .WithAnthropic(ant => ant
        .WithSystemPrompt("You are a helpful assistant.")
        .WithThinking(new ThinkingConfig 
        { 
            BudgetTokens = 2048, 
            Type = ThinkingType.Enabled 
        })
        .WithDisableParallelToolUse(true))
    .WithFunctions(weatherFunction, calculatorFunction)
    .Build();

var agent = agentFactory.CreateAgent(config);
```

### OpenAI with Structured Outputs

```csharp
var config = new LMConfigBuilder()
    .WithModel("gpt-4")
    .WithOpenAI(oai => oai
        .WithResponseFormat(new ResponseFormat
        {
            Type = ResponseFormatType.JsonSchema,
            JsonSchema = weatherResponseSchema
        })
        .WithLogProbs(true)
        .WithTopLogProbs(5))
    .Build();

var agent = agentFactory.CreateAgent(config);
```

### Configuration from JSON

```csharp
var configJson = """
{
  "modelId": "anthropic/claude-3-sonnet",
  "temperature": 0.7,
  "maxTokens": 2000,
  "providerConfig": {
    "type": "OpenRouter",
    "providerOrder": ["anthropic", "openai"],
    "fallbackModels": ["anthropic/claude-3-haiku"],
    "topA": 0.8,
    "transforms": ["middle-out"]
  }
}
""";

var config = JsonSerializer.Deserialize<LMConfig>(configJson);
var agent = agentFactory.CreateAgent(config);
```

## Migration Strategy

### 1. Gradual Migration Approach

```csharp
// Phase 1: Introduce alongside existing system
public class HybridAgentFactory : IAgentFactory
{
    public IAgent CreateAgent(GenerateReplyOptions options) // Legacy
    {
        var config = options.ToLMConfig();
        return CreateAgent(config);
    }
    
    public IAgent CreateAgent(LMConfig config) // New
    {
        // New implementation
    }
}
```

### 2. Configuration Migration Tools

```csharp
public class ConfigurationMigrator
{
    public LMConfig MigrateFromOptions(GenerateReplyOptions options)
    {
        // Convert legacy options to new config
    }
    
    public void MigrateConfigurationFiles(string directory)
    {
        // Migrate JSON configuration files
    }
}
```

### 3. Deprecation Timeline

- **Month 1-2**: Introduce LMConfig alongside existing system
- **Month 3-4**: Update all internal code to use LMConfig
- **Month 5-6**: Mark old methods as obsolete
- **Month 7-8**: Remove deprecated methods (breaking change)

## Benefits and Impact

### Benefits
1. **Unified Configuration**: Single system for all providers
2. **Type Safety**: Compile-time validation of configurations
3. **Extensibility**: Easy to add new providers and features
4. **Developer Experience**: Fluent API and clear documentation
5. **Maintainability**: Centralized configuration logic

### Performance Impact
- **Minimal Runtime Overhead**: Configuration validation at creation time
- **Memory Efficiency**: Shared configuration objects
- **Caching Opportunities**: Reusable agent instances for same configurations

### Testing Strategy
1. **Unit Tests**: Each component with comprehensive test coverage
2. **Integration Tests**: End-to-end scenarios with real providers
3. **Performance Tests**: Configuration creation and agent instantiation
4. **Compatibility Tests**: Backward compatibility validation

## Conclusion

The LMConfig module provides a robust, extensible foundation for managing the complexity of multiple Language Model providers while maintaining type safety and developer productivity. The design leverages proven patterns (Builder, Strategy, Factory) and integrates seamlessly with the existing codebase, providing a clear migration path and immediate value.

The modular architecture ensures that new providers can be easily added, and the configuration system can evolve to support new features as the LLM ecosystem continues to develop. 