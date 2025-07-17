# LmDotNet Misc Tools

This package provides miscellaneous utilities for LmDotNet, including **HTTP-level caching** for LLM requests.

## Features

- **HTTP-Level Caching**: Cache HTTP requests and responses at the HttpClient level
- **SHA256-Based Cache Keys**: Generate cache keys from URL + POST body content
- **File-Based Storage**: Store cached responses as individual files with SHA256 filenames
- **Configurable Options**: Extensive configuration via code, configuration files, or environment variables
- **Provider-Agnostic**: Works with any HTTP-based LLM provider (OpenAI, Anthropic, etc.)
- **Thread-Safe**: Concurrent cache access with proper synchronization
- **Expiration Support**: Automatic cache expiration with configurable timeouts

## Quick Start

### Basic Usage with Improved Defaults

```csharp
using AchieveAi.LmDotnetTools.Misc.Extensions;
using AchieveAi.LmDotnetTools.Misc.Http;

// Configure services with sensible defaults
var services = new ServiceCollection();
services.AddLlmFileCacheFromEnvironment(); // Uses defaults if no env vars set

// Create caching HttpClient for OpenAI - cache will be stored in ./LLM_CACHE
var httpClient = services.CreateCachingOpenAIClient(
    apiKey: "your-api-key",
    baseUrl: "https://api.openai.com/v1");

// Use the HttpClient with any OpenAI provider
var openAiClient = new OpenClient(httpClient, "https://api.openai.com/v1");
```

### Improved Defaults (No Configuration Required)

The caching system now uses sensible defaults that work out of the box:

- **Cache Directory**: `./LLM_CACHE` (in current working directory)
- **Caching Enabled**: `true`
- **Cache Expiration**: `24 hours`
- **Max Cache Items**: `10,000`
- **Max Cache Size**: `1 GB`
- **Cleanup on Startup**: `false` (keeps implementation simple)

### Immutable Configuration

`LlmCacheOptions` is now an immutable record, providing:
- **Thread Safety**: Configuration cannot be modified after creation
- **Value Equality**: Records provide structural equality by default
- **Immutability**: Prevents accidental configuration changes
- **Better Testing**: Easier to create test configurations

```csharp
// Create immutable configuration
var options = new LlmCacheOptions
{
    CacheDirectory = "./MyCache",
    EnableCaching = true,
    CacheExpiration = TimeSpan.FromHours(48)
};

// Use with method to create variations
var testOptions = options with { CacheDirectory = "./TestCache" };
```

### Legacy Usage (More Configuration)

```csharp
using AchieveAi.LmDotnetTools.Misc.Extensions;
using AchieveAi.LmDotnetTools.Misc.Http;

// Configure services
var services = new ServiceCollection();
services.AddLlmFileCacheFromEnvironment(); // Uses environment variables
// OR
services.AddLlmFileCache(new LlmCacheOptions {
    CacheDirectory = "./MyCustomCache",
    CacheExpiration = TimeSpan.FromHours(48)
});

// Create caching HttpClient for OpenAI
var httpClient = services.CreateCachingOpenAIClient(
    apiKey: "your-api-key",
    baseUrl: "https://api.openai.com/v1");

// Use the HttpClient with any OpenAI provider
var openAiClient = new OpenClient(httpClient, "https://api.openai.com/v1");
```

### Factory Pattern

```csharp
// Register the factory
services.AddLlmFileCache(options => { /* configure */ });

// Use the factory
var serviceProvider = services.BuildServiceProvider();
var factory = serviceProvider.GetRequiredService<ICachingHttpClientFactory>();

// Create different clients
var openAiClient = factory.CreateForOpenAI("api-key", "https://api.openai.com/v1");
var anthropicClient = factory.CreateForAnthropic("api-key", "https://api.anthropic.com");
```

### Wrapping Existing HttpClients

