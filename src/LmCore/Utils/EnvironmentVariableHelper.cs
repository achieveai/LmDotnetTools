namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Utility for handling environment variables in production code.
/// </summary>
public static class EnvironmentVariableHelper
{
    /// <summary>
    /// Gets API key from environment variables with fallback options.
    /// </summary>
    /// <param name="primaryKey">Primary environment variable name</param>
    /// <param name="fallbackKeys">Fallback environment variable names</param>
    /// <param name="defaultValue">Default value if no keys found</param>
    /// <returns>API key value</returns>
    public static string GetApiKeyFromEnv(string primaryKey, string[]? fallbackKeys = null, string defaultValue = "")
    {
        var apiKey = Environment.GetEnvironmentVariable(primaryKey);
        if (!string.IsNullOrEmpty(apiKey))
        {
            return apiKey;
        }

        if (fallbackKeys != null)
        {
            foreach (var fallbackKey in fallbackKeys)
            {
                apiKey = Environment.GetEnvironmentVariable(fallbackKey);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    return apiKey;
                }
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Gets API base URL from environment variables with fallback options.
    /// </summary>
    /// <param name="primaryKey">Primary environment variable name</param>
    /// <param name="fallbackKeys">Fallback environment variable names</param>
    /// <param name="defaultValue">Default value if no keys found</param>
    /// <returns>API base URL value</returns>
    public static string GetApiBaseUrlFromEnv(
        string primaryKey,
        string[]? fallbackKeys = null,
        string defaultValue = "https://api.openai.com/v1"
    )
    {
        var baseUrl = Environment.GetEnvironmentVariable(primaryKey);
        if (!string.IsNullOrEmpty(baseUrl))
        {
            return baseUrl;
        }

        if (fallbackKeys != null)
        {
            foreach (var fallbackKey in fallbackKeys)
            {
                baseUrl = Environment.GetEnvironmentVariable(fallbackKey);
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    return baseUrl;
                }
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// Gets environment variable value with fallback options.
    /// </summary>
    /// <param name="primaryKey">Primary environment variable name</param>
    /// <param name="fallbackKeys">Fallback environment variable names</param>
    /// <param name="defaultValue">Default value if no keys found</param>
    /// <returns>Environment variable value</returns>
    public static string GetEnvironmentVariableWithFallback(
        string primaryKey,
        string[]? fallbackKeys = null,
        string defaultValue = ""
    )
    {
        var value = Environment.GetEnvironmentVariable(primaryKey);
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (fallbackKeys != null)
        {
            foreach (var fallbackKey in fallbackKeys)
            {
                value = Environment.GetEnvironmentVariable(fallbackKey);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
        }

        return defaultValue;
    }
}
