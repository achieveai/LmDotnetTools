using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Configuration;

/// <summary>
/// Environment variable constants and configuration helpers for OpenAI Provider.
/// </summary>
public static class EnvironmentVariables
{
    #region OpenRouter Usage Middleware Environment Variables
    
    /// <summary>
    /// Enable/disable OpenRouter usage tracking middleware (default: true).
    /// </summary>
    public const string EnableUsageMiddleware = "ENABLE_USAGE_MIDDLEWARE";
    
    /// <summary>
    /// Enable/disable inline usage accounting in requests (default: true).
    /// </summary>
    public const string EnableInlineUsage = "ENABLE_INLINE_USAGE";
    
    /// <summary>
    /// Cache TTL in seconds for usage data (default: 300).
    /// </summary>
    public const string UsageCacheTtlSec = "USAGE_CACHE_TTL_SEC";
    
    /// <summary>
    /// OpenRouter API key for usage lookup (required when middleware enabled).
    /// </summary>
    public const string OpenRouterApiKey = "OPENROUTER_API_KEY";
    
    #endregion

    #region Configuration Reading Helpers
    
    /// <summary>
    /// Gets the EnableUsageMiddleware setting from configuration.
    /// </summary>
    /// <param name="configuration">Configuration instance</param>
    /// <returns>True if usage middleware should be enabled (default: true)</returns>
    public static bool GetEnableUsageMiddleware(IConfiguration configuration)
    {
        var value = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            EnableUsageMiddleware, null, "true");
        return bool.TryParse(value, out var result) ? result : true;
    }
    
    /// <summary>
    /// Gets the EnableInlineUsage setting from configuration.
    /// </summary>
    /// <param name="configuration">Configuration instance</param>
    /// <returns>True if inline usage should be enabled (default: true)</returns>
    public static bool GetEnableInlineUsage(IConfiguration configuration)
    {
        var value = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            EnableInlineUsage, null, "true");
        return bool.TryParse(value, out var result) ? result : true;
    }
    
    /// <summary>
    /// Gets the usage cache TTL in seconds from configuration.
    /// </summary>
    /// <param name="configuration">Configuration instance</param>
    /// <returns>Cache TTL in seconds (default: 300)</returns>
    public static int GetUsageCacheTtlSec(IConfiguration configuration)
    {
        var value = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            UsageCacheTtlSec, null, "300");
        return int.TryParse(value, out var result) && result > 0 ? result : 300;
    }
    
    /// <summary>
    /// Gets the OpenRouter API key from configuration.
    /// </summary>
    /// <param name="configuration">Configuration instance</param>
    /// <returns>OpenRouter API key or null if not found</returns>
    public static string? GetOpenRouterApiKey(IConfiguration configuration)
    {
        return EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            OpenRouterApiKey, null, "");
    }
    
    #endregion

    #region Validation and Fail-Fast Logic
    
    /// <summary>
    /// Validates that required configuration is present for OpenRouter usage middleware.
    /// </summary>
    /// <param name="configuration">Configuration instance</param>
    /// <exception cref="InvalidOperationException">Thrown when required configuration is missing</exception>
    public static void ValidateOpenRouterUsageConfiguration(IConfiguration configuration)
    {
        var enableUsageMiddleware = GetEnableUsageMiddleware(configuration);
        
        if (!enableUsageMiddleware)
        {
            // Middleware disabled, no validation needed
            return;
        }
        
        var openRouterApiKey = GetOpenRouterApiKey(configuration);
        
        if (string.IsNullOrWhiteSpace(openRouterApiKey))
        {
            throw new InvalidOperationException(
                $"OpenRouter usage middleware is enabled but {OpenRouterApiKey} environment variable is missing or empty. " +
                $"Either set the API key or disable the middleware by setting {EnableUsageMiddleware}=false.");
        }
    }
    
    /// <summary>
    /// Creates a configuration summary for logging/debugging purposes.
    /// </summary>
    /// <param name="configuration">Configuration instance</param>
    /// <returns>Configuration summary (API key masked for security)</returns>
    public static string GetConfigurationSummary(IConfiguration configuration)
    {
        var enableUsageMiddleware = GetEnableUsageMiddleware(configuration);
        var enableInlineUsage = GetEnableInlineUsage(configuration);
        var cacheTtlSec = GetUsageCacheTtlSec(configuration);
        var apiKeyPresent = !string.IsNullOrWhiteSpace(GetOpenRouterApiKey(configuration));
        
        return $"OpenRouter Usage Middleware Configuration: " +
               $"Enabled={enableUsageMiddleware}, " +
               $"InlineUsage={enableInlineUsage}, " +
               $"CacheTtl={cacheTtlSec}s, " +
               $"ApiKeyPresent={apiKeyPresent}";
    }
    
    #endregion
}

/// <summary>
/// Extension methods for dependency injection configuration.
/// </summary>
public static class EnvironmentVariablesServiceExtensions
{
    /// <summary>
    /// Validates OpenRouter usage configuration during startup.
    /// Call this during service registration to fail fast if configuration is invalid.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration instance</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection ValidateOpenRouterUsageConfiguration(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        EnvironmentVariables.ValidateOpenRouterUsageConfiguration(configuration);
        return services;
    }
    
    /// <summary>
    /// Adds OpenRouter usage configuration as a singleton service.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration instance</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddOpenRouterUsageConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register configuration values as singleton
        services.AddSingleton<IOpenRouterUsageConfiguration>(provider =>
            new OpenRouterUsageConfiguration(configuration));
            
        return services;
    }
}

/// <summary>
/// Interface for OpenRouter usage configuration.
/// </summary>
public interface IOpenRouterUsageConfiguration
{
    /// <summary>
    /// Whether usage middleware is enabled.
    /// </summary>
    bool EnableUsageMiddleware { get; }
    
    /// <summary>
    /// Whether inline usage accounting is enabled.
    /// </summary>
    bool EnableInlineUsage { get; }
    
    /// <summary>
    /// Cache TTL in seconds for usage data.
    /// </summary>
    int UsageCacheTtlSec { get; }
    
    /// <summary>
    /// OpenRouter API key for usage lookup.
    /// </summary>
    string? OpenRouterApiKey { get; }
}

/// <summary>
/// Implementation of OpenRouter usage configuration.
/// </summary>
public class OpenRouterUsageConfiguration : IOpenRouterUsageConfiguration
{
    public OpenRouterUsageConfiguration(IConfiguration configuration)
    {
        EnableUsageMiddleware = EnvironmentVariables.GetEnableUsageMiddleware(configuration);
        EnableInlineUsage = EnvironmentVariables.GetEnableInlineUsage(configuration);
        UsageCacheTtlSec = EnvironmentVariables.GetUsageCacheTtlSec(configuration);
        OpenRouterApiKey = EnvironmentVariables.GetOpenRouterApiKey(configuration);
    }
    
    public bool EnableUsageMiddleware { get; }
    public bool EnableInlineUsage { get; }
    public int UsageCacheTtlSec { get; }
    public string? OpenRouterApiKey { get; }
} 