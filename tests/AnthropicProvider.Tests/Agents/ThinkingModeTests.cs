namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.TestUtils;
using Xunit;

public class ThinkingModeTests
{
    [Fact]
    public void AnthropicRequest_FromMessages_ShouldExtractThinkingFromOptions()
    {
        Console.WriteLine("Starting AnthropicRequest_FromMessages_ShouldExtractThinkingFromOptions test");
        // Arrange
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "What is 1234 * 5678?" },
        };
        Console.WriteLine("Created messages");

        // Set up thinking mode in options with an explicit budget
        var expectedBudget = 2048;
        var thinking = new AnthropicThinking(expectedBudget);
        Console.WriteLine($"Created thinking with budget: {thinking.BudgetTokens}");

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            Temperature = 1.0f,
            ExtraProperties = ImmutableDictionary.Create<string, object?>().Add("Thinking", thinking),
        };
        Console.WriteLine("Created options with thinking in ExtraProperties");

        // Act
        Console.WriteLine("About to call AnthropicRequest.FromMessages");
        var request = AnthropicRequest.FromMessages(messages, options);
        Console.WriteLine(
            $"FromMessages result - request: {(request != null ? "not null" : "null")}, Thinking: {(request?.Thinking != null ? request.Thinking.BudgetTokens.ToString() : "null")}"
        );

        // Assert
        Assert.NotNull(request);
        Assert.NotNull(request.Thinking);
        Assert.Equal(expectedBudget, request.Thinking.BudgetTokens);
    }

    [Fact]
    public async Task ThinkingMode_ShouldBeIncludedInRequest()
    {
        Console.WriteLine("Starting ThinkingMode_ShouldBeIncludedInRequest test");

        // Arrange - Using MockHttpHandlerBuilder with request capture
        var handler = MockHttpHandlerBuilder
            .Create()
            .RespondWithAnthropicMessage("This is a mock response for testing.", "claude-3-7-sonnet-20250219", 10, 20)
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);

        var thinking = new AnthropicThinking(2048);
        Console.WriteLine($"Created thinking with budget: {thinking.BudgetTokens}");

        var request = new AnthropicRequest
        {
            Model = "claude-3-7-sonnet-20250219",
            Thinking = thinking,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content =
                    [
                        new AnthropicContent { Type = "text", Text = "Hello" },
                    ],
                },
            ],
        };
        Console.WriteLine($"Created request with thinking: {request.Thinking?.BudgetTokens}");

        // Act - async call with proper await
        _ = await anthropicClient.CreateChatCompletionsAsync(request);
        Console.WriteLine($"After API call - Captured thinking from request");

        // Assert using the RequestCapture API
        Assert.Equal(1, requestCapture.RequestCount);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Thinking);
        Assert.Equal(2048, capturedRequest.Thinking.BudgetTokens);
        Console.WriteLine($"Verified thinking budget: {capturedRequest.Thinking.BudgetTokens}");

        // Also verify that the request was captured correctly
        Assert.Equal("claude-3-7-sonnet-20250219", capturedRequest.Model);
    }

    [Fact]
    public async Task ThinkingWithExecutePythonTool_ShouldBeIncludedInRequest()
    {
        TestLogger.Log("Starting ThinkingWithExecutePythonTool_ShouldBeIncludedInRequest test");

        // Arrange - Using MockHttpHandlerBuilder with request capture
        var handler = MockHttpHandlerBuilder
            .Create()
            .RespondWithAnthropicMessage("This is a mock response for testing.", "claude-3-7-sonnet-20250219", 10, 20)
            .CaptureRequests(out var requestCapture)
            .Build();

        var httpClient = new HttpClient(handler);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient);
        TestLogger.Log("Created agent and capture client");

        var messages = new[]
        {
            new TextMessage
            {
                Role = Role.System,
                Text =
                    "You are a helpful assistant that can use tools to help users. When you need to execute Python code, use the execute_python_in_container tool.",
            },
            new TextMessage { Role = Role.User, Text = "Find the files in /code that are not present in /code_old." },
        };
        TestLogger.Log($"Created messages array with {messages.Length} messages");

        // Create function definition for Python execution
        var pythonFunction = new FunctionContract
        {
            Name = "python_mcp-execute_python_in_container",
            Description = "Execute Python code in a Docker container",
            Parameters = new List<FunctionParameterContract>
            {
                new FunctionParameterContract
                {
                    Name = "code",
                    Description = "Python code to execute",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true,
                },
            },
        };

        // Set up thinking in options
        var thinking = new AnthropicThinking(1024);

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            MaxToken = 2000,
            Functions = [pythonFunction],
            ExtraProperties = ImmutableDictionary.Create<string, object?>().Add("Thinking", thinking),
        };
        TestLogger.Log("Created options with thinking and function tools");

        // Act
        TestLogger.Log("About to call GenerateReplyAsync");
        var response = await agent.GenerateReplyAsync(messages, options);
        TestLogger.Log("After GenerateReplyAsync call");

        // Assert using RequestCapture API
        Assert.Equal(1, requestCapture.RequestCount);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Thinking);
        Assert.Equal(1024, capturedRequest.Thinking.BudgetTokens);

        // Check system prompt handling
        Assert.NotNull(capturedRequest.System);
        Assert.Equal(
            "You are a helpful assistant that can use tools to help users. When you need to execute Python code, use the execute_python_in_container tool.",
            capturedRequest.System
        );

        // Check tool configuration using structured data
        var tools = capturedRequest.Tools.ToList();
        _ = Assert.Single(tools);
        Assert.Equal("python_mcp-execute_python_in_container", tools[0].Name);
        Assert.NotNull(tools[0].Description);
        Assert.NotNull(tools[0].InputSchema);

        TestLogger.Log($"Thinking budget verified: {capturedRequest.Thinking.BudgetTokens}");
    }
}
