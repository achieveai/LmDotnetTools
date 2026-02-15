using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

public class ThinkingModeTests : LoggingTestBase
{
    public ThinkingModeTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void AnthropicRequest_FromMessages_ShouldExtractThinkingFromOptions()
    {
        Logger.LogTrace("Starting AnthropicRequest_FromMessages_ShouldExtractThinkingFromOptions test");
        // Arrange
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "What is 1234 * 5678?" },
        };
        Logger.LogTrace("Created messages");

        // Set up thinking mode in options with an explicit budget
        var expectedBudget = 2048;
        var thinking = new AnthropicThinking(expectedBudget);
        Logger.LogTrace("Created thinking with budget: {BudgetTokens}", thinking.BudgetTokens);

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            Temperature = 1.0f,
            ExtraProperties = ImmutableDictionary.Create<string, object?>().Add("Thinking", thinking),
        };
        Logger.LogTrace("Created options with thinking in ExtraProperties");

        // Act
        Logger.LogTrace("About to call AnthropicRequest.FromMessages");
        var request = AnthropicRequest.FromMessages(messages, options);
        Logger.LogTrace(
            "FromMessages result - request: {RequestStatus}, Thinking: {ThinkingBudget}",
            request != null ? "not null" : "null",
            request?.Thinking != null ? request.Thinking.BudgetTokens.ToString() : "null");

        // Assert
        Assert.NotNull(request);
        Assert.NotNull(request.Thinking);
        Assert.Equal(expectedBudget, request.Thinking.BudgetTokens);
    }

    [Fact]
    public async Task ThinkingMode_ShouldBeIncludedInRequest()
    {
        Logger.LogTrace("Starting ThinkingMode_ShouldBeIncludedInRequest test");

        // Arrange - Using test-mode handler with request capture
        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(capture: requestCapture, chunkDelayMs: 0);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);

        var thinking = new AnthropicThinking(2048);
        Logger.LogTrace("Created thinking with budget: {BudgetTokens}", thinking.BudgetTokens);

        var request = new AnthropicRequest
        {
            Model = "claude-3-7-sonnet-20250219",
            Thinking = thinking,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "Hello" }],
                },
            ],
        };
        Logger.LogTrace("Created request with thinking: {BudgetTokens}", request.Thinking?.BudgetTokens);

        // Act - async call with proper await
        _ = await anthropicClient.CreateChatCompletionsAsync(request);
        Logger.LogTrace("After API call - Captured thinking from request");

        // Assert using the RequestCapture API
        Assert.Equal(1, requestCapture.RequestCount);

        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.Thinking);
        Assert.Equal(2048, capturedRequest.Thinking.BudgetTokens);
        Logger.LogTrace("Verified thinking budget: {BudgetTokens}", capturedRequest.Thinking.BudgetTokens);

        // Also verify that the request was captured correctly
        Assert.Equal("claude-3-7-sonnet-20250219", capturedRequest.Model);
    }

    [Fact]
    public async Task ThinkingWithExecutePythonTool_ShouldBeIncludedInRequest()
    {
        Logger.LogTrace("Starting ThinkingWithExecutePythonTool_ShouldBeIncludedInRequest test");

        // Arrange - Using test-mode handler with request capture
        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(capture: requestCapture, chunkDelayMs: 0);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent("TestAgent", anthropicClient);
        Logger.LogTrace("Created agent and capture client");

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
        Logger.LogTrace("Created messages array with {MessageCount} messages", messages.Length);

        // Create function definition for Python execution
        var pythonFunction = new FunctionContract
        {
            Name = "python_mcp-execute_python_in_container",
            Description = "Execute Python code in a Docker container",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "code",
                    Description = "Python code to execute",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true,
                },
            ],
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
        Logger.LogTrace("Created options with thinking and function tools");

        // Act
        Logger.LogTrace("About to call GenerateReplyAsync");
        var response = await agent.GenerateReplyAsync(messages, options);
        Logger.LogTrace("After GenerateReplyAsync call");

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

        Logger.LogTrace("Thinking budget verified: {BudgetTokens}", capturedRequest.Thinking.BudgetTokens);
    }
}
