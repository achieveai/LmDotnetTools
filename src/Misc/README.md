# LmDotNet File-Based LLM Caching System

A high-performance, file-based caching solution for LLM requests in the LmDotNet ecosystem. This system provides transparent caching of LLM API requests and responses using configurable file storage with SHA256-based naming.

## Features

- **File-based storage**: Uses individual files with SHA256 names for cache entries
- **Configurable directory**: Store cache files in any directory you specify
- **Transparent caching**: Drop-in replacement for IOpenClient with automatic cache management
- **Cache expiration**: Configurable TTL with automatic cleanup
- **Size limits**: Configure maximum cache size and item count
- **Dependency injection**: Full DI container integration with multiple configuration options
- **Environment configuration**: Configure via environment variables
- **Streaming support**: Caches both regular and streaming LLM responses
- **Comprehensive testing**: 53+ unit and integration tests

## Quick Start

### 1. Basic Setup with Dependency Injection

```csharp
using AchieveAi.LmDotnetTools.Misc.Extensions;

var services = new ServiceCollection();

// Add file-based LLM caching with default settings
services.AddLlmFileCache(options =>
{
    options.CacheDirectory = @"C:\MyApp\LlmCache";
    options.EnableCaching = true;
    options.CacheExpiration = TimeSpan.FromHours(24);
    options.MaxCacheItems = 10_000;
});

// Add and wrap your OpenAI client with caching
services.AddCachedOpenAIClient("your-openai-api-key");

var serviceProvider = services.BuildServiceProvider();
var client = serviceProvider.GetRequiredService<IOpenClient>();

// Use the client normally - caching is transparent
var response = await client.CreateChatCompletionsAsync(request);
```

### 2. Environment Variable Configuration

Set environment variables for configuration:

```bash
# Windows
set LLM_CACHE_DIRECTORY=C:\MyApp\Cache
set LLM_CACHE_ENABLED=true
set LLM_CACHE_EXPIRATION_HOURS=24
set LLM_CACHE_MAX_ITEMS=5000
set LLM_CACHE_MAX_SIZE_MB=100

# Linux/Mac
export LLM_CACHE_DIRECTORY=/var/cache/myapp/llm
export LLM_CACHE_ENABLED=true
export LLM_CACHE_EXPIRATION_HOURS=24
export LLM_CACHE_MAX_ITEMS=5000
export LLM_CACHE_MAX_SIZE_MB=100
```

Then register services:

```csharp
services.AddLlmFileCacheFromEnvironment();
```

### 3. Configuration from appsettings.json

Add to your `appsettings.json`:

```json
{
  "LlmCache": {
    "CacheDirectory": "C:\\MyApp\\Cache",
    "EnableCaching": true,
    "CacheExpiration": "1.00:00:00",
    "MaxCacheItems": 10000,
    "MaxCacheSizeBytes": 104857600,
    "CleanupOnStartup": true
  }
}
```

Register services:

```csharp
services.AddLlmFileCache(configuration);
```

## Configuration Options

### LlmCacheOptions Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `CacheDirectory` | `string` | `%LocalAppData%\AchieveAI\LmDotNet\Cache` | Directory where cache files are stored |
| `EnableCaching` | `bool` | `true` | Enable/disable caching |
| `CacheExpiration` | `TimeSpan?` | `24 hours` | Cache entry expiration time (null = never expires) |
| `MaxCacheItems` | `int?` | `10,000` | Maximum number of cached items (null = no limit) |
| `MaxCacheSizeBytes` | `long?` | `1 GB` | Maximum cache size in bytes (null = no limit) |
| `CleanupOnStartup` | `bool` | `true` | Clean expired entries on startup |

### Environment Variables

| Variable | Type | Description |
|----------|------|-------------|
| `LLM_CACHE_DIRECTORY` | `string` | Cache directory path |
| `LLM_CACHE_ENABLED` | `bool` | Enable/disable caching |
| `LLM_CACHE_EXPIRATION_HOURS` | `double` | Cache expiration in hours |
| `LLM_CACHE_MAX_ITEMS` | `int` | Maximum cached items |
| `LLM_CACHE_MAX_SIZE_MB` | `long` | Maximum cache size in MB |
| `LLM_CACHE_CLEANUP_ON_STARTUP` | `bool` | Cleanup on startup |

## Advanced Usage

### Manual Client Wrapping

```csharp
// Create your own client
var openAiClient = new OpenClient("your-api-key");

// Wrap with caching
var kvStore = new FileKvStore(@"C:\MyCache");
var options = new LlmCacheOptions 
{ 
    CacheDirectory = @"C:\MyCache",
    CacheExpiration = TimeSpan.FromMinutes(30)
};

var cachedClient = new FileCachingClient(openAiClient, kvStore, options);

// Use normally
var response = await cachedClient.CreateChatCompletionsAsync(request);
```

### Decorator Pattern

```csharp
services.AddTransient<IOpenClient, OpenClient>();
services.AddLlmFileCache(options);

// Decorate existing registration with caching
services.DecorateOpenClientWithFileCache();
```

### Factory Pattern

```csharp
services.AddLlmFileCache(options);
services.AddCachedOpenClientFactory();

// Get factory and wrap any client
var factory = serviceProvider.GetRequiredService<Func<IOpenClient, IOpenClient>>();
var originalClient = new OpenClient("api-key");
var cachedClient = factory(originalClient);
```

## Cache Management

### Statistics

```csharp
var stats = await serviceProvider.GetCacheStatisticsAsync();
Console.WriteLine($"Cache enabled: {stats.IsEnabled}");
Console.WriteLine($"Cached items: {stats.TotalItems}");
Console.WriteLine($"Cache directory: {stats.CacheDirectory}");
Console.WriteLine($"Max items: {stats.MaxItems}");
Console.WriteLine($"Expiration: {stats.ConfiguredExpiration}");
```

### Clear Cache

```csharp
// Clear all cached items
await serviceProvider.ClearLlmCacheAsync();
```

### Direct Cache Access

```csharp
var kvStore = serviceProvider.GetRequiredService<FileKvStore>();

// Get cache count
var count = await kvStore.GetCountAsync();

// Clear cache
await kvStore.ClearAsync();

// Manual cache operations
await kvStore.SetAsync("key", "value");
var value = await kvStore.GetAsync<string>("key");
```

## Architecture

### Components

1. **FileKvStore**: Core file-based key-value storage with SHA256 naming
2. **LlmCacheOptions**: Configuration and validation
3. **FileCachingClient**: IOpenClient wrapper providing transparent caching
4. **ServiceCollectionExtensions**: DI registration helpers

### File Storage

- Cache files are stored with SHA256-based names for uniqueness
- Each request generates a unique cache key based on request content
- Files are stored as JSON in the configured directory
- Expired entries are cleaned up automatically (optional)

### Key Generation

Cache keys are generated using SHA256 hash of the serialized request:

```csharp
var requestJson = JsonSerializer.Serialize(request, jsonOptions);
var hashBytes = SHA256.ComputeHash(Encoding.UTF8.GetBytes(requestJson));
var cacheKey = Convert.ToBase64String(hashBytes);
```

## Performance Considerations

### Benefits

- **Reduced API costs**: Avoid duplicate requests to expensive LLM APIs
- **Faster responses**: Cached responses return immediately
- **Offline capability**: Cached responses available when APIs are unavailable
- **File-based**: No external dependencies, works anywhere

### Best Practices

1. **Set appropriate expiration**: Balance freshness vs. cache hit rate
2. **Monitor cache size**: Use `MaxCacheSizeBytes` to prevent disk overflow
3. **Choose cache location**: Use fast storage (SSD) for better performance
4. **Regular cleanup**: Enable `CleanupOnStartup` for automatic maintenance

### Cache Hit Optimization

- Identical requests (same model, messages, parameters) will hit cache
- Minor differences in request will result in cache miss
- Consider normalizing requests before caching for better hit rates

## Testing

The system includes comprehensive tests covering:

- File storage operations (CRUD, cleanup, error handling)
- Cache configuration and validation
- Service registration and dependency injection
- Integration scenarios
- Environment variable parsing
- Cache statistics and management

Run tests:

```bash
dotnet test tests/Misc.Tests/Misc.Tests.csproj
```

## Integration with AchieveAI

When AchieveAI integrates with LmDotNet, you can add caching by:

1. **During LmDotNet setup**:
```csharp
services.AddLlmFileCache(options => {
    options.CacheDirectory = @"C:\AchieveAI\Cache";
    options.CacheExpiration = TimeSpan.FromHours(6);
});

// When registering LmDotNet providers
services.AddTransient<IOpenClient>(provider => {
    var client = new OpenClient(apiKey);
    var factory = provider.GetRequiredService<Func<IOpenClient, IOpenClient>>();
    return factory(client); // Returns cached version
});
```

2. **Decorator pattern**:
```csharp
// Register your LmDotNet client normally
services.AddTransient<IOpenClient, OpenClient>();

// Add caching
services.AddLlmFileCache(configuration);
services.DecorateOpenClientWithFileCache();
```

This ensures all LLM requests go through the caching layer automatically.

## Troubleshooting

### Common Issues

1. **Permission denied**: Ensure the process has write access to the cache directory
2. **Disk space**: Monitor cache size and set appropriate limits
3. **Cache misses**: Verify request serialization is consistent
4. **Performance**: Use SSD storage for cache directory

### Debugging

Enable logging to see cache operations:

```csharp
// Cache statistics
var stats = await serviceProvider.GetCacheStatisticsAsync();
Console.WriteLine(stats.ToString());

// Manual cache inspection
var kvStore = serviceProvider.GetRequiredService<FileKvStore>();
await foreach (var key in await kvStore.EnumerateKeysAsync())
{
    Console.WriteLine($"Cached key: {key}");
}
```

## Contributing

To contribute to the caching system:

1. Add tests for new functionality
2. Follow existing patterns for configuration
3. Ensure thread safety for concurrent access
4. Update documentation for new features

## License

This caching system is part of the AchieveAI LmDotNet project. 