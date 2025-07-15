using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Http;

namespace AchieveAi.LmDotnetTools.Misc.Extensions;

/// <summary>
/// Extension methods for registering LLM caching services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers LLM file caching services with the specified options.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="options">Cache configuration options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCache(this IServiceCollection services, LlmCacheOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        options.Validate();

        // Register the options
        services.AddSingleton(options);

        // Register the file-based cache store
        services.AddSingleton<IKvStore>(provider =>
        {
            var cacheOptions = provider.GetRequiredService<LlmCacheOptions>();
            return new FileKvStore(cacheOptions.CacheDirectory);
        });

        // Register factory for creating caching HttpClients
        services.AddSingleton<ICachingHttpClientFactory, CachingHttpClientFactory>();

        return services;
    }

    /// <summary>
    /// Registers LLM file caching services with configuration from IConfiguration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Configuration instance</param>
    /// <param name="configurationSection">Configuration section name (defaults to "LlmCache")</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCache(this IServiceCollection services, IConfiguration configuration, string configurationSection = "LlmCache")
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        var options = new LlmCacheOptions();
        configuration.GetSection(configurationSection).Bind(options);

        return services.AddLlmFileCache(options);
    }

    /// <summary>
    /// Registers LLM file caching services with configuration from action.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Action to configure cache options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCache(this IServiceCollection services, Action<LlmCacheOptions> configureOptions)
    {
        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions));

        var options = new LlmCacheOptions();
        configureOptions(options);

        return services.AddLlmFileCache(options);
    }

    /// <summary>
    /// Registers LLM file caching services with configuration from environment variables.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCacheFromEnvironment(this IServiceCollection services)
    {
        var options = LlmCacheOptions.FromEnvironment();
        return services.AddLlmFileCache(options);
    }

    /// <summary>
    /// Creates a caching HttpClient for OpenAI-compatible APIs.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="timeout">Optional timeout (defaults to 5 minutes)</param>
    /// <param name="headers">Optional additional headers</param>
    /// <returns>Configured HttpClient with caching</returns>
    public static HttpClient CreateCachingOpenAIClient(this IServiceCollection services, 
        string apiKey, 
        string baseUrl, 
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var serviceProvider = services.BuildServiceProvider();
        var cache = serviceProvider.GetRequiredService<IKvStore>();
        var options = serviceProvider.GetRequiredService<LlmCacheOptions>();
        var logger = serviceProvider.GetService<ILogger<CachingHttpMessageHandler>>();

        return Http.CachingHttpClientFactory.CreateForOpenAIWithCache(
            apiKey, baseUrl, cache, options, timeout, headers, logger);
    }

    /// <summary>
    /// Creates a caching HttpClient for Anthropic APIs.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="apiKey">The API key for authentication</param>
    /// <param name="baseUrl">The base URL for the API</param>
    /// <param name="timeout">Optional timeout (defaults to 5 minutes)</param>
    /// <param name="headers">Optional additional headers</param>
    /// <returns>Configured HttpClient with caching</returns>
    public static HttpClient CreateCachingAnthropicClient(this IServiceCollection services, 
        string apiKey, 
        string baseUrl, 
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var serviceProvider = services.BuildServiceProvider();
        var cache = serviceProvider.GetRequiredService<IKvStore>();
        var options = serviceProvider.GetRequiredService<LlmCacheOptions>();
        var logger = serviceProvider.GetService<ILogger<CachingHttpMessageHandler>>();

        return Http.CachingHttpClientFactory.CreateForAnthropicWithCache(
            apiKey, baseUrl, cache, options, timeout, headers, logger);
    }

    /// <summary>
    /// Wraps an existing HttpClient with caching capabilities.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="existingClient">The existing HttpClient to wrap</param>
    /// <returns>New HttpClient with caching capabilities</returns>
    public static HttpClient WrapWithCache(this IServiceCollection services, HttpClient existingClient)
    {
        var serviceProvider = services.BuildServiceProvider();
        var cache = serviceProvider.GetRequiredService<IKvStore>();
        var options = serviceProvider.GetRequiredService<LlmCacheOptions>();
        var logger = serviceProvider.GetService<ILogger<CachingHttpMessageHandler>>();

        return Http.CachingHttpClientFactory.WrapWithCache(existingClient, cache, options, logger);
    }

    /// <summary>
    /// Gets cache statistics for monitoring and diagnostics.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>Cache statistics</returns>
    public static async Task<CacheStatistics> GetCacheStatisticsAsync(this IServiceCollection services)
    {
        var serviceProvider = services.BuildServiceProvider();
        var cache = serviceProvider.GetRequiredService<IKvStore>();
        var options = serviceProvider.GetRequiredService<LlmCacheOptions>();

        var fileStore = cache as FileKvStore;
        if (fileStore != null)
        {
            var count = await fileStore.GetCountAsync();
            var directory = new DirectoryInfo(fileStore.CacheDirectory);
            var totalSize = directory.Exists ? directory.GetFiles().Sum(f => f.Length) : 0;

            return new CacheStatistics
            {
                ItemCount = count,
                TotalSizeBytes = totalSize,
                CacheDirectory = fileStore.CacheDirectory,
                IsEnabled = options.EnableCaching,
                MaxItems = options.MaxCacheItems ?? 0,
                MaxSizeBytes = options.MaxCacheSizeBytes ?? 0
            };
        }

        return new CacheStatistics
        {
            ItemCount = 0,
            TotalSizeBytes = 0,
            CacheDirectory = options.CacheDirectory,
            IsEnabled = options.EnableCaching,
            MaxItems = options.MaxCacheItems ?? 0,
            MaxSizeBytes = options.MaxCacheSizeBytes ?? 0
        };
    }

    /// <summary>
    /// Clears all cached items.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>Task representing the clear operation</returns>
    public static async Task ClearLlmCacheAsync(this IServiceCollection services)
    {
        var serviceProvider = services.BuildServiceProvider();
        var cache = serviceProvider.GetRequiredService<IKvStore>();

        var fileStore = cache as FileKvStore;
        if (fileStore != null)
        {
            await fileStore.ClearAsync();
        }
    }
}

