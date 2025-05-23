using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using dotenv.net;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// Factory for creating OpenClient instances using environment variables from .env file.
/// </summary>
public static class OpenClientFactory
{
    private static bool _envLoaded = false;

    /// <summary>
    /// Creates an OpenClient instance using API key and base URL from .env file.
    /// </summary>
    /// <param name="envFilePath">Optional path to .env file. If not provided, will look in the project root.</param>
    /// <returns>An initialized OpenClient instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required environment variables are missing.</exception>
    public static IOpenClient Create(string? envFilePath = null)
    {
        LoadEnvIfNeeded(envFilePath);

        string? apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        string? baseUrl = Environment.GetEnvironmentVariable("LLM_API_BASE_URL");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("LLM_API_KEY environment variable is not set in .env file");
        }

        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException("LLM_API_BASE_URL environment variable is not set in .env file");
        }

        return new OpenClient(apiKey, baseUrl);
    }

    /// <summary>
    /// Creates a DatabasedClientWrapper instance using API key and base URL from .env file.
    /// The wrapper will use a test data file based on the test case name.
    /// </summary>
    /// <param name="testCaseName">Name of the test case, used to generate the test data file path.</param>
    /// <param name="envFilePath">Optional path to .env file. If not provided, will look in the project root.</param>
    /// <returns>An initialized DatabasedClientWrapper instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when required environment variables are missing.</exception>
    public static IOpenClient CreateDatabasedClient(string testCaseName, string? envFilePath = null, bool allowAdditionalRequests = false)
    {
        LoadEnvIfNeeded(envFilePath);

        // Create the inner client
        IOpenClient innerClient = Create(envFilePath);

        // Get the test directory from environment variables
        string? testDirectory = Environment.GetEnvironmentVariable("TEST_DIRECTORY");
        if (string.IsNullOrEmpty(testDirectory))
        {
            string workspaceRoot = TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory);
            testDirectory = Path.Combine(workspaceRoot, "tests", "OpenAIProvider.Tests", "TestData");
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

        // Generate a file path for the test data
        string testDataFilePath = Path.Combine(
          testDirectory,
          $"{testCaseName.Replace(" ", "_").Replace(".", "_")}.json"
        );

        return new DatabasedClientWrapper(innerClient, testDataFilePath, allowAdditionalRequests);
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
        string? apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        string? baseUrl = Environment.GetEnvironmentVariable("LLM_API_BASE_URL");

        Console.WriteLine($"After loading, LLM_API_KEY exists: {!string.IsNullOrEmpty(apiKey)}");
        Console.WriteLine($"After loading, LLM_API_BASE_URL: {baseUrl}");

        _envLoaded = true;
    }
}