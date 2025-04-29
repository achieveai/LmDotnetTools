using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class NaturalToolUseMiddlewareTests
{
    private readonly Mock<IJsonSchemaValidator> _mockSchemaValidator;
    private readonly Mock<IAgent> _mockFallbackParser;
    private readonly List<FunctionContract> _functionContracts;
    private readonly Dictionary<string, Func<string, Task<string>>> _functionMap;

    // Common test context
    private readonly MiddlewareContext _defaultContext;

    public NaturalToolUseMiddlewareTests()
    {
        _mockSchemaValidator = new Mock<IJsonSchemaValidator>();
        _mockFallbackParser = new Mock<IAgent>();
        _functionContracts = new List<FunctionContract>
    {
      new FunctionContract
      {
        Name = "GetWeather",
        Description = "A test tool",
        Parameters = new List<FunctionParameterContract>
        {
          new FunctionParameterContract
          {
            Name = "location",
            ParameterType = new JsonSchemaObject { Type = "string" },
            Description = "First parameter",
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "unit",
            ParameterType = new JsonSchemaObject { Type = "string" },
            Description = "Second parameter",
            IsRequired = true
          }
        }
      }
    };

        _functionMap = new Dictionary<string, Func<string, Task<string>>>
    {
      { "GetWeather", async (args) => await Task.FromResult("{\"temperature\": 72, \"conditions\": \"sunny\"}") }
    };

        // Initialize default context
        _defaultContext = new MiddlewareContext(new List<IMessage> { new TextMessage { Text = "Hello", Role = Role.User } }, null);
    }

    // Helper method to create middleware with default configuration
    private NaturalToolUseMiddleware CreateMiddleware()
    {
        return new NaturalToolUseMiddleware(
          _functionContracts,
          _functionMap,
          _mockFallbackParser.Object,
          "TestMiddleware",
          _mockSchemaValidator.Object);
    }

    // Helper method to setup mock agent with text response
    private Mock<IAgent> SetupMockAgent(string responseText)
    {
        var mockAgent = new Mock<IAgent>();
        mockAgent.Setup(a => a.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<IMessage> { new TextMessage { Text = responseText, Role = Role.Assistant } });
        return mockAgent;
    }

    // Helper method to setup a streaming agent with a single text message
    private Mock<IStreamingAgent> SetupStreamingAgent(string text)
    {
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        mockStreamingAgent.Setup(a => a.GenerateReplyStreamingAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()))
          .Returns(Task.FromResult(CreateSimpleAsyncEnumerable(text)));

        return mockStreamingAgent;
    }

    private async IAsyncEnumerable<IMessage> CreateSimpleAsyncEnumerable(string text)
    {
        await Task.Yield(); // Add await to make this truly async
        yield return new TextMessage { Text = text, Role = Role.Assistant };
    }

    private async Task<List<IMessage>> CollectAsyncEnumerable(IAsyncEnumerable<IMessage> asyncEnumerable)
    {
        var result = new List<IMessage>();
        await foreach (var item in asyncEnumerable)
        {
            result.Add(item);
        }
        return result;
    }

    [Fact]
    public async Task InvokeAsync_WithNoToolCall_ReturnsTextMessage()
    {
        // Arrange
        var middleware = CreateMiddleware();
        var mockAgent = SetupMockAgent("Hi there!");

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        var textMessage = Assert.Single(result.OfType<TextMessage>());
        Assert.Equal("Hi there!", textMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithValidToolCall_ReturnsMessages()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string validToolCall = "Here's the weather: <GetWeather>\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</GetWeather>";
        var mockAgent = SetupMockAgent(validToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Just check that we got a response
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidToolCall_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();

        // Create a response with proper tool call format but invalid JSON inside
        string invalidJsonToolCall = "Let me get that weather: <GetWeather>\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"invalid_json\": true,\n}\n```\n</GetWeather>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the JSON (invalid schema)
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        // Setup fallback parser to return valid JSON (this is what the fallback parser is supposed to do)
        string validFallbackJson = "```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()))
          .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Make schema validator accept the fixed JSON from the fallback parser
        _mockSchemaValidator.SetupSequence(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
          .Returns(false)  // First call with invalid JSON
          .Returns(true);  // Second call with fixed JSON from fallback

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Verify the fallback parser was called
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(),
            It.IsAny<GenerateReplyOptions>(),
            It.IsAny<CancellationToken>()),
          Times.AtLeastOnce);

        // Just check that we got a response
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithNoToolCall_ReturnsTextMessage()
    {
        // Arrange
        var middleware = CreateMiddleware();
        var mockAgent = SetupStreamingAgent("Hi there!");

        // Act
        var streamingResult = await middleware.InvokeStreamingAsync(_defaultContext, mockAgent.Object);
        var result = await CollectAsyncEnumerable(streamingResult);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains(result, m => m is TextMessage);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithValidToolCall_ReturnsMessages()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string validToolCall = "Here's the weather: <GetWeather>\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</GetWeather>";
        var mockAgent = SetupStreamingAgent(validToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Act
        var streamingResult = await middleware.InvokeStreamingAsync(_defaultContext, mockAgent.Object);
        var result = await CollectAsyncEnumerable(streamingResult);

        // Assert
        Assert.NotEmpty(result);
    }
}