/// <summary>
/// Interface for factory that creates caching HttpClients.
/// </summary>
public interface ICachingHttpClientFactory
{
    /// <summary>
    /// Creates an HttpClient with caching for OpenAI-compatible APIs.
    /// </summary>
    HttpClient CreateForOpenAI(string apiKey, string baseUrl, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? headers = null);

    /// <summary>
    /// Creates an HttpClient with caching for Anthropic APIs.
    /// </summary>
    HttpClient CreateForAnthropic(string apiKey, string baseUrl, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? headers = null);

    /// <summary>
    /// Wraps an existing HttpClient with caching.
    /// </summary>
    HttpClient WrapWithCache(HttpClient existingClient);
}

/// <summary>
/// Factory implementation for creating caching HttpClients.
/// </summary>
public class CachingHttpClientFactory : ICachingHttpClientFactory
{
    private readonly IKvStore _cache;
    private readonly LlmCacheOptions _options;
    private readonly ILogger<CachingHttpMessageHandler>? _logger;

    public CachingHttpClientFactory(IKvStore cache, LlmCacheOptions options, ILogger<CachingHttpMessageHandler>? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public HttpClient CreateForOpenAI(string apiKey, string baseUrl, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        return Http.CachingHttpClientFactory.CreateForOpenAIWithCache(apiKey, baseUrl, _cache, _options, timeout, headers, _logger);
    }

    public HttpClient CreateForAnthropic(string apiKey, string baseUrl, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        return Http.CachingHttpClientFactory.CreateForAnthropicWithCache(apiKey, baseUrl, _cache, _options, timeout, headers, _logger);
    }

    public HttpClient WrapWithCache(HttpClient existingClient)
    {
        return Http.CachingHttpClientFactory.WrapWithCache(existingClient, _cache, _options, _logger);
    }
}

/// <summary>
/// Statistics about the cache for monitoring and diagnostics.
/// </summary>
public class CacheStatistics
{
    /// <summary>
    /// Number of items currently in the cache.
    /// </summary>
    public int ItemCount { get; set; }

    /// <summary>
    /// Total size of cached items in bytes.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Cache directory path.
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Whether caching is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Maximum number of items allowed in cache.
    /// </summary>
    public int MaxItems { get; set; }

    /// <summary>
    /// Maximum cache size in bytes.
    /// </summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>
    /// Cache utilization as a percentage (0-100).
    /// </summary>
    public double ItemUtilizationPercent => MaxItems > 0 ? (double)ItemCount / MaxItems * 100 : 0;

    /// <summary>
    /// Size utilization as a percentage (0-100).
    /// </summary>
    public double SizeUtilizationPercent => MaxSizeBytes > 0 ? (double)TotalSizeBytes / MaxSizeBytes * 100 : 0;
} 