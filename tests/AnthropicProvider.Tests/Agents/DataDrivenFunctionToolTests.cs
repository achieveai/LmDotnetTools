using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

public class DataDrivenFunctionToolTests : LoggingTestBase
{
    private const string ManualArtifactCreationEnvVar = "LM_ENABLE_MANUAL_ARTIFACT_CREATION";
    private readonly ProviderTestDataManager _testDataManager = new();

    public DataDrivenFunctionToolTests(ITestOutputHelper output) : base(output) { }

    private static string EnvTestPath =>
        Path.Combine(TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory), ".env.test");

    [Theory]
    [MemberData(nameof(GetFunctionToolTestCases))]
    [InlineData("ToolCallResultTool")]
    public async Task FunctionTool_RequestAndResponseTransformation(string testName)
    {
        Logger.LogTrace("Starting test for {TestName}", testName);

        // Arrange - Load data from test files
        var (messages, options) = _testDataManager.LoadLmCoreRequest(testName, ProviderType.Anthropic);

        Logger.LogTrace(
            "Loaded {MessageCount} messages and options with {FunctionCount} functions",
            messages.Length,
            options.Functions?.Length ?? 0);

        messages = PrepareInstructionDrivenMessages(testName, messages, options);

        // Execute via deterministic SSE test-mode handler for offline full-stack testing.
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(chunkDelayMs: 0);
        var client = new AnthropicClient(GetApiKeyFromEnv(), httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", client);
        Logger.LogTrace("Created agent with AnthropicTestSseMessageHandler");

        // Act
        var response = await agent.GenerateReplyAsync(messages, options);
        Logger.LogTrace("Generated response: {ResponseType}", response?.GetType().Name);

        Assert.NotNull(response);
        var result = response!.ToList();
        Assert.NotEmpty(result);
        Assert.IsType<UsageMessage>(result.Last());

        var responseWithoutUsage = result.Take(result.Count - 1).ToList();
        var toolCalls = responseWithoutUsage.GetAllToolCalls().ToList();
        if (testName.Contains("MultiFunctionTool", StringComparison.Ordinal))
        {
            Assert.Equal(2, toolCalls.Count);
            Assert.Contains(
                toolCalls,
                tc =>
                    tc.FunctionName == "python_mcp-list_directory" && tc.FunctionArgs == "{\"relative_path\":\".\"}"
            );
            Assert.Contains(
                toolCalls,
                tc =>
                    tc.FunctionName == "python_mcp-get_directory_tree"
                    && tc.FunctionArgs == "{\"relative_path\":\"code\"}"
            );
        }
        else if (testName.Contains("WeatherFunctionTool", StringComparison.Ordinal))
        {
            Assert.Single(toolCalls);
            Assert.Contains(
                toolCalls,
                tc => tc.FunctionName == "getWeather" && tc.FunctionArgs == "{\"location\":\"San Francisco\"}"
            );
        }
        else if (options.Functions is { Length: > 0 })
        {
            Assert.Single(toolCalls);
            var expectedFunction = options.Functions[0].Name;
            Assert.Contains(toolCalls, tc => tc.FunctionName == expectedFunction && tc.FunctionArgs == "{}");
        }
        else
        {
            Assert.Contains(responseWithoutUsage, m => m is TextMessage);
        }

        Logger.LogTrace("Test {TestName} completed successfully with {ToolCallCount} tool calls", testName, toolCalls.Count);
    }

    private static IMessage[] PrepareInstructionDrivenMessages(
        string testName,
        IMessage[] messages,
        GenerateReplyOptions options
    )
    {
        var userIndex = Array.FindLastIndex(messages, m => m is TextMessage tm && tm.Role == Role.User);
        if (userIndex < 0 || messages[userIndex] is not TextMessage userMessage)
        {
            return messages;
        }

        string instruction;
        if (testName.Contains("MultiFunctionTool", StringComparison.Ordinal))
        {
            instruction =
                """
                <|instruction_start|>{"instruction_chain":[{"id_message":"multi-tool","messages":[{"tool_call":[{"name":"python_mcp-list_directory","args":{"relative_path":"."}},{"name":"python_mcp-get_directory_tree","args":{"relative_path":"code"}}]}]}]}<|instruction_end|>
                """;
        }
        else if (testName.Contains("WeatherFunctionTool", StringComparison.Ordinal))
        {
            instruction =
                """
                <|instruction_start|>{"instruction_chain":[{"id_message":"weather-tool","messages":[{"tool_call":[{"name":"getWeather","args":{"location":"San Francisco"}}]}]}]}<|instruction_end|>
                """;
        }
        else if (options.Functions is { Length: > 0 })
        {
            var functionName = options.Functions[0].Name;
            instruction =
                $"<|instruction_start|>{{\"instruction_chain\":[{{\"id_message\":\"tool\",\"messages\":[{{\"tool_call\":[{{\"name\":\"{functionName}\",\"args\":{{}}}}]}}]}}]}}<|instruction_end|>";
        }
        else
        {
            // ToolCallResultTool-style input: no function definitions, expect summarized text response.
            instruction =
                """
                <|instruction_start|>{"instruction_chain":[{"id_message":"tool-result-summary","messages":[{"text_message":{"length":60}}]}]}<|instruction_end|>
                """;
        }

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
            .GetTestCaseNames(ProviderType.Anthropic)
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
        var testDataPath = _testDataManager.GetTestDataPath(testName, ProviderType.Anthropic, DataType.LmCoreRequest);

        if (File.Exists(testDataPath))
        {
            Logger.LogTrace("Test data already exists at {TestDataPath}. Skipping creation.", testDataPath);
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

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            Functions = [weatherFunction],
        };

        // Save LmCore request
        _testDataManager.SaveLmCoreRequest(testName, ProviderType.Anthropic, messages, options);

        // 2. Create client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "Anthropic",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath, true)
            .ForwardToApi("https://api.anthropic.com/v1", GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(GetApiKeyFromEnv(), httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", client);

        // 3. Generate response
        var response = await agent.GenerateReplyAsync(messages, options);

        // 4. Save final response
        _testDataManager.SaveFinalResponse(testName, ProviderType.Anthropic, response);
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
        var testDataPath = _testDataManager.GetTestDataPath(testName, ProviderType.Anthropic, DataType.LmCoreRequest);

        if (File.Exists(testDataPath))
        {
            Logger.LogTrace("Test data already exists at {TestDataPath}. Skipping creation.", testDataPath);
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
            ModelId = "claude-3-7-sonnet-20250219",
            MaxToken = 2000,
            Temperature = 0.7f,
            Functions = [listDirectoryFunction, getDirTreeFunction],
        };

        // Save LmCore request
        _testDataManager.SaveLmCoreRequest(testName, ProviderType.Anthropic, messages, options);

        // 2. Create client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "Anthropic",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath, true)
            .ForwardToApi("https://api.anthropic.com/v1", GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient(GetApiKeyFromEnv(), httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", client);

        // 3. Generate response
        var response = await agent.GenerateReplyAsync(messages, options);

        // 4. Save final response
        _testDataManager.SaveFinalResponse(testName, ProviderType.Anthropic, response);
    }

    /// <summary>
    ///     Helper method to get API key from environment
    /// </summary>
    private static string GetApiKeyFromEnv()
    {
        return EnvironmentHelper.GetApiKeyFromEnv("ANTHROPIC_API_KEY");
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