```csharp
// Wrap an existing HttpClient with caching
var existingClient = new HttpClient();
var cachedClient = services.WrapWithCache(existingClient);
```

## Configuration

### Code Configuration

```csharp
services.AddLlmFileCache(new LlmCacheOptions
{
    CacheDirectory = "./LLM_CACHE",  // Current directory + LLM_CACHE
    EnableCaching = true,
    CacheExpiration = TimeSpan.FromHours(24),
    MaxCacheItems = 10_000,
    MaxCacheSizeBytes = 1_073_741_824, // 1 GB
    CleanupOnStartup = false  // Keep it simple
});
```

### Configuration File (appsettings.json)

```json
{
  "LlmCache": {
    "CacheDirectory": "./LLM_CACHE",
    "EnableCaching": true,
    "CacheExpirationHours": 24,
    "MaxCacheItems": 10000,
    "MaxCacheSizeBytes": 1073741824,
    "CleanupOnStartup": false
  }
}
```

```csharp
services.AddLlmFileCache(configuration, "LlmCache");
```

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `LLM_CACHE_DIRECTORY` | Cache directory path | `./LLM_CACHE` (current directory) |
| `LLM_CACHE_ENABLED` | Enable/disable caching | `true` |
| `LLM_CACHE_EXPIRATION_HOURS` | Cache expiration in hours | `24` |
| `LLM_CACHE_MAX_ITEMS` | Maximum cached items | `10000` |
| `LLM_CACHE_MAX_SIZE_MB` | Maximum cache size in megabytes | `1024` |
| `LLM_CACHE_CLEANUP_ON_STARTUP` | Clean up expired items on startup | `false` |

```csharp
services.AddLlmFileCacheFromEnvironment();
```

## How It Works

### HTTP-Level Caching

The caching system works at the HTTP level using a custom `HttpMessageHandler`:

1. **Request Interception**: `CachingHttpMessageHandler` intercepts HTTP requests
2. **Cache Key Generation**: Creates SHA256 hash from URL + POST body content
3. **Cache Lookup**: Checks if a cached response exists and is not expired
4. **Response Caching**: Stores successful HTTP responses for future use
5. **Transparent Operation**: No changes needed to existing provider code

### Cache Key Generation

```csharp
// Cache key is generated from:
// - Full URL (including query parameters)
// - POST body content (JSON request)
// - Authorization scheme (not the actual token)
var cacheKey = SHA256(url + postBody + authScheme);
```

### File Storage

- Each cached response is stored as `{SHA256_HASH}.json`
- Files are stored in the configured cache directory
- Automatic directory creation and cleanup
- Thread-safe file operations

## Architecture

```
HttpClient Request
       ↓
CachingHttpMessageHandler
       ↓
Cache Key Generation (SHA256)
       ↓
Cache Lookup (FileKvStore)
       ↓
Cache Hit? → Return Cached Response
       ↓
Cache Miss → Forward to Real HTTP Handler
       ↓
Cache Successful Response
       ↓
Return Response to Client
```

## Integration with AchieveAI

When AchieveAI adopts LmDotNet, they can integrate caching in several ways:

### Option 1: Factory Pattern

```csharp
// In AchieveAI's service registration
services.AddLlmFileCacheFromEnvironment();

// When creating LLM clients
var factory = serviceProvider.GetRequiredService<ICachingHttpClientFactory>();
var httpClient = factory.CreateForOpenAI(apiKey, baseUrl);
var llmClient = new OpenClient(httpClient, baseUrl);
```

### Option 2: HttpClient Wrapping

```csharp
// Wrap existing HttpClient creation
var originalClient = CreateHttpClient(apiKey, baseUrl);
var cachedClient = services.WrapWithCache(originalClient);
var llmClient = new OpenClient(cachedClient, baseUrl);
```

### Option 3: Direct Integration

```csharp
// Direct use of CachingHttpClientFactory
var httpClient = CachingHttpClientFactory.CreateForOpenAIWithCache(
    apiKey, baseUrl, cache, options, timeout, headers, logger);
```

## Performance Considerations

### Cache Hits
- **Instant Response**: Cached responses return immediately
- **No Network Overhead**: Eliminates HTTP round trips
- **Cost Savings**: Reduces LLM API usage costs

### Cache Misses
- **Minimal Overhead**: SHA256 hashing is very fast
- **Async Caching**: Response caching doesn't block the request
- **Graceful Degradation**: Caching errors don't affect actual requests

### Memory Usage
- **File-Based Storage**: Minimal memory footprint
- **Stream Processing**: Large responses handled efficiently
- **Cleanup**: Automatic expired item removal

## Monitoring and Diagnostics

### Cache Statistics

```csharp
var stats = await services.GetCacheStatisticsAsync();
Console.WriteLine($"Cache Items: {stats.ItemCount}");
Console.WriteLine($"Cache Size: {stats.TotalSizeBytes} bytes");
Console.WriteLine($"Utilization: {stats.ItemUtilizationPercent:F1}%");
```

### Cache Management

```csharp
// Clear all cached items
await services.ClearLlmCacheAsync();

// Check cache statistics
var stats = await services.GetCacheStatisticsAsync();
if (stats.SizeUtilizationPercent > 90)
{
    // Implement cache cleanup logic
}
```

## Error Handling

The caching system is designed to be resilient:

- **Cache Failures**: Never block actual HTTP requests
- **Expired Items**: Automatically ignored and cleaned up
- **File System Errors**: Gracefully degraded to direct requests
- **Serialization Errors**: Logged but don't affect functionality

## Testing

### Unit Testing

```csharp
[Test]
public async Task CachingHttpClient_CachesSuccessfulRequests()
{
    var cache = new FileKvStore(tempDirectory);
    var options = new LlmCacheOptions { EnableCaching = true };
    
    var httpClient = CachingHttpClientFactory.CreateForOpenAIWithCache(
        "test-key", "https://api.test.com", cache, options);
    
    // First request - cache miss
    var response1 = await httpClient.PostAsync("/chat/completions", content);
    
    // Second request - cache hit
    var response2 = await httpClient.PostAsync("/chat/completions", content);
    
    // Both responses should be identical
    Assert.That(await response1.Content.ReadAsStringAsync(), 
                Is.EqualTo(await response2.Content.ReadAsStringAsync()));
}
```

### Integration Testing

```csharp
[Test]
public async Task EndToEnd_CachingWithRealProvider()
{
    var services = new ServiceCollection();
    services.AddLlmFileCache(options => {
        options.CacheDirectory = Path.GetTempPath();
        options.EnableCaching = true;
    });
    
    var httpClient = services.CreateCachingOpenAIClient(
        Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        "https://api.openai.com/v1");
    
    var client = new OpenClient(httpClient, "https://api.openai.com/v1");
    
    // Test caching with real API calls
    var request = new ChatCompletionRequest { /* ... */ };
    var response1 = await client.CreateChatCompletionsAsync(request);
    var response2 = await client.CreateChatCompletionsAsync(request);
    
    // Second response should be from cache
}
```

## Troubleshooting

### Common Issues

1. **Cache Not Working**
   - Check `EnableCaching` is `true`
   - Verify cache directory permissions
   - Ensure requests are identical (same URL + body)

2. **High Memory Usage**
   - Reduce `MaxCacheItems` or `MaxCacheSizeBytes`
   - Enable `CleanupOnStartup`
   - Implement periodic cache cleanup

3. **Slow Performance**
   - Check cache directory is on fast storage (SSD)
   - Verify cache hit rate with statistics
   - Consider cache expiration settings

### Debugging

Enable detailed logging:

```csharp
services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
```

Log output will include:
- Cache hits/misses with keys
- File operations and errors
- Performance timings
- Cache statistics

## License

This project is licensed under the MIT License. 