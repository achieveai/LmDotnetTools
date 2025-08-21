using dotenv.net;

namespace MemoryServer.Utils;

/// <summary>
/// Utility for loading environment variables from .env files
/// </summary>
public static class EnvironmentHelper
{
    private static bool _envLoaded = false;
    private static readonly object _lockObject = new object();

    /// <summary>
    /// Loads environment variables from .env file if not already loaded.
    /// Searches for .env file in current directory and parent directories.
    /// </summary>
    public static void LoadEnvIfNeeded()
    {
        lock (_lockObject)
        {
            if (_envLoaded)
            {
                return;
            }

            try
            {
                // Find workspace root by looking for .env file or solution file
                string? envPath = FindEnvFile();

                if (envPath != null)
                {
                    Console.WriteLine($"Loading environment variables from: {envPath}");
                    DotEnv.Load(options: new DotEnvOptions(
                        envFilePaths: new[] { envPath },
                        ignoreExceptions: false
                    ));
                    Console.WriteLine("Environment variables loaded successfully");
                }
                else
                {
                    Console.WriteLine("No .env file found in current directory or parent directories");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to load .env file: {ex.Message}");
            }

            _envLoaded = true;
        }
    }

    /// <summary>
    /// Finds the .env file by searching current directory and parent directories
    /// </summary>
    /// <returns>Path to .env file if found, null otherwise</returns>
    private static string? FindEnvFile()
    {
        var currentDir = Environment.CurrentDirectory;

        while (!string.IsNullOrEmpty(currentDir))
        {
            var envPath = Path.Combine(currentDir, ".env");
            if (File.Exists(envPath))
            {
                return envPath;
            }

            // Check for workspace indicators (solution file, .git directory)
            if (Directory.GetFiles(currentDir, "*.sln").Any() ||
                Directory.Exists(Path.Combine(currentDir, ".git")))
            {
                // We've found the workspace root, stop searching
                break;
            }

            // Move up one directory
            currentDir = Path.GetDirectoryName(currentDir);
        }

        return null;
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
}