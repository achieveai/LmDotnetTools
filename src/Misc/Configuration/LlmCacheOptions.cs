namespace AchieveAi.LmDotnetTools.Misc.Configuration;

/// <summary>
/// Configuration options for LLM request caching.
/// </summary>
public record LlmCacheOptions
{
    /// <summary>
    /// Gets the directory where cache files will be stored.
    /// Defaults to a subdirectory in the current working directory.
    /// </summary>
    public string CacheDirectory { get; init; } = GetDefaultCacheDirectory();

    /// <summary>
    /// Gets whether caching is enabled.
    /// Defaults to true.
    /// </summary>
    public bool EnableCaching { get; init; } = true;

    /// <summary>
    /// Gets the cache expiration time.
    /// If null, cached items never expire.
    /// Defaults to 24 hours.
    /// </summary>
    public TimeSpan? CacheExpiration { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets the maximum number of cached items.
    /// If null, there is no limit.
    /// Defaults to 10,000 items.
    /// </summary>
    public int? MaxCacheItems { get; init; } = 10_000;

    /// <summary>
    /// Gets the maximum size of the cache directory in bytes.
    /// If null, there is no size limit.
    /// Defaults to 1 GB.
    /// </summary>
    public long? MaxCacheSizeBytes { get; init; } = 1_073_741_824; // 1 GB

    /// <summary>
    /// Gets whether to clean up expired cache files on startup.
    /// Defaults to false to keep implementation simple.
    /// </summary>
    public bool CleanupOnStartup { get; init; } = false;

    /// <summary>
    /// Gets the default cache directory path.
    /// Uses current directory + "/LLM_CACHE" for simplicity.
    /// </summary>
    public static string GetDefaultCacheDirectory()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "LLM_CACHE");
    }

    /// <summary>
    /// Validates the configuration options.
    /// </summary>
    /// <returns>A list of validation errors, or an empty list if valid.</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(CacheDirectory))
        {
            errors.Add("CacheDirectory cannot be null or empty.");
        }
        else
        {
            try
            {
                // Try to get the full path to validate the directory path
                Path.GetFullPath(CacheDirectory);
            }
            catch (Exception ex)
            {
                errors.Add($"CacheDirectory is not a valid path: {ex.Message}");
            }
        }

        if (CacheExpiration.HasValue && CacheExpiration.Value <= TimeSpan.Zero)
        {
            errors.Add("CacheExpiration must be greater than zero.");
        }

        if (MaxCacheItems.HasValue && MaxCacheItems.Value <= 0)
        {
            errors.Add("MaxCacheItems must be greater than zero.");
        }

        if (MaxCacheSizeBytes.HasValue && MaxCacheSizeBytes.Value <= 0)
        {
            errors.Add("MaxCacheSizeBytes must be greater than zero.");
        }

        return errors;
    }

    /// <summary>
    /// Creates LlmCacheOptions from environment variables.
    /// Environment variables:
    /// - LLM_CACHE_DIRECTORY: Cache directory path (defaults to current directory + "/LLM_CACHE")
    /// - LLM_CACHE_ENABLED: Enable/disable caching (defaults to true)
    /// - LLM_CACHE_EXPIRATION_HOURS: Cache expiration in hours (defaults to 24)
    /// - LLM_CACHE_MAX_ITEMS: Maximum number of cached items (defaults to 10,000)
    /// - LLM_CACHE_MAX_SIZE_MB: Maximum cache size in megabytes (defaults to 1,024 MB)
    /// - LLM_CACHE_CLEANUP_ON_STARTUP: Cleanup on startup (defaults to false for simplicity)
    /// </summary>
    /// <returns>LlmCacheOptions configured from environment variables.</returns>
    public static LlmCacheOptions FromEnvironment()
    {
        var cacheDirectory = Environment.GetEnvironmentVariable("LLM_CACHE_DIRECTORY");
        var enableCaching = Environment.GetEnvironmentVariable("LLM_CACHE_ENABLED");
        var expirationHours = Environment.GetEnvironmentVariable("LLM_CACHE_EXPIRATION_HOURS");
        var maxItems = Environment.GetEnvironmentVariable("LLM_CACHE_MAX_ITEMS");
        var maxSizeMB = Environment.GetEnvironmentVariable("LLM_CACHE_MAX_SIZE_MB");
        var cleanupOnStartup = Environment.GetEnvironmentVariable("LLM_CACHE_CLEANUP_ON_STARTUP");

        return new LlmCacheOptions
        {
            CacheDirectory = !string.IsNullOrEmpty(cacheDirectory) ? cacheDirectory : GetDefaultCacheDirectory(),
            EnableCaching =
                !string.IsNullOrEmpty(enableCaching) && bool.TryParse(enableCaching, out var enabled) ? enabled : true,
            CacheExpiration =
                !string.IsNullOrEmpty(expirationHours) && double.TryParse(expirationHours, out var hours) && hours > 0
                    ? TimeSpan.FromHours(hours)
                    : TimeSpan.FromHours(24),
            MaxCacheItems =
                !string.IsNullOrEmpty(maxItems) && int.TryParse(maxItems, out var items) && items > 0 ? items : 10_000,
            MaxCacheSizeBytes =
                !string.IsNullOrEmpty(maxSizeMB) && long.TryParse(maxSizeMB, out var sizeMB) && sizeMB > 0
                    ? sizeMB * 1024 * 1024
                    : 1_073_741_824,
            CleanupOnStartup =
                !string.IsNullOrEmpty(cleanupOnStartup) && bool.TryParse(cleanupOnStartup, out var cleanup)
                    ? cleanup
                    : false,
        };
    }
}
