using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// Factory for creating client wrappers for different providers.
/// </summary>
public static class ClientWrapperFactory
{
    /// <summary>
    /// Creates a new OpenAI client wrapper for testing.
    /// </summary>
    /// <param name="innerClient">The inner OpenAI client to wrap.</param>
    /// <param name="testName">The name of the test to use for the test data file.</param>
    /// <param name="allowAdditionalRequests">If true, allows collecting additional requests when predefined ones are exhausted.</param>
    /// <returns>A wrapped OpenAI client.</returns>
    public static DatabasedClientWrapper CreateOpenAIClientWrapper(
        IOpenClient innerClient,
        string testName,
        bool allowAdditionalRequests = false)
    {
        string testDataPath = GetTestDataPath("OpenAI", testName);
        return new DatabasedClientWrapper(innerClient, testDataPath, allowAdditionalRequests);
    }

    /// <summary>
    /// Creates a new Anthropic client wrapper for testing.
    /// </summary>
    /// <param name="innerClient">The inner Anthropic client to wrap.</param>
    /// <param name="testName">The name of the test to use for the test data file.</param>
    /// <param name="allowAdditionalRequests">If true, allows collecting additional requests when predefined ones are exhausted.</param>
    /// <returns>A wrapped Anthropic client.</returns>
    public static AnthropicClientWrapper CreateAnthropicClientWrapper(
        IAnthropicClient innerClient,
        string testName,
        bool allowAdditionalRequests = false)
    {
        string testDataPath = GetTestDataPath("Anthropic", testName);
        return new AnthropicClientWrapper(innerClient, testDataPath, allowAdditionalRequests);
    }

    /// <summary>
    /// Gets the path to the test data file.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <param name="testName">The name of the test.</param>
    /// <returns>The path to the test data file.</returns>
    private static string GetTestDataPath(string provider, string testName)
    {
        string testDirectory = Environment.GetEnvironmentVariable("TEST_DIRECTORY") ?? "../TestData";
        if (!Directory.Exists(testDirectory))
        {
            Directory.CreateDirectory(testDirectory);
        }

        string providerDirectory = Path.Combine(testDirectory, provider);
        if (!Directory.Exists(providerDirectory))
        {
            Directory.CreateDirectory(providerDirectory);
        }

        return Path.Combine(providerDirectory, $"{testName}.json");
    }

    /// <summary>
    /// Loads an existing test data file as a JSON string.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <param name="testName">The name of the test.</param>
    /// <returns>The content of the test data file, or null if the file doesn't exist.</returns>
    public static string? LoadTestDataAsString(string provider, string testName)
    {
        string path = GetTestDataPath(provider, testName);
        if (File.Exists(path))
        {
            return File.ReadAllText(path);
        }
        return null;
    }

    /// <summary>
    /// Loads an existing test data file as a TestData object.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <param name="testName">The name of the test.</param>
    /// <returns>The TestData object, or null if the file doesn't exist.</returns>
    public static TestData? LoadTestData(string provider, string testName)
    {
        string? json = LoadTestDataAsString(provider, testName);
        if (json != null)
        {
            return JsonSerializer.Deserialize<TestData>(json, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        return null;
    }
} 