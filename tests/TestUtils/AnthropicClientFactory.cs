using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using dotenv.net;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// Factory for creating AnthropicClient instances using environment variables from .env file.
/// </summary>
public static class AnthropicClientFactory
{
    private static bool _envLoaded = false;

    /// <summary>
    /// Creates an AnthropicClient instance using API key from .env file.
    /// </summary>
    /// <param name="envFilePath">Optional path to .env file. If not provided, will look in the project root.</param>
    /// <returns>An initialized AnthropicClient instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required environment variables are missing.</exception>
    public static IAnthropicClient Create(string? envFilePath = null)
    {
        LoadEnvIfNeeded(envFilePath);

        string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set in .env file");
        }

        return new AnthropicClient(apiKey);
    }

    /// <summary>
    /// Creates an AnthropicClientWrapper instance using API key from .env file.
    /// The wrapper will use a test data file based on the test case name.
    /// </summary>
    /// <param name="testCaseName">Name of the test case, used to generate the test data file path.</param>
    /// <param name="envFilePath">Optional path to .env file. If not provided, will look in the project root.</param>
    /// <returns>An initialized AnthropicClientWrapper instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required environment variables are missing.</exception>
    public static IAnthropicClient CreateDatabasedClient(string testCaseName, string? envFilePath = null, bool allowAdditionalRequests = false)
    {
        LoadEnvIfNeeded(envFilePath);

        // Create the inner client
        IAnthropicClient innerClient = Create(envFilePath);

        // Get the test directory from environment variables
        string? testDirectory = Environment.GetEnvironmentVariable("TEST_DIRECTORY");
        if (string.IsNullOrEmpty(testDirectory))
        {
            string workspaceRoot = TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory);
            testDirectory = Path.Combine(workspaceRoot, "tests", "AnthropicProvider.Tests", "TestData");
        }
        else if (!Path.IsPathRooted(testDirectory))
        {
            // If testDirectory is a relative path, make it relative to the workspace root
            string workspaceRoot = TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory);
            testDirectory = Path.Combine(workspaceRoot, testDirectory);
        }

        // Ensure the test directory exists
        if (!Directory.Exists(testDirectory))
        {
            Directory.CreateDirectory(testDirectory);
        }

        // Create the Anthropic-specific subdirectory
        string anthropicDirectory = Path.Combine(testDirectory, "Anthropic");
        if (!Directory.Exists(anthropicDirectory))
        {
            Directory.CreateDirectory(anthropicDirectory);
        }

        // Generate a file path for the test data using the Anthropic subdirectory
        string testDataFilePath = Path.Combine(
          anthropicDirectory,
          $"{testCaseName.Replace(" ", "_").Replace(".", "_")}.json"
        );

        return new AnthropicClientWrapper(innerClient, testDataFilePath, allowAdditionalRequests);
    }

    /// <summary>
    /// Loads environment variables from .env file if not already loaded.
    /// </summary>
    /// <param name="envFilePath">Optional path to .env file.</param>
    private static void LoadEnvIfNeeded(string? envFilePath = null)
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
                DotEnv.Load(options: new DotEnvOptions(
                  envFilePaths: new[] { envFilePath },
                  ignoreExceptions: false
                ));
            }
            else
            {
                Console.WriteLine($"WARNING: File does not exist at {envFilePath}");
            }
        }
        else
        {
            // Try to find the .env.test file in the workspace root
            string workspaceRoot = TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory);
            string workspaceEnvPath = Path.Combine(workspaceRoot, ".env.test");

            Console.WriteLine($"Loading environment variables from workspace root: {workspaceEnvPath}");
            if (File.Exists(workspaceEnvPath))
            {
                Console.WriteLine($"File exists at {workspaceEnvPath}");
                DotEnv.Load(options: new DotEnvOptions(
                  envFilePaths: new[] { workspaceEnvPath },
                  ignoreExceptions: false
                ));
            }
            else
            {
                Console.WriteLine($"WARNING: File does not exist at {workspaceEnvPath}");
            }
        }

        // Verify that the environment variables were loaded
        string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        Console.WriteLine($"After loading, ANTHROPIC_API_KEY exists: {!string.IsNullOrEmpty(apiKey)}");

        _envLoaded = true;
    }
}