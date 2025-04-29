using System.Diagnostics;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.TestUtils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

public class DataDrivenFunctionToolTests
{
    private readonly ProviderTestDataManager _testDataManager = new ProviderTestDataManager();
    private static string EnvTestPath => Path.Combine(AchieveAi.LmDotnetTools.TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory), ".env.test");

    [Theory]
    [MemberData(nameof(GetFunctionToolTestCases))]
    public async Task FunctionTool_RequestAndResponseTransformation(string testName)
    {
        Debug.WriteLine($"Starting test for {testName}");

        // Arrange - Load data from test files
        var (messages, options) = _testDataManager.LoadLmCoreRequest(testName, ProviderType.OpenAI);
        Debug.WriteLine($"Loaded {messages.Length} messages and options with {options.Functions?.Length ?? 0} functions");

        // Use the DatabasedClientWrapper to record or replay the API interaction
        using var client = OpenClientFactory.CreateDatabasedClient(Path.Combine("OpenAI", testName), EnvTestPath, false);
        var agent = new OpenClientAgent("TestAgent", client);
        Debug.WriteLine("Created agent with client wrapper");

        // Act
        var response = await agent.GenerateReplyAsync(messages, options);
        Debug.WriteLine($"Generated response: {response?.GetType().Name}");

        // Assert - Compare with expected response
        var expectedResponses = _testDataManager.LoadFinalResponse(testName, ProviderType.OpenAI);

        Debug.WriteLine($"Response count: {response?.Count() ?? 0}, Expected count: {expectedResponses.Count}");
        Debug.WriteLine($"Response types: {string.Join(", ", response?.Select(r => r.GetType().Name) ?? Array.Empty<string>())}");
        Debug.WriteLine($"Expected types: {string.Join(", ", expectedResponses.Select(r => r.GetType().Name))}");

        Assert.NotNull(response);
        // The expected count in the test files is 2, but the actual response now has 3 items due to the UsageMessage
        // Modify the assertion to expect 3 items instead of 2
        Assert.Equal(expectedResponses.Count() + 1, response.Count());

        // Match the first two messages from the response with the expected messages
        var responseToTest = response.Take(expectedResponses.Count()).ToList();
        foreach (var (expectedResponse, responseItem) in expectedResponses.Zip(responseToTest))
        {
            if (expectedResponse is TextMessage expectedTextResponse)
            {
                Assert.IsType<TextMessage>(responseItem);
                Assert.Equal(expectedTextResponse.Text, ((TextMessage)responseItem).Text);
                Assert.Equal(expectedTextResponse.Role, responseItem.Role);
            }
            else if (expectedResponse is ToolsCallAggregateMessage expectedToolsCallAggregateMessage)
            {
                Assert.IsType<ToolsCallAggregateMessage>(responseItem);
                var toolsCallAggregateMessage = (ToolsCallAggregateMessage)responseItem;
                Assert.Equal(expectedToolsCallAggregateMessage.Role, toolsCallAggregateMessage.Role);
                Assert.Equal(expectedToolsCallAggregateMessage.FromAgent, toolsCallAggregateMessage.FromAgent);
                Assert.Equal(
                    expectedToolsCallAggregateMessage.ToolsCallMessage!.GetToolCalls()!.Count(),
                    toolsCallAggregateMessage.ToolsCallMessage!.GetToolCalls()!.Count());
                foreach (var (expectedToolCall, toolCall) in expectedToolsCallAggregateMessage.ToolsCallMessage!
                    .GetToolCalls()!.Zip(toolsCallAggregateMessage.ToolsCallMessage!.GetToolCalls()!))
                {
                    Assert.Equal(expectedToolCall.FunctionName, toolCall.FunctionName);
                    Assert.Equal(expectedToolCall.FunctionArgs, toolCall.FunctionArgs);
                }
            }
        }

        // Verify that the last message is a UsageMessage
        var lastMessage = response.Last();
        Assert.IsType<UsageMessage>(lastMessage);

        Debug.WriteLine($"Test {testName} completed successfully");
    }

    /// <summary>
    /// Gets all test cases from the TestData directory.
    /// </summary>
    public static IEnumerable<object[]> GetFunctionToolTestCases()
    {
        var testDataManager = new ProviderTestDataManager();
        return testDataManager.GetTestCaseNames(ProviderType.OpenAI)
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
        string testName = "WeatherFunctionTool";
        string testDataPath = _testDataManager.GetTestDataPath(testName, ProviderType.OpenAI, DataType.LmCoreRequest);

        if (File.Exists(testDataPath))
        {
            Debug.WriteLine($"Test data already exists at {testDataPath}. Skipping creation.");
            return;
        }

        // 1. LmCore request data - messages and options
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" }
        };

        var weatherFunction = new FunctionContract
        {
            Name = "getWeather",
            Description = "Get current weather for a location",
            Parameters = new List<FunctionParameterContract>
            {
                new FunctionParameterContract
                {
                    Name = "location",
                    Description = "City name",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true
                }
            }
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4",
            Functions = new[] { weatherFunction }
        };

        // Save LmCore request
        _testDataManager.SaveLmCoreRequest(testName, ProviderType.OpenAI, messages, options);

        // 2. Create a client to capture request/response
        using var client = OpenClientFactory.CreateDatabasedClient(testName, EnvTestPath, true);
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
        string testName = "MultiFunctionTool";
        string testDataPath = _testDataManager.GetTestDataPath(testName, ProviderType.OpenAI, DataType.LmCoreRequest);

        if (File.Exists(testDataPath))
        {
            Debug.WriteLine($"Test data already exists at {testDataPath}. Skipping creation.");
            return;
        }

        // 1. LmCore request data - messages and options
        var messages = new[]
        {
            new TextMessage { Role = Role.System, Text = "You are a helpful assistant that can use tools to help users." },
            new TextMessage { Role = Role.User, Text = "List files in root and \"code\" directories." }
        };

        // Create multiple function definitions
        var listDirectoryFunction = new FunctionContract
        {
            Name = "python_mcp-list_directory",
            Description = "List the contents of a directory within the code directory",
            Parameters = new List<FunctionParameterContract>
            {
                new FunctionParameterContract
                {
                    Name = "relative_path",
                    Description = "Relative path within the code directory",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = false
                }
            }
        };

        var getDirTreeFunction = new FunctionContract
        {
            Name = "python_mcp-get_directory_tree",
            Description = "Get an ASCII tree representation of a directory structure",
            Parameters = new List<FunctionParameterContract>
            {
                new FunctionParameterContract
                {
                    Name = "relative_path",
                    Description = "Relative path within the code directory",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = false
                }
            }
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4",
            MaxToken = 2000,
            Temperature = 0.7f,
            Functions = new[] { listDirectoryFunction, getDirTreeFunction }
        };

        // Save LmCore request
        _testDataManager.SaveLmCoreRequest(testName, ProviderType.OpenAI, messages, options);

        // 2. Create a client to capture request/response
        using var client = OpenClientFactory.CreateDatabasedClient(testName, EnvTestPath, true);
        var agent = new OpenClientAgent("TestAgent", client);

        // 3. Generate response
        var response = await agent.GenerateReplyAsync(messages, options);

        // 4. Save final response
        _testDataManager.SaveFinalResponse(testName, ProviderType.OpenAI, response);
    }
}