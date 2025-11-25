using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.TestUtils.MockTools;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

public class FunctionToolTests
{
    [Fact]
    public async Task RequestFormat_FunctionTools()
    {
        TestLogger.Log("Starting RequestFormat_FunctionTools test");

        // Arrange - Using MockHttpHandlerBuilder with request capture
        var handler = MockHttpHandlerBuilder
            .Create()
            .RespondWithAnthropicMessage("This is a mock response for testing.", "claude-3-7-sonnet-20250219")
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient);
        TestLogger.Log("Created agent and capture client");

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" },
        };
        TestLogger.Log($"Created {messages.Length} messages");

        // Get weather function from MockWeatherTool
        var weatherFunction = MockToolCallHelper.CreateMockToolCalls([typeof(MockWeatherTool)]).Item1.First();

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            Functions = [weatherFunction],
        };
        TestLogger.Log("Created options with function tools");
        TestLogger.LogObject("Function definition", weatherFunction);

        // Act
        TestLogger.Log("About to call GenerateReplyAsync");
        var response = await agent.GenerateReplyAsync(messages, options);
        TestLogger.Log("After GenerateReplyAsync call");

        // Assert using structured RequestCapture API
        Assert.Equal(1, requestCapture.RequestCount);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        Assert.Equal("claude-3-7-sonnet-20250219", capturedRequest.Model);

        // Assert on tools using structured data instead of string inspection
        var tools = capturedRequest.Tools.ToList();
        Assert.NotEmpty(tools);

        var weatherTool = tools.First();
        Assert.Equal("getWeather", weatherTool.Name);
        Assert.NotNull(weatherTool.Description);
        Assert.NotNull(weatherTool.InputSchema);

        // Verify the tool has the expected input properties
        Assert.True(weatherTool.HasInputProperty("location"));
        Assert.Equal("string", weatherTool.GetInputPropertyType("location"));

        TestLogger.Log($"Successfully validated tool: {weatherTool.Name} with {tools.Count} tools total");
    }

    [Fact]
    public async Task MultipleTools_ShouldBeCorrectlyConfigured()
    {
        TestLogger.Log("Starting MultipleTools_ShouldBeCorrectlyConfigured test");

        // Arrange - Using MockHttpHandlerBuilder with request capture
        var handler = MockHttpHandlerBuilder
            .Create()
            .RespondWithAnthropicMessage("This is a mock response for testing.", "claude-3-7-sonnet-20250219")
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient);
        TestLogger.Log("Created agent and capture client");

        var messages = new[]
        {
            new TextMessage
            {
                Role = Role.System,
                Text = "You are a helpful assistant that can use tools to help users.",
            },
            new TextMessage { Role = Role.User, Text = "List files in root and \"code\" directories." },
        };
        TestLogger.Log($"Created messages array with {messages.Length} messages");

        // Get mock functions from MockPythonExecutionTool
        var mockFunctions = MockToolCallHelper.CreateMockToolCalls([typeof(MockPythonExecutionTool)]).Item1;

        // Create multiple function definitions based on example_requests.json
        // But extract parameter information from the mock tools
        var listDirectoryTemplate = mockFunctions.First(f => f.Name == "list_directory");
        var listDirectoryFunction = new FunctionContract
        {
            Name = "python_mcp-list_directory",
            Description = "List the contents of a directory within the code directory",
            Parameters = listDirectoryTemplate.Parameters,
        };

        var deleteFileTemplate = mockFunctions.First(f => f.Name == "delete_file");
        var deleteFileFunction = new FunctionContract
        {
            Name = "python_mcp-delete_file",
            Description = "Delete a file from the code directory",
            Parameters = deleteFileTemplate.Parameters,
        };

        var getDirTreeTemplate = mockFunctions.First(f => f.Name == "get_directory_tree");
        var getDirTreeFunction = new FunctionContract
        {
            Name = "python_mcp-get_directory_tree",
            Description = "Get an ASCII tree representation of a directory structure",
            Parameters = getDirTreeTemplate.Parameters,
        };

        var cleanupTemplate = mockFunctions.First(f => f.Name == "cleanup_code_directory");
        var cleanupFunction = new FunctionContract
        {
            Name = "python_mcp-cleanup_code_directory",
            Description = "Clean up the code directory by removing all files and subdirectories",
            Parameters = cleanupTemplate.Parameters,
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            MaxToken = 2000,
            Temperature = 0.7f,
            Functions = [listDirectoryFunction, deleteFileFunction, getDirTreeFunction, cleanupFunction],
        };
        TestLogger.Log("Created options with multiple function tools");

        // Act
        TestLogger.Log("About to call GenerateReplyAsync");
        var response = await agent.GenerateReplyAsync(messages, options);
        TestLogger.Log("After GenerateReplyAsync call");

        // Assert using structured RequestCapture API
        Assert.Equal(1, requestCapture.RequestCount);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        Assert.Equal("claude-3-7-sonnet-20250219", capturedRequest.Model);

        // Assert on tools using structured data instead of string inspection
        var tools = capturedRequest.Tools.ToList();
        Assert.Equal(4, tools.Count);

        // Verify that all expected tools are present
        var toolNames = tools.Select(t => t.Name).ToList();
        Assert.Contains("python_mcp-list_directory", toolNames);
        Assert.Contains("python_mcp-delete_file", toolNames);
        Assert.Contains("python_mcp-get_directory_tree", toolNames);
        Assert.Contains("python_mcp-cleanup_code_directory", toolNames);

        // Verify each tool has proper structure
        foreach (var tool in tools)
        {
            Assert.NotNull(tool.Name);
            Assert.NotNull(tool.Description);
            Assert.NotNull(tool.InputSchema);
        }

        TestLogger.Log($"Successfully validated all {tools.Count} tools with structured data");
    }

    [Fact]
    public async Task ToolUseResponse_ShouldBeCorrectlyParsed()
    {
        TestLogger.Log("Starting ToolUseResponse_ShouldBeCorrectlyParsed test");

        // Arrange - Using MockHttpHandlerBuilder with tool use response
        var handler = MockHttpHandlerBuilder
            .Create()
            .RespondWithToolUse(
                "python_mcp-list_directory",
                new { relative_path = "." },
                "I'll help you list the files in the root directory. Let me do this for you by using the list_directory function."
            )
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient);
        TestLogger.Log("Created agent and mock handler for tool use response");

        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "List files in the root directory" },
        };

        // Extract list_directory function from MockPythonExecutionTool
        var listDirTemplate = MockToolCallHelper
            .CreateMockToolCalls([typeof(MockPythonExecutionTool)])
            .Item1.First(f => f.Name == "list_directory");

        // Create function definition following the original test pattern
        var listDirFunction = new FunctionContract
        {
            Name = "python_mcp-list_directory",
            Description = "List directory contents",
            Parameters = listDirTemplate.Parameters,
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            Functions = [listDirFunction],
        };

        // Act
        TestLogger.Log("About to call GenerateReplyAsync");
        var response = await agent.GenerateReplyAsync(messages, options);
        TestLogger.Log("After GenerateReplyAsync call");

        // Verify we got a proper response with text
        Assert.NotNull(response);
        _ = Assert.IsType<TextMessage>(response.First());

        var textResponse = (TextMessage)response.First();
        Assert.Contains("I'll help you list the files", textResponse.Text);

        // Check that the request was captured correctly using RequestCapture API
        Assert.Equal(1, requestCapture.RequestCount);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Tools);

        var tools = capturedRequest.Tools.ToList();
        _ = Assert.Single(tools);
        Assert.Equal("python_mcp-list_directory", tools[0].Name);

        TestLogger.Log($"Successfully validated tool use response with tool: {tools[0].Name}");
    }
}
