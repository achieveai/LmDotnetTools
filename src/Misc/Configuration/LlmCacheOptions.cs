using System.ComponentModel.DataAnnotations;

namespace AchieveAi.LmDotnetTools.Misc.Configuration;

/// <summary>
/// Configuration options for LLM request caching.
/// </summary>
public class LlmCacheOptions
{
    /// <summary>
    /// Gets or sets the directory where cache files will be stored.
    /// Defaults to a subdirectory in the user's local application data folder.
    /// </summary>
    public string CacheDirectory { get; set; } = GetDefaultCacheDirectory();

    /// <summary>
    /// Gets or sets whether caching is enabled.
    /// Defaults to true.
    /// </summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>
    /// Gets or sets the cache expiration time.
    /// If null, cached items never expire.
    /// Defaults to 24 hours.
    /// </summary>
    public TimeSpan? CacheExpiration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the maximum number of cached items.
    /// If null, there is no limit.
    /// Defaults to 10,000 items.
    /// </summary>
    public int? MaxCacheItems { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the maximum size of the cache directory in bytes.
    /// If null, there is no size limit.
    /// Defaults to 1 GB.
    /// </summary>
    public long? MaxCacheSizeBytes { get; set; } = 1_073_741_824; // 1 GB

    /// <summary>
    /// Gets or sets whether to clean up expired cache files on startup.
    /// Defaults to true.
    /// </summary>
    public bool CleanupOnStartup { get; set; } = true;

    /// <summary>
    /// Gets the default cache directory path.
    /// </summary>
    public static string GetDefaultCacheDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AchieveAI", "LmDotNet", "Cache");
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
    /// Creates a copy of the current options with the specified overrides.
    /// </summary>
    /// <param name="configure">Action to configure the new options.</param>
    /// <returns>A new LlmCacheOptions instance with the specified changes.</returns>
    public LlmCacheOptions With(Action<LlmCacheOptions> configure)
    {
        var newOptions = new LlmCacheOptions
        {
            CacheDirectory = CacheDirectory,
            EnableCaching = EnableCaching,
            CacheExpiration = CacheExpiration,
            MaxCacheItems = MaxCacheItems,
            MaxCacheSizeBytes = MaxCacheSizeBytes,
            CleanupOnStartup = CleanupOnStartup
        };

        configure(newOptions);
        return newOptions;
    }

    /// <summary>
    /// Creates LlmCacheOptions from environment variables.
    /// Environment variables:
    /// - LLM_CACHE_DIRECTORY: Cache directory path
    /// - LLM_CACHE_ENABLED: Enable/disable caching (true/false)
    /// - LLM_CACHE_EXPIRATION_HOURS: Cache expiration in hours
    /// - LLM_CACHE_MAX_ITEMS: Maximum number of cached items
    /// - LLM_CACHE_MAX_SIZE_MB: Maximum cache size in megabytes
    /// - LLM_CACHE_CLEANUP_ON_STARTUP: Cleanup on startup (true/false)
    /// </summary>
    /// <returns>LlmCacheOptions configured from environment variables.</returns>
    public static LlmCacheOptions FromEnvironment()
    {
        var options = new LlmCacheOptions();

        var cacheDirectory = Environment.GetEnvironmentVariable("LLM_CACHE_DIRECTORY");
        if (!string.IsNullOrEmpty(cacheDirectory))
        {
            options.CacheDirectory = cacheDirectory;
        }

        var enableCaching = Environment.GetEnvironmentVariable("LLM_CACHE_ENABLED");
        if (!string.IsNullOrEmpty(enableCaching) && bool.TryParse(enableCaching, out var enabled))
        {
            options.EnableCaching = enabled;
        }

        var expirationHours = Environment.GetEnvironmentVariable("LLM_CACHE_EXPIRATION_HOURS");
        if (!string.IsNullOrEmpty(expirationHours) && double.TryParse(expirationHours, out var hours))
        {
            options.CacheExpiration = hours > 0 ? TimeSpan.FromHours(hours) : null;
        }

        var maxItems = Environment.GetEnvironmentVariable("LLM_CACHE_MAX_ITEMS");
        if (!string.IsNullOrEmpty(maxItems) && int.TryParse(maxItems, out var items))
        {
            options.MaxCacheItems = items > 0 ? items : null;
        }

        var maxSizeMB = Environment.GetEnvironmentVariable("LLM_CACHE_MAX_SIZE_MB");
        if (!string.IsNullOrEmpty(maxSizeMB) && long.TryParse(maxSizeMB, out var sizeMB))
        {
            options.MaxCacheSizeBytes = sizeMB > 0 ? sizeMB * 1024 * 1024 : null;
        }

        var cleanupOnStartup = Environment.GetEnvironmentVariable("LLM_CACHE_CLEANUP_ON_STARTUP");
        if (!string.IsNullOrEmpty(cleanupOnStartup) && bool.TryParse(cleanupOnStartup, out var cleanup))
        {
            options.CleanupOnStartup = cleanup;
        }

        return options;
    }

    /// <summary>
    /// Returns a string representation of the cache options.
    /// </summary>
    public override string ToString()
    {
        return $"LlmCacheOptions {{ " +
               $"CacheDirectory: '{CacheDirectory}', " +
               $"EnableCaching: {EnableCaching}, " +
               $"CacheExpiration: {CacheExpiration}, " +
               $"MaxCacheItems: {MaxCacheItems}, " +
               $"MaxCacheSizeBytes: {MaxCacheSizeBytes}, " +
               $"CleanupOnStartup: {CleanupOnStartup} }}";
    }
} 