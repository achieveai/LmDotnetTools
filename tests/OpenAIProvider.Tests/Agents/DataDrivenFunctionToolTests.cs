using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.TestUtils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

public class DataDrivenFunctionToolTests
{
    private readonly ProviderTestDataManager _testDataManager = new();
    private static string EnvTestPath =>
        Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            ".env.test"
        );

    private static readonly string[] fallbackKeys = ["LLM_API_KEY"];
    private static readonly string[] fallbackKeysArray = ["LLM_API_BASE_URL"];

    [Theory]
    [MemberData(nameof(GetFunctionToolTestCases))]
    public async Task FunctionTool_RequestAndResponseTransformation(string testName)
    {
        Debug.WriteLine($"Starting test for {testName}");

        // Arrange - Load data from test files
        var (messages, options) = _testDataManager.LoadLmCoreRequest(testName, ProviderType.OpenAI);
        Debug.WriteLine(
            $"Loaded {messages.Length} messages and options with {options.Functions?.Length ?? 0} functions"
        );

        // Create HTTP client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "OpenAI",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath, allowAdditional: false)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);
        Debug.WriteLine("Created agent with MockHttpHandlerBuilder record/playback");

        // Act
        var response = await agent.GenerateReplyAsync(messages, options);
        Debug.WriteLine($"Generated response: {response?.GetType().Name}");

        // Assert - Compare with expected response
        var expectedResponses = _testDataManager.LoadFinalResponse(testName, ProviderType.OpenAI);

        Debug.WriteLine($"Response count: {response?.Count() ?? 0}, Expected count: {expectedResponses?.Count ?? 0}");
        Debug.WriteLine(
            $"Response types: {string.Join(", ", response?.Select(r => r.GetType().Name) ?? [])}"
        );
        Debug.WriteLine(
            $"Expected types: {string.Join(", ", expectedResponses?.Select(r => r.GetType().Name) ?? [])}"
        );

        Assert.NotNull(response);

        if (expectedResponses == null)
        {
            // No expected data exists yet, skip comparison
            return;
        }

        // The expected count in the test files is 2, but the actual response now has 3 items due to the UsageMessage
        // Modify the assertion to expect 3 items instead of 2
        Assert.Equal(expectedResponses.Count + 1, response.Count());

        // Match the first two messages from the response with the expected messages
        var responseToTest = response.Take(expectedResponses.Count).ToList();
        foreach (var (expectedResponse, responseItem) in expectedResponses.Zip(responseToTest))
        {
            if (expectedResponse is TextMessage expectedTextResponse)
            {
                _ = Assert.IsType<TextMessage>(responseItem);
                Assert.Equal(expectedTextResponse.Text, ((TextMessage)responseItem).Text);
                Assert.Equal(expectedTextResponse.Role, responseItem.Role);
            }
            else if (expectedResponse is ToolsCallAggregateMessage expectedToolsCallAggregateMessage)
            {
                _ = Assert.IsType<ToolsCallAggregateMessage>(responseItem);
                var toolsCallAggregateMessage = (ToolsCallAggregateMessage)responseItem;
                Assert.Equal(expectedToolsCallAggregateMessage.Role, toolsCallAggregateMessage.Role);
                Assert.Equal(expectedToolsCallAggregateMessage.FromAgent, toolsCallAggregateMessage.FromAgent);
                Assert.Equal(
                    expectedToolsCallAggregateMessage.ToolsCallMessage!.GetToolCalls()!.Count(),
                    toolsCallAggregateMessage.ToolsCallMessage!.GetToolCalls()!.Count()
                );
                foreach (
                    var (expectedToolCall, toolCall) in expectedToolsCallAggregateMessage
                        .ToolsCallMessage!.GetToolCalls()!
                        .Zip(toolsCallAggregateMessage.ToolsCallMessage!.GetToolCalls()!)
                )
                {
                    Assert.Equal(expectedToolCall.FunctionName, toolCall.FunctionName);
                    Assert.Equal(expectedToolCall.FunctionArgs, toolCall.FunctionArgs);
                }
            }
        }

        // Verify that the last message is a UsageMessage
        var lastMessage = response.Last();
        _ = Assert.IsType<UsageMessage>(lastMessage);

        Debug.WriteLine($"Test {testName} completed successfully");
    }

    /// <summary>
    /// Gets all test cases from the TestData directory.
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
    /// Creates a test case data file. Run this method to generate test data.
    /// </summary>
    [Fact]
    public async Task CreateWeatherFunctionToolTestData()
    {
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
                new() {
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
            .WithRecordPlayback(testDataFilePath, allowAdditional: true)
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
    /// Creates a multi-function test case data file. Run this method to generate test data.
    /// </summary>
    [Fact]
    public async Task CreateMultiFunctionToolTestData()
    {
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
                new() {
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
                new() {
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
            .WithRecordPlayback(testDataFilePath, allowAdditional: true)
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
    /// Helper method to get API key from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiKeyFromEnv()
    {
        return EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackKeys, "test-api-key");
    }

    /// <summary>
    /// Helper method to get API base URL from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiBaseUrlFromEnv()
    {
        return EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeysArray, "https://api.openai.com/v1");
    }
}
