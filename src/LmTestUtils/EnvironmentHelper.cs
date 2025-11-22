using dotenv.net;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Shared utility for loading environment variables in test scenarios
/// </summary>
public static class EnvironmentHelper
{
    private static bool _envLoaded = false;
    private static readonly object _lockObject = new object();

    /// <summary>
    /// Loads environment variables from .env file if not already loaded.
    /// </summary>
    /// <param name="envFilePath">Optional path to .env file.</param>
    public static void LoadEnvIfNeeded(string? envFilePath = null)
    {
        lock (_lockObject)
        {
            if (_envLoaded)
            {
                return;
            }

            if (envFilePath != null)
            {
                Console.WriteLine($"Loading environment variables from specified path: {envFilePath}");
                if (File.Exists(envFilePath))
                {
                    Console.WriteLine($"File exists at {envFilePath}");
                    DotEnv.Load(
                        options: new DotEnvOptions(envFilePaths: new[] { envFilePath }, ignoreExceptions: false)
                    );
                }
                else
                {
                    Console.WriteLine($"WARNING: File does not exist at {envFilePath}");
                }
            }
            else
            {
                // Try to find the .env.test file in the workspace root
                var workspaceRoot = FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory);
                var workspaceEnvPath = Path.Combine(workspaceRoot, ".env.test");

                Console.WriteLine($"Loading environment variables from workspace root: {workspaceEnvPath}");
                if (File.Exists(workspaceEnvPath))
                {
                    Console.WriteLine($"File exists at {workspaceEnvPath}");
                    DotEnv.Load(
                        options: new DotEnvOptions(envFilePaths: new[] { workspaceEnvPath }, ignoreExceptions: false)
                    );
                }
                else
                {
                    Console.WriteLine($"WARNING: File does not exist at {workspaceEnvPath}");
                }
            }

            _envLoaded = true;
        }
    }

    /// <summary>
    /// Gets API key from environment variables with fallback options
    /// </summary>
    /// <param name="primaryKey">Primary environment variable name</param>
    /// <param name="fallbackKeys">Fallback environment variable names</param>
    /// <param name="defaultValue">Default value if no keys found</param>
    /// <returns>API key value</returns>
    public static string GetApiKeyFromEnv(
        string primaryKey,
        string[]? fallbackKeys = null,
        string defaultValue = "test-api-key"
    )
    {
        LoadEnvIfNeeded();

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
    /// Gets API base URL from environment variables with fallback options
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
        LoadEnvIfNeeded();

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
    /// Resets the environment loading state (useful for testing)
    /// </summary>
    public static void ResetEnvironmentState()
    {
        lock (_lockObject)
        {
            _envLoaded = false;
        }
    }

    /// <summary>
    /// Finds the workspace root directory by looking for solution files
    /// </summary>
    /// <param name="startPath">Starting directory path</param>
    /// <returns>Workspace root directory path</returns>
    public static string FindWorkspaceRoot(string startPath)
    {
        var currentDir = new DirectoryInfo(startPath);

        while (currentDir != null)
        {
            // Look for solution files or other workspace indicators
            if (
                currentDir.GetFiles("*.sln").Length > 0
                || currentDir.GetDirectories(".git").Length > 0
                || currentDir.GetFiles(".env.test").Length > 0
            )
            {
                return currentDir.FullName;
            }

            currentDir = currentDir.Parent;
        }

        // Fallback to the starting path if no workspace root found
        return startPath;
    }
}
