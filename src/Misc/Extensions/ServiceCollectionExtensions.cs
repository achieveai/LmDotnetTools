using AchieveAi.LmDotnetTools.Misc.Clients;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.Misc.Extensions;

/// <summary>
/// Extension methods for registering LLM caching services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds LLM file-based caching services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="configuration">Configuration to read LLM cache options from</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure LlmCacheOptions from configuration
        services.Configure<LlmCacheOptions>(configuration.GetSection("LlmCache"));
        
        // Register FileKvStore as singleton for caching
        services.AddSingleton<FileKvStore>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<LlmCacheOptions>>().Value;
            return new FileKvStore(options.CacheDirectory);
        });

        return services;
    }

    /// <summary>
    /// Adds LLM file-based caching services to the service collection with explicit options.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="options">The LLM cache options to use</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCache(
        this IServiceCollection services,
        LlmCacheOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        // Validate options
        var validationErrors = options.Validate();
        if (validationErrors.Any())
        {
            throw new ArgumentException($"Invalid cache options: {string.Join(", ", validationErrors)}", nameof(options));
        }

        // Register options as singleton
        services.AddSingleton(options);
        
        // Register FileKvStore as singleton for caching
        services.AddSingleton<FileKvStore>(serviceProvider =>
        {
            var cacheOptions = serviceProvider.GetRequiredService<LlmCacheOptions>();
            return new FileKvStore(cacheOptions.CacheDirectory);
        });

        return services;
    }

    /// <summary>
    /// Adds LLM file-based caching services to the service collection with configuration action.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="configureOptions">Action to configure the LLM cache options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCache(
        this IServiceCollection services,
        Action<LlmCacheOptions> configureOptions)
    {
        if (configureOptions == null) throw new ArgumentNullException(nameof(configureOptions));

        var options = new LlmCacheOptions();
        configureOptions(options);

        return services.AddLlmFileCache(options);
    }

    /// <summary>
    /// Adds LLM file-based caching services with environment variable configuration.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLlmFileCacheFromEnvironment(
        this IServiceCollection services)
    {
        var options = LlmCacheOptions.FromEnvironment();
        return services.AddLlmFileCache(options);
    }

    /// <summary>
    /// Decorates an existing IOpenClient registration with file-based caching.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    /// <remarks>
    /// This method assumes that an IOpenClient and LLM caching services are already registered.
    /// It replaces the existing IOpenClient registration with a cached version.
    /// </remarks>
    public static IServiceCollection DecorateOpenClientWithFileCache(
        this IServiceCollection services)
    {
        // Find existing IOpenClient registration
        var existingDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IOpenClient));
        if (existingDescriptor == null)
        {
            throw new InvalidOperationException("No IOpenClient registration found. Register an IOpenClient before decorating with cache.");
        }

        // Remove existing registration
        services.Remove(existingDescriptor);

        // Register the cached version
        services.AddTransient<IOpenClient>(serviceProvider =>
        {
            // Create the original client using the existing descriptor
            IOpenClient innerClient;
            if (existingDescriptor.ImplementationFactory != null)
            {
                innerClient = (IOpenClient)existingDescriptor.ImplementationFactory(serviceProvider);
            }
            else if (existingDescriptor.ImplementationType != null)
            {
                innerClient = (IOpenClient)ActivatorUtilities.CreateInstance(serviceProvider, existingDescriptor.ImplementationType);
            }
            else if (existingDescriptor.ImplementationInstance != null)
            {
                innerClient = (IOpenClient)existingDescriptor.ImplementationInstance;
            }
            else
            {
                throw new InvalidOperationException("Unable to resolve IOpenClient from existing registration.");
            }

            // Get caching dependencies
            var kvStore = serviceProvider.GetRequiredService<FileKvStore>();
            var options = serviceProvider.GetService<LlmCacheOptions>() ?? 
                         serviceProvider.GetService<IOptions<LlmCacheOptions>>()?.Value ?? 
                         new LlmCacheOptions();

            // Return cached client
            return new FileCachingClient(innerClient, kvStore, options);
        });

        return services;
    }

    /// <summary>
    /// Creates a cached IOpenClient factory that can wrap any IOpenClient with file-based caching.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddCachedOpenClientFactory(
        this IServiceCollection services)
    {
        services.AddTransient<Func<IOpenClient, IOpenClient>>(serviceProvider =>
        {
            return innerClient =>
            {
                var kvStore = serviceProvider.GetRequiredService<FileKvStore>();
                var options = serviceProvider.GetService<LlmCacheOptions>() ?? 
                             serviceProvider.GetService<IOptions<LlmCacheOptions>>()?.Value ?? 
                             new LlmCacheOptions();

                return new FileCachingClient(innerClient, kvStore, options);
            };
        });

        return services;
    }

    /// <summary>
    /// Adds a complete LLM file caching setup with OpenAI client integration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="openAiApiKey">OpenAI API key</param>
    /// <param name="baseUrl">OpenAI base URL (optional)</param>
    /// <param name="cacheOptions">Cache configuration options (optional)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddCachedOpenAIClient(
        this IServiceCollection services,
        string openAiApiKey,
        string? baseUrl = null,
        LlmCacheOptions? cacheOptions = null)
    {
        if (string.IsNullOrEmpty(openAiApiKey))
            throw new ArgumentException("OpenAI API key cannot be null or empty", nameof(openAiApiKey));

        // Add caching services
        services.AddLlmFileCache(cacheOptions ?? new LlmCacheOptions());

        // Register OpenAI client
        services.AddTransient<IOpenClient>(serviceProvider =>
        {
            var client = new OpenClient(openAiApiKey, baseUrl ?? "https://api.openai.com/v1");
            
            // Wrap with caching
            var kvStore = serviceProvider.GetRequiredService<FileKvStore>();
            var options = serviceProvider.GetService<LlmCacheOptions>() ?? new LlmCacheOptions();
            
            return new FileCachingClient(client, kvStore, options);
        });

        return services;
    }

    /// <summary>
    /// Gets cache statistics from the registered cache store.
    /// </summary>
    /// <param name="serviceProvider">The service provider</param>
    /// <returns>A task that returns cache statistics</returns>
    public static async Task<CacheStatistics> GetCacheStatisticsAsync(this IServiceProvider serviceProvider)
    {
        var kvStore = serviceProvider.GetService<FileKvStore>();
        if (kvStore == null)
        {
            return new CacheStatistics { IsEnabled = false };
        }

        var options = serviceProvider.GetService<LlmCacheOptions>() ?? 
                     serviceProvider.GetService<IOptions<LlmCacheOptions>>()?.Value;

        var count = await kvStore.GetCountAsync();
        
        return new CacheStatistics
        {
            IsEnabled = options?.EnableCaching ?? true,
            CacheDirectory = kvStore.CacheDirectory,
            TotalItems = count,
            ConfiguredExpiration = options?.CacheExpiration,
            MaxItems = options?.MaxCacheItems,
            MaxSizeBytes = options?.MaxCacheSizeBytes
        };
    }

    /// <summary>
    /// Clears the LLM cache through the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the cache clear operation</returns>
    public static async Task ClearLlmCacheAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        var kvStore = serviceProvider.GetService<FileKvStore>();
        if (kvStore != null)
        {
            await kvStore.ClearAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Statistics about the LLM cache.
/// </summary>
public class CacheStatistics
{
    /// <summary>
    /// Whether caching is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The cache directory path.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Total number of cached items.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Configured cache expiration time.
    /// </summary>
    public TimeSpan? ConfiguredExpiration { get; set; }

    /// <summary>
    /// Maximum number of cached items allowed.
    /// </summary>
    public int? MaxItems { get; set; }

    /// <summary>
    /// Maximum cache size in bytes.
    /// </summary>
    public long? MaxSizeBytes { get; set; }

    /// <summary>
    /// Returns a string representation of the cache statistics.
    /// </summary>
    public override string ToString()
    {
        return $"Cache Statistics: Enabled={IsEnabled}, Directory='{CacheDirectory}', " +
               $"Items={TotalItems}, Expiration={ConfiguredExpiration}, " +
               $"MaxItems={MaxItems}, MaxSize={MaxSizeBytes} bytes";
    }
} 