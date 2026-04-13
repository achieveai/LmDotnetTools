using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.TestUtils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

public class DataDrivenFunctionToolTests
{
    private const string ManualArtifactCreationEnvVar = "LM_ENABLE_MANUAL_ARTIFACT_CREATION";
    private const string TestBaseUrl = "http://test-mode/v1";
    private static readonly string[] fallbackKeys = ["LLM_API_KEY"];
    private static readonly string[] fallbackKeysArray = ["LLM_API_BASE_URL"];
    private readonly ProviderTestDataManager _testDataManager = new();

    private static string EnvTestPath =>
        Path.Combine(TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory), ".env.test");

    [Theory]
    [MemberData(nameof(GetFunctionToolTestCases))]
    public async Task FunctionTool_RequestAndResponseTransformation(string testName)
    {
        Debug.WriteLine($"Starting test for {testName}");

        // Arrange - Load data from test files
        var (messages, options) = _testDataManager.LoadLmCoreRequest(testName, ProviderType.OpenAI);
        messages = PrepareInstructionDrivenMessages(testName, messages);
        Debug.WriteLine(
            $"Loaded {messages.Length} messages and options with {options.Functions?.Length ?? 0} functions"
        );

        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var client = new OpenClient(httpClient, TestBaseUrl);
        var agent = new OpenClientAgent("TestAgent", client);
        Debug.WriteLine("Created agent with TestSseMessageHandler");

        // Act
        var response = await agent.GenerateReplyAsync(messages, options);
        Debug.WriteLine($"Generated response: {response?.GetType().Name}");

        // Assert deterministic semantic invariants for SSE-handler full-stack testing.
        Assert.NotNull(response);
        var list = response.ToList();
        Assert.NotEmpty(list);
        Assert.IsType<UsageMessage>(list.Last());
        var toolCalls = list.Take(list.Count - 1).GetAllToolCalls().ToList();
        Assert.NotEmpty(toolCalls);

        if (testName.Contains("MultiFunctionTool", StringComparison.Ordinal))
        {
            Assert.Contains(toolCalls, tc => tc.FunctionName == "python_mcp-list_directory" && tc.FunctionArgs == "{\"relative_path\":\".\"}");
            Assert.Contains(
                toolCalls,
                tc => tc.FunctionName == "python_mcp-get_directory_tree" && tc.FunctionArgs == "{\"relative_path\":\"code\"}"
            );
        }
        else
        {
            Assert.Contains(toolCalls, tc => tc.FunctionName == "getWeather" && tc.FunctionArgs == "{\"location\":\"San Francisco\"}");
        }

        Debug.WriteLine($"Test {testName} completed successfully");
    }

    private static IMessage[] PrepareInstructionDrivenMessages(string testName, IMessage[] messages)
    {
        var userIndex = Array.FindIndex(messages, m => m is TextMessage tm && tm.Role == Role.User);
        if (userIndex < 0 || messages[userIndex] is not TextMessage userMessage)
        {
            return messages;
        }

        var instruction = testName.Contains("MultiFunctionTool", StringComparison.Ordinal)
            ? """
              <|instruction_start|>{"instruction_chain":[{"id_message":"multi-tool","messages":[{"tool_call":[{"name":"python_mcp-list_directory","args":{"relative_path":"."}},{"name":"python_mcp-get_directory_tree","args":{"relative_path":"code"}}]}]}]}<|instruction_end|>
              """
            : """
              <|instruction_start|>{"instruction_chain":[{"id_message":"weather-tool","messages":[{"tool_call":[{"name":"getWeather","args":{"location":"San Francisco"}}]}]}]}<|instruction_end|>
              """;

        var rewritten = messages.ToArray();
        rewritten[userIndex] = userMessage with { Text = $"{userMessage.Text}\n{instruction}" };
        return rewritten;
    }

    /// <summary>
    ///     Gets all test cases from the TestData directory.
    /// </summary>
    public static IEnumerable<object[]> GetFunctionToolTestCases()
    {
        var testDataManager = new ProviderTestDataManager();
        return testDataManager
            .GetTestCaseNames(ProviderType.OpenAI)
            .Where(name => name.Contains("FunctionTool"))
            .Select(name => new object[] { name });
    }

    /// <summary>
    ///     Creates a test case data file. Run this method to generate test data.
    /// </summary>
    [Fact]
    public async Task CreateWeatherFunctionToolTestData()
    {
        if (!ManualArtifactCreationEnabled())
        {
            return;
        }

        // Skip if the test data already exists
        var testName = "WeatherFunctionTool";
        var testDataPath = _testDataManager.GetTestDataPath(testName, ProviderType.OpenAI, DataType.LmCoreRequest);

        if (File.Exists(testDataPath))
        {
            Debug.WriteLine($"Test data already exists at {testDataPath}. Skipping creation.");
            return;
        }

        // 1. LmCore request data - messages and options
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" },
        };

        var weatherFunction = new FunctionContract
        {
            Name = "getWeather",
            Description = "Get current weather for a location",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "location",
                    Description = "City name",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true,
                },
            ],
        };

        var options = new GenerateReplyOptions { ModelId = "gpt-4", Functions = [weatherFunction] };

        // Save LmCore request
        _testDataManager.SaveLmCoreRequest(testName, ProviderType.OpenAI, messages, options);

        // 2. Create client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath, true)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // 3. Generate response
        var response = await agent.GenerateReplyAsync(messages, options);

        // 4. Save final response
        _testDataManager.SaveFinalResponse(testName, ProviderType.OpenAI, response);
    }

    /// <summary>
    ///     Creates a multi-function test case data file. Run this method to generate test data.
    /// </summary>
    [Fact]
    public async Task CreateMultiFunctionToolTestData()
    {
        if (!ManualArtifactCreationEnabled())
        {
            return;
        }

        // Skip if the test data already exists
        var testName = "MultiFunctionTool";
        var testDataPath = _testDataManager.GetTestDataPath(testName, ProviderType.OpenAI, DataType.LmCoreRequest);

        if (File.Exists(testDataPath))
        {
            Debug.WriteLine($"Test data already exists at {testDataPath}. Skipping creation.");
            return;
        }

        // 1. LmCore request data - messages and options
        var messages = new[]
        {
            new TextMessage
            {
                Role = Role.System,
                Text = "You are a helpful assistant that can use tools to help users.",
            },
            new TextMessage { Role = Role.User, Text = "List files in root and \"code\" directories." },
        };

        // Create multiple function definitions
        var listDirectoryFunction = new FunctionContract
        {
            Name = "python_mcp-list_directory",
            Description = "List the contents of a directory within the code directory",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "relative_path",
                    Description = "Relative path within the code directory",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = false,
                },
            ],
        };

        var getDirTreeFunction = new FunctionContract
        {
            Name = "python_mcp-get_directory_tree",
            Description = "Get an ASCII tree representation of a directory structure",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "relative_path",
                    Description = "Relative path within the code directory",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = false,
                },
            ],
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4",
            MaxToken = 2000,
            Temperature = 0.7f,
            Functions = [listDirectoryFunction, getDirTreeFunction],
        };

        // Save LmCore request
        _testDataManager.SaveLmCoreRequest(testName, ProviderType.OpenAI, messages, options);

        // 2. Create client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath, true)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // 3. Generate response
        var response = await agent.GenerateReplyAsync(messages, options);

        // 4. Save final response
        _testDataManager.SaveFinalResponse(testName, ProviderType.OpenAI, response);
    }

    /// <summary>
    ///     Helper method to get API key from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiKeyFromEnv()
    {
        return EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackKeys);
    }

    /// <summary>
    ///     Helper method to get API base URL from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiBaseUrlFromEnv()
    {
        return EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeysArray);
    }

    private static bool ManualArtifactCreationEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(ManualArtifactCreationEnvVar),
            "1",
            StringComparison.Ordinal
        );
    }
}
