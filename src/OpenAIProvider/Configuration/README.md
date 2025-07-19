# OpenRouter Usage Tracking Middleware - Complete Guide

This document provides comprehensive guidance for configuring, using, and troubleshooting the OpenRouter usage tracking middleware in LmDotnet.

## Overview

The OpenRouter Usage Tracking Middleware automatically collects token and cost statistics for all calls routed through OpenRouter. It provides:

- **Inline Usage Accounting**: Prefers usage data returned directly in API responses
- **Fallback Generation Lookup**: Falls back to `/api/v1/generation` endpoint when inline data unavailable
- **Intelligent Caching**: In-memory cache with configurable TTL to reduce API calls
- **Automatic Integration**: Seamlessly integrates when provider is "openrouter"
- **Performance Optimized**: ≤400ms p99 latency budget for final chunk enrichment

## Environment Variables

| Variable | Type | Default | Description |
|----------|------|---------|-------------|
| `ENABLE_USAGE_MIDDLEWARE` | bool | `true` | Enable/disable OpenRouter usage tracking middleware |
| `ENABLE_INLINE_USAGE` | bool | `true` | Enable/disable inline usage accounting in requests |
| `USAGE_CACHE_TTL_SEC` | int | `300` | Cache TTL in seconds for usage data |
| `OPENROUTER_API_KEY` | string | *required* | OpenRouter API key for usage lookup (required when middleware enabled) |

## Usage Examples

### Basic Configuration

```bash
# .env file or environment variables
ENABLE_USAGE_MIDDLEWARE=true
ENABLE_INLINE_USAGE=true
USAGE_CACHE_TTL_SEC=300
OPENROUTER_API_KEY=sk-or-your-api-key-here
```

### Dependency Injection Setup

```csharp
using AchieveAi.LmDotnetTools.OpenAIProvider.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

// In your Program.cs or Startup.cs
var builder = WebApplication.CreateBuilder(args);

// Validate configuration early (fail-fast)
builder.Services.ValidateOpenRouterUsageConfiguration(builder.Configuration);

// Register configuration as a service
builder.Services.AddOpenRouterUsageConfiguration(builder.Configuration);

var app = builder.Build();
```

### Using Configuration in Code

```csharp
// Inject the configuration service
public class SomeService
{
    private readonly IOpenRouterUsageConfiguration _config;
    
    public SomeService(IOpenRouterUsageConfiguration config)
    {
        _config = config;
    }
    
    public void DoSomething()
    {
        if (_config.EnableUsageMiddleware)
        {
            // Use middleware-related functionality
            var apiKey = _config.OpenRouterApiKey;
            var cacheTtl = _config.UsageCacheTtlSec;
        }
    }
}
```

### Static Configuration Helpers

```csharp
using AchieveAi.LmDotnetTools.OpenAIProvider.Configuration;

// Read configuration statically
var enableMiddleware = EnvironmentVariables.GetEnableUsageMiddleware(configuration);
var enableInlineUsage = EnvironmentVariables.GetEnableInlineUsage(configuration);
var cacheTtl = EnvironmentVariables.GetUsageCacheTtlSec(configuration);
var apiKey = EnvironmentVariables.GetOpenRouterApiKey(configuration);

// Validate configuration
try 
{
    EnvironmentVariables.ValidateOpenRouterUsageConfiguration(configuration);
    Console.WriteLine("Configuration is valid");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Configuration error: {ex.Message}");
}

// Get configuration summary for logging
var summary = EnvironmentVariables.GetConfigurationSummary(configuration);
Console.WriteLine(summary);
// Output: "OpenRouter Usage Middleware Configuration: Enabled=true, InlineUsage=true, CacheTtl=300s, ApiKeyPresent=true"
```

### Disabling the Middleware

```bash
# Disable usage middleware entirely
ENABLE_USAGE_MIDDLEWARE=false
# OPENROUTER_API_KEY not required when disabled
```

### Development vs Production

```bash
# Development - more verbose logging, shorter cache
ENABLE_USAGE_MIDDLEWARE=true
ENABLE_INLINE_USAGE=true
USAGE_CACHE_TTL_SEC=60
OPENROUTER_API_KEY=sk-or-dev-key

# Production - optimized settings
ENABLE_USAGE_MIDDLEWARE=true
ENABLE_INLINE_USAGE=true
USAGE_CACHE_TTL_SEC=600
OPENROUTER_API_KEY=sk-or-prod-key
```

## Sample Enriched JSON Examples

### Inline Usage Response (Preferred)

When OpenRouter returns usage data directly in the completion response:

```json
{
  "id": "chatcmpl-abc123",
  "object": "chat.completion",
  "model": "openai/gpt-4",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "Hello! How can I help you today?"
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 25,
    "completion_tokens": 12,
    "total_tokens": 37,
    "total_cost": 0.001425
  }
}
```

**UsageMessage Generated:**
```json
{
  "role": "assistant",
  "usage": {
    "promptTokens": 25,
    "completionTokens": 12,
    "totalTokens": 37,
    "totalCost": 0.001425,
    "extraProperties": {
      "model": "openai/gpt-4",
      "is_cached": false
    }
  },
  "fromAgent": "assistant",
  "generationId": "chatcmpl-abc123",
  "metadata": {
    "source": "openrouter_middleware"
  }
}
```

### Fallback Generation Response

When usage data is retrieved from OpenRouter's generation endpoint:

**API Request:**
```
GET https://openrouter.ai/api/v1/generation?id=chatcmpl-abc123
Authorization: Bearer sk-or-your-api-key
```

**API Response:**
```json
{
  "data": {
    "model": "openai/gpt-4",
    "tokens_prompt": 25,
    "tokens_completion": 12,
    "total_cost": 0.001425,
    "generation_time": 2.5,
    "streamed": true,
    "created_at": "2024-01-15T10:30:00Z"
  }
}
```

**UsageMessage Generated:**
```json
{
  "role": "assistant",
  "usage": {
    "promptTokens": 25,
    "completionTokens": 12,
    "totalTokens": 37,
    "totalCost": 0.001425,
    "extraProperties": {
      "model": "openai/gpt-4",
      "generation_time": 2.5,
      "streamed": true,
      "created_at": "2024-01-15T10:30:00Z",
      "is_cached": false
    }
  },
  "fromAgent": "assistant",
  "generationId": "chatcmpl-abc123",
  "metadata": {
    "source": "openrouter_middleware"
  }
}
```

### Cached Usage Response

When usage data is retrieved from the in-memory cache:

**UsageMessage Generated:**
```json
{
  "role": "assistant",
  "usage": {
    "promptTokens": 25,
    "completionTokens": 12,
    "totalTokens": 37,
    "totalCost": 0.001425,
    "extraProperties": {
      "model": "openai/gpt-4",
      "is_cached": true
    }
  },
  "fromAgent": "assistant",
  "generationId": "chatcmpl-abc123",
  "metadata": {
    "source": "openrouter_middleware"
  }
}
```

### Streaming Response with Usage

For streaming responses, usage is enriched in the final message:

**Streaming Messages:**
```json
// Messages 1-n (original messages unchanged)
{"role": "assistant", "text": "Hello! How"}
{"role": "assistant", "text": " can I help"}
{"role": "assistant", "text": " you today?"}

// Final message (dedicated UsageMessage)
{
  "role": "assistant",
  "usage": {
    "promptTokens": 25,
    "completionTokens": 12,
    "totalTokens": 37,
    "totalCost": 0.001425,
    "extraProperties": {
      "model": "openai/gpt-4",
      "is_cached": false
    }
  },
  "fromAgent": "assistant",
  "generationId": "chatcmpl-abc123",
  "metadata": {
    "source": "openrouter_middleware"
  }
}
```

## OpenRouter Documentation Links

For comprehensive information about OpenRouter's usage tracking capabilities, refer to the official documentation:

- **OpenRouter API Documentation**: https://openrouter.ai/docs
- **Chat Completions with Usage**: https://openrouter.ai/docs#chat-completions
- **Generation Endpoint**: https://openrouter.ai/docs#get-generation
- **Usage and Billing**: https://openrouter.ai/docs#usage
- **Model Pricing**: https://openrouter.ai/docs#models
- **API Authentication**: https://openrouter.ai/docs#authentication

### Key OpenRouter Features

- **Inline Usage**: Set `"usage": {"include": true}` in requests to receive usage data directly
- **Generation Lookup**: Use `GET /api/v1/generation?id={completion_id}` for post-completion usage retrieval
- **Model Support**: Usage tracking available for all supported models
- **Cost Calculation**: Automatic cost calculation based on model pricing and token usage

## Troubleshooting

### Common Issues

#### ❌ Missing API Key Error

**Error:**
```
InvalidOperationException: OpenRouter usage middleware is enabled but OPENROUTER_API_KEY environment variable is missing or empty. Either set the API key or disable the middleware by setting ENABLE_USAGE_MIDDLEWARE=false.
```

**Solution:**
```bash
# Option 1: Set the API key
export OPENROUTER_API_KEY=sk-or-your-api-key-here

# Option 2: Disable middleware
export ENABLE_USAGE_MIDDLEWARE=false
```

#### ❌ Usage Data Not Appearing

**Symptoms:**
- Messages don't have usage metadata
- No usage enrichment in final messages

**Diagnostics:**
1. **Check middleware is enabled:**
   ```bash
   echo $ENABLE_USAGE_MIDDLEWARE  # Should be 'true' or empty
   ```

2. **Verify provider is OpenRouter:**
   ```csharp
   // Ensure you're using OpenRouter as provider
   var options = new GenerateReplyOptions
   {
       ModelId = "openai/gpt-4",  // Provider format: provider/model
       // ... other options
   };
   ```

3. **Check completion ID exists:**
   ```csharp
   // Verify messages have completion IDs
   foreach (var message in messages)
   {
       Console.WriteLine($"Message ID: {message.GenerationId}");
       Console.WriteLine($"Metadata keys: {string.Join(", ", message.Metadata?.Keys ?? new string[0])}");
   }
   ```

**Solutions:**
- Ensure `ENABLE_USAGE_MIDDLEWARE=true`
- Use OpenRouter-compatible model names (e.g., `openai/gpt-4`, `anthropic/claude-3-sonnet`)
- Verify `OPENROUTER_API_KEY` is valid
- Check logs for middleware warnings

#### ❌ High Latency / Timeouts

**Symptoms:**
- Slow response times
- Timeout errors in logs
- Missing usage data for some requests

**Diagnostics:**
```bash
# Check middleware logs for timeout warnings
grep "Usage middleware failure" application.log
grep "timeout" application.log
```

**Solutions:**
1. **Increase cache TTL to reduce API calls:**
   ```bash
   export USAGE_CACHE_TTL_SEC=600  # 10 minutes
   ```

2. **Disable fallback for performance-critical scenarios:**
   ```bash
   export ENABLE_INLINE_USAGE=true
   # Relies only on inline usage, skips generation endpoint
   ```

3. **Check OpenRouter API status:**
   - Visit https://status.openrouter.ai/
   - Monitor OpenRouter API response times

#### ❌ Incorrect Usage Costs

**Symptoms:**
- Usage costs don't match OpenRouter dashboard
- Token counts seem wrong

**Diagnostics:**
1. **Compare with OpenRouter logs:**
   - Check your OpenRouter account dashboard
   - Compare completion IDs and costs

2. **Verify model pricing:**
   ```bash
   # Check current model pricing at:
   # https://openrouter.ai/docs#models
   ```

**Solutions:**
- Clear usage cache to force fresh lookups:
  ```bash
  # Restart application or reduce cache TTL temporarily
  export USAGE_CACHE_TTL_SEC=1
  ```
- Verify model names match exactly with OpenRouter
- Check for any model aliases or redirects

#### ❌ Memory/Performance Issues

**Symptoms:**
- Increasing memory usage over time
- Slow performance
- Cache-related errors

**Diagnostics:**
```csharp
// Monitor cache size and performance
var cacheStats = usageCache.GetStatistics(); // If available
Console.WriteLine($"Cache entries: {cacheStats.EntryCount}");
Console.WriteLine($"Hit ratio: {cacheStats.HitRatio:P}");
```

**Solutions:**
1. **Tune cache TTL:**
   ```bash
   # Shorter TTL for high-volume applications
   export USAGE_CACHE_TTL_SEC=60   # 1 minute
   
   # Longer TTL for low-volume applications  
   export USAGE_CACHE_TTL_SEC=3600 # 1 hour
   ```

2. **Monitor cache efficiency:**
   - High cache hit ratio (>80%) = good
   - Low cache hit ratio (<50%) = consider adjusting TTL

### Debug Logging

Enable detailed logging to troubleshoot issues:

```csharp
// In appsettings.json or logging configuration
{
  "Logging": {
    "LogLevel": {
      "AchieveAi.LmDotnetTools.OpenAIProvider.Middleware.OpenRouterUsageMiddleware": "Debug",
      "Default": "Information"
    }
  }
}
```

**Debug Log Examples:**
```
[DEBUG] OpenRouterUsageMiddleware initialized with cache TTL: 300 seconds
[DEBUG] Cache hit for completion chatcmpl-abc123
[DEBUG] Cache miss for completion chatcmpl-def456, attempting API fallback
[INFO] Usage data enriched: {completionId: chatcmpl-abc123, model: openai/gpt-4, promptTokens: 25, completionTokens: 12, totalCost: 0.001425, cached: false}
[WARN] Usage middleware failure: all 7 retries exhausted for completion chatcmpl-error789
```

### Performance Monitoring

Track middleware performance metrics:

**Key Metrics:**
- **Cache hit ratio**: Should be >70% for good performance
- **API call frequency**: Monitor generation endpoint usage  
- **Latency impact**: Final chunk should be ≤400ms added latency
- **Error rate**: Usage enrichment failures should be <1%

**Monitoring Queries:**
```bash
# Cache performance
grep "Cache hit" application.log | wc -l
grep "Cache miss" application.log | wc -l

# Error tracking  
grep "Usage middleware failure" application.log | wc -l
grep "Counter increment: usage_middleware_failure" application.log | wc -l

# Latency monitoring
grep "Usage data enriched" application.log | tail -100
```

### Support and Community

If you continue to experience issues:

1. **Check GitHub Issues**: https://github.com/your-org/LmDotnet/issues
2. **OpenRouter Support**: https://openrouter.ai/support
3. **Review Documentation**: Ensure you're following the latest guidelines
4. **Enable Debug Logging**: Collect detailed logs before reporting issues

## Error Handling

### Invalid Configuration Values

Invalid values fall back to defaults:
- Non-boolean values for flags default to `true`
- Invalid or negative cache TTL defaults to `300` seconds
- Empty API key is treated as missing (when validation runs)

### Network Resilience

The middleware is designed to gracefully handle network issues:
- **Automatic retries**: Up to 6 attempts with 500ms delays
- **Timeout handling**: 3s for streaming, 5s for synchronous calls
- **Graceful degradation**: Continues without usage data if all retries fail
- **Error logging**: Structured logs for monitoring and debugging

## Integration with OpenRouterUsageMiddleware

The existing `OpenRouterUsageMiddleware` automatically reads these environment variables:

- `USAGE_CACHE_TTL_SEC` - Used for cache TTL configuration
- `OPENROUTER_API_KEY` - Used for API authentication

The middleware constructor accepts these values as parameters for dependency injection scenarios. 