# OpenAI-Compatible Providers Support

## Overview

The LMConfig system supports multiple OpenAI-compatible providers through our existing OpenAI provider implementation. This allows you to use the same codebase to access models from different providers with minimal configuration changes.

## Supported Providers

### DeepInfra
**Description**: Multi-vendor model hosting platform with competitive pricing  
**Base URL**: `https://api.deepinfra.com/v1/openai/`  
**Authentication**: `DEEPINFRA_API_KEY` environment variable  
**Tags**: `["economic", "multi-vendor", "openai-compatible"]`

**Supported Models**:
- GPT-4o, GPT-4o-mini (OpenAI models)
- Llama-3.1-70B (Meta models)
- DeepSeek-R1-Distill-Llama-70B (DeepSeek models)

### Cerebras
**Description**: High-performance inference with hardware acceleration  
**Base URL**: `https://api.cerebras.ai/v1/`  
**Authentication**: `CEREBRAS_API_KEY` environment variable  
**Tags**: `["ultra-fast", "high-performance", "openai-compatible"]`

**Supported Models**:
- Llama-4-Scout-17B-16E-Instruct
- Llama-3.1-8B variants

### Groq
**Description**: Ultra-fast inference for various models  
**Base URL**: `https://api.groq.com/openai/v1/`  
**Authentication**: `GROQ_API_KEY` environment variable  
**Tags**: `["ultra-fast", "high-performance", "openai-compatible"]`

**Supported Models**:
- Llama-3.1-70B-Instruct
- Llama-4-Scout-17B-16E-Instruct
- Gemma-2-9B variants
- Mistral-7B variants
- DeepSeek-R1-Distill-Llama-70B

### OpenRouter
**Description**: Provider aggregator with failover capabilities  
**Base URL**: `https://openrouter.ai/api/v1/`  
**Authentication**: `OPENROUTER_API_KEY` environment variable  
**Tags**: `["aggregator", "fallback", "reliable", "openai-compatible"]`

**Supported Models**: All models from multiple providers (GPT, Claude, Llama, Gemini, etc.)

### Google Gemini
**Description**: Google's Gemini models via OpenAI-compatible API  
**Base URL**: `https://generativelanguage.googleapis.com/v1beta/openai/`  
**Authentication**: `GEMINI_API_KEY` environment variable  
**Tags**: `["economic", "multimodal", "long-context", "openai-compatible"]`

**Supported Models**:
- Gemini-2.0-Flash
- Gemini-2.5-Pro
- Gemini-2.5-Flash

## Configuration Examples

### Basic Provider Selection

```csharp
// Prefer ultra-fast providers for real-time applications
var config = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .PreferTags(ProviderTags.UltraFast, ProviderTags.HighPerformance)
    .Build();

// Result: Selects Groq or Cerebras based on availability and priority
```

### Cost-Conscious Selection

```csharp
// Prefer economic providers for budget-conscious applications
var config = new LMConfigBuilder()
    .WithModel("gpt-4o")
    .PreferTags(ProviderTags.Economic, ProviderTags.CostEffective)
    .WithBudgetLimit(0.05m)
    .Build();

// Result: Selects DeepInfra over OpenAI for cost savings
```

### High-Availability Setup

```csharp
// Use aggregator providers for high availability
var config = new LMConfigBuilder()
    .WithModel("claude-3-sonnet")
    .RequireTags(ProviderTags.Aggregator)
    .Build();

// Result: Uses OpenRouter which provides automatic failover
```

### Provider-Specific Configuration

```csharp
// DeepInfra-specific configuration
var deepInfraConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .WithDeepInfra(config => config
        .WithEconomicTier()
        .WithMultiVendorFallback())
    .Build();

// Groq-specific configuration for ultra-fast inference
var groqConfig = new LMConfigBuilder()
    .WithModel("llama-4-scout-17b-16e-instruct")
    .WithGroq(config => config
        .WithUltraFastMode()
        .WithHighPerformanceHardware())
    .Build();

// Google Gemini for long context
var geminiConfig = new LMConfigBuilder()
    .WithModel("gemini-2.0-flash")
    .WithGoogleGemini(config => config
        .WithLongContext(true)
        .WithMultimodal(true))
    .Build();
```

## Environment Setup

To use these providers, set the appropriate environment variables:

```bash
# DeepInfra
export DEEPINFRA_API_KEY="your-deepinfra-api-key"

# Cerebras
export CEREBRAS_API_KEY="your-cerebras-api-key"

# Groq
export GROQ_API_KEY="your-groq-api-key"

# OpenRouter
export OPENROUTER_API_KEY="your-openrouter-api-key"

# Google Gemini
export GEMINI_API_KEY="your-gemini-api-key"
```

## Provider Selection Strategies

### By Performance
```csharp
// Ultra-fast inference (Groq, Cerebras)
var ultraFastConfig = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .PreferTags(ProviderTags.UltraFast)
    .WithMaxLatency(TimeSpan.FromSeconds(2))
    .Build();
```

### By Cost
```csharp
// Most economical option (DeepInfra, Gemini)
var economicConfig = new LMConfigBuilder()
    .WithModel("gpt-4o-mini")
    .PreferEconomic()
    .WithBudgetLimit(0.02m)
    .Build();
```

### By Reliability
```csharp
// High availability with fallback (OpenRouter)
var reliableConfig = new LMConfigBuilder()
    .WithModel("claude-3-sonnet")
    .PreferTags(ProviderTags.Aggregator, ProviderTags.Reliable)
    .WithFallbackStrategy(FallbackStrategy.Automatic)
    .Build();
```

## Testing and Validation

The implementation includes comprehensive tests for:

1. **API Compatibility**: Ensuring OpenAI-compatible providers work with our OpenAI agent
2. **Cost Calculation**: Accurate pricing for all providers
3. **Provider Selection**: Tag-based selection and priority handling
4. **Failover Logic**: Automatic fallback when providers are unavailable
5. **Performance Metrics**: Latency and throughput comparisons

## Best Practices

1. **Use Tags for Selection**: Leverage provider tags for automatic selection based on requirements
2. **Set Budget Limits**: Prevent unexpected costs with budget constraints
3. **Configure Fallbacks**: Use OpenRouter or multiple providers for high availability
4. **Monitor Performance**: Track latency and cost metrics across providers
5. **Environment-Specific Configuration**: Use different providers for development, staging, and production

## Migration from Direct Provider SDKs

If you're currently using provider-specific SDKs, migrating to LMConfig provides:

- **Unified API**: Same code works across all providers
- **Automatic Failover**: Built-in redundancy and reliability
- **Cost Optimization**: Automatic selection of cost-effective providers
- **Performance Tracking**: Built-in metrics and monitoring
- **Type Safety**: Compile-time validation of configurations

Example migration:

```csharp
// Before: Direct Groq SDK usage
var groqClient = new GroqClient(apiKey);
var response = await groqClient.ChatCompletionsAsync(model, messages);

// After: LMConfig with automatic provider selection
var config = new LMConfigBuilder()
    .WithModel("llama-3.1-70b-instruct")
    .PreferTags(ProviderTags.UltraFast)
    .Build();

var agent = new EnhancedDynamicProviderAgent(modelConfigService, serviceProvider, logger);
var response = await agent.GenerateReplyAsync(messages, config);
```

This migration provides the same performance benefits while adding cost optimization, automatic failover, and unified configuration management. 