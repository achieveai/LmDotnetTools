using System.Linq;
using AchieveAi.LmDotnetTools.LmConfig.Http;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Http;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
    public static IServiceCollection AddLlmFileCache(
        this IServiceCollection services,
        LlmCacheOptions options
    )
    {
        // Guard against duplicate registration – if cache options have already been added we assume the
        // cache pipeline has been wired up previously and simply return the collection unchanged.
        if (services.Any(sd => sd.ServiceType == typeof(LlmCacheOptions)))
            return services;

        ArgumentNullException.ThrowIfNull(options);

        var validationErrors = options.Validate();
        if (validationErrors.Count > 0)
        {
            throw new ArgumentException(
                $"Invalid cache options: {string.Join(", ", validationErrors)}",
                nameof(options)
            );
        }

        // Register the options (idempotent)
        services.TryAddSingleton(options);

        // Register the file-based cache store (idempotent)
        services.TryAddSingleton<FileKvStore>(provider =>
        {
            var cacheOptions = provider.GetRequiredService<LlmCacheOptions>();
            return new FileKvStore(cacheOptions.CacheDirectory);
        });

        // Register IKvStore as alias to FileKvStore (idempotent)
        services.TryAddSingleton<IKvStore>(provider => provider.GetRequiredService<FileKvStore>());

        // Ensure a single IHttpHandlerBuilder and attach the cache wrapper without building a temporary provider
        var builderDescriptor = services.FirstOrDefault(sd =>
            sd.ServiceType == typeof(IHttpHandlerBuilder)
        );

        if (builderDescriptor == null)
        {
            // No builder yet – register a new one with the cache wrapper pre-attached
            services.AddSingleton<IHttpHandlerBuilder>(sp =>
            {
                var hb = new HandlerBuilder();
                var store = sp.GetRequiredService<IKvStore>();
                var opts = sp.GetRequiredService<LlmCacheOptions>();
                hb.Use(StandardWrappers.WithKvCache(store, opts));
                return hb;
            });
        }
        else
        {
            // Builder already registered – replace the descriptor with one that adds our wrapper lazily
            services.Remove(builderDescriptor);

            services.AddSingleton<IHttpHandlerBuilder>(sp =>
            {
                var innerBuilder =
                    (builderDescriptor.ImplementationInstance as HandlerBuilder)
                    ?? (builderDescriptor.ImplementationFactory?.Invoke(sp) as HandlerBuilder)
                    ?? new HandlerBuilder();

                var store = sp.GetRequiredService<IKvStore>();
                var opts = sp.GetRequiredService<LlmCacheOptions>();
                innerBuilder.Use(StandardWrappers.WithKvCache(store, opts));
                return innerBuilder;
            });
        }

        // Legacy CachingHttpClientFactory registration removed – caching is now handled through the injected IHttpHandlerBuilder pipeline.

        return services;
    }

    /// <summary>
    /// Registers LLM file caching services with configuration from IConfiguration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Configuration instance</param>
    /// <param name="configurationSection">Configuration section name (defaults to "LlmCache")</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCache(
        this IServiceCollection services,
        IConfiguration configuration,
        string configurationSection = "LlmCache"
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(configurationSection);
        var options = new LlmCacheOptions
        {
            CacheDirectory =
                section.GetValue<string>("CacheDirectory")
                ?? LlmCacheOptions.GetDefaultCacheDirectory(),
            EnableCaching = section.GetValue<bool>("EnableCaching", true),
            CacheExpiration =
                section.GetValue<int?>("CacheExpirationHours") is int hours && hours > 0
                    ? TimeSpan.FromHours(hours)
                    : TimeSpan.FromHours(24),
            MaxCacheItems =
                section.GetValue<int?>("MaxCacheItems") is int items && items > 0 ? items : 10_000,
            MaxCacheSizeBytes =
                section.GetValue<long?>("MaxCacheSizeBytes") is long bytes && bytes > 0
                    ? bytes
                    : 1_073_741_824,
            CleanupOnStartup = section.GetValue<bool>("CleanupOnStartup", false),
        };

        return services.AddLlmFileCache(options);
    }

    /// <summary>
    /// Registers LLM file caching services with configuration from environment variables.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCacheFromEnvironment(
        this IServiceCollection services
    )
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
    public static HttpClient CreateCachingOpenAIClient(
        this IServiceCollection services,
        string apiKey,
        string baseUrl,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null
    )
    {
        var sp = services.BuildServiceProvider();
        var handlerBuilder = sp.GetRequiredService<IHttpHandlerBuilder>();
        var logger = sp.GetService<ILogger<CachingHttpMessageHandler>>();

        return AchieveAi.LmDotnetTools.LmConfig.Http.HttpClientFactory.Create(
            new AchieveAi.LmDotnetTools.LmConfig.Http.ProviderConfig(
                apiKey,
                baseUrl,
                AchieveAi.LmDotnetTools.LmConfig.Http.ProviderType.OpenAI
            ),
            handlerBuilder,
            timeout,
            headers,
            logger
        );
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
    public static HttpClient CreateCachingAnthropicClient(
        this IServiceCollection services,
        string apiKey,
        string baseUrl,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? headers = null
    )
    {
        var sp = services.BuildServiceProvider();
        var handlerBuilder = sp.GetRequiredService<IHttpHandlerBuilder>();
        var logger = sp.GetService<ILogger<CachingHttpMessageHandler>>();

        return AchieveAi.LmDotnetTools.LmConfig.Http.HttpClientFactory.Create(
            new AchieveAi.LmDotnetTools.LmConfig.Http.ProviderConfig(
                apiKey,
                baseUrl,
                AchieveAi.LmDotnetTools.LmConfig.Http.ProviderType.Anthropic
            ),
            handlerBuilder,
            timeout,
            headers,
            logger
        );
    }

    /// <summary>
    /// Wraps an existing HttpClient with caching capabilities.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="existingClient">The existing HttpClient to wrap</param>
    /// <returns>New HttpClient with caching capabilities</returns>
    public static HttpClient WrapWithCache(
        this IServiceCollection services,
        HttpClient existingClient
    )
    {
        ArgumentNullException.ThrowIfNull(existingClient);

        var sp = services.BuildServiceProvider();
        var handlerBuilder = sp.GetRequiredService<IHttpHandlerBuilder>();
        var logger = sp.GetService<ILogger<CachingHttpMessageHandler>>();

        var newClient = AchieveAi.LmDotnetTools.LmConfig.Http.HttpClientFactory.Create(
            provider: null,
            pipeline: handlerBuilder,
            timeout: existingClient.Timeout,
            headers: null,
            logger: logger
        );

        newClient.BaseAddress = existingClient.BaseAddress;
        foreach (var h in existingClient.DefaultRequestHeaders)
            newClient.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);

        return newClient;
    }

    /// <summary>
    /// Gets cache statistics for monitoring and diagnostics.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>Cache statistics</returns>
    public static async Task<CacheStatistics> GetCacheStatisticsAsync(
        this IServiceCollection services
    )
    {
        var serviceProvider = services.BuildServiceProvider();
        var cache = serviceProvider.GetService<IKvStore>();
        var options = serviceProvider.GetService<LlmCacheOptions>();

        if (cache == null || options == null)
        {
            return new CacheStatistics
            {
                ItemCount = 0,
                TotalSizeBytes = 0,
                CacheDirectory = string.Empty,
                IsEnabled = false,
                MaxItems = 0,
                MaxSizeBytes = 0,
            };
        }

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
                MaxSizeBytes = options.MaxCacheSizeBytes ?? 0,
            };
        }

        return new CacheStatistics
        {
            ItemCount = 0,
            TotalSizeBytes = 0,
            CacheDirectory = options.CacheDirectory,
            IsEnabled = options.EnableCaching,
            MaxItems = options.MaxCacheItems ?? 0,
            MaxSizeBytes = options.MaxCacheSizeBytes ?? 0,
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
        var cache = serviceProvider.GetService<IKvStore>();

        if (cache == null)
        {
            return; // No cache configured, nothing to clear
        }

        var fileStore = cache as FileKvStore;
        if (fileStore != null)
        {
            await fileStore.ClearAsync();
        }
    }
}

// NOTE: Legacy ICachingHttpClientFactory interface and its implementation have been removed in favour of the IHttpHandlerBuilder-driven pipeline approach.
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
    public double SizeUtilizationPercent =>
        MaxSizeBytes > 0 ? (double)TotalSizeBytes / MaxSizeBytes * 100 : 0;
}
