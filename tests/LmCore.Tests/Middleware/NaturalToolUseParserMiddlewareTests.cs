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

public class NaturalToolUseParserMiddlewareTests
{
    private readonly Mock<IJsonSchemaValidator> _mockSchemaValidator;
    private readonly Mock<IAgent> _mockFallbackParser;
    private readonly List<FunctionContract> _functionContracts;

    // Common test context
    private readonly MiddlewareContext _defaultContext;

    public NaturalToolUseParserMiddlewareTests()
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

        // Initialize default context
        _defaultContext = new MiddlewareContext(new List<IMessage> { new TextMessage { Text = "Hello", Role = Role.User } }, null);
    }

    // Helper method to create middleware with default configuration
    private NaturalToolUseParserMiddleware CreateMiddleware()
    {
        return new NaturalToolUseParserMiddleware(_functionContracts, _mockSchemaValidator.Object, _mockFallbackParser.Object);
    }

    // Helper method to setup mock agent with text response
    private Mock<IAgent> SetupMockAgent(string responseText)
    {
        var mockAgent = new Mock<IAgent>();
        mockAgent.Setup(a => a.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new List<IMessage> { new TextMessage { Text = responseText, Role = Role.Assistant } });
        return mockAgent;
    }

    // Helper method to setup a streaming agent with text update messages
    private Mock<IStreamingAgent> SetupStreamingAgent(string fullText, int chunkSize = 5)
    {
        var textChunks = SplitIntoChunks(fullText, chunkSize);
        var textUpdateMessages = MessageUpdateJoinerMiddlewareTests.CreateTextUpdateMessages(textChunks);

        var mockStreamingAgent = new Mock<IStreamingAgent>();
        mockStreamingAgent.Setup(a => a.GenerateReplyStreamingAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                          .Returns(Task.FromResult(CreateAsyncEnumerable(textUpdateMessages)));

        return mockStreamingAgent;
    }

    // Helper method to create middleware chain for streaming tests
    private IStreamingAgent CreateStreamingMiddlewareChain(IStreamingAgent innerAgent)
    {
        // First, use MessageUpdateJoinerMiddleware to join text updates into a complete TextMessage
        var joinerMiddleware = new MessageUpdateJoinerMiddleware();

        // Then, apply the NaturalToolUseParserMiddleware to the joined messages
        var naturalToolUseMiddleware = CreateMiddleware();

        // Create the chain: innerAgent -> joinerMiddleware -> naturalToolUseMiddleware
        var joinerWrappingAgent = new MiddlewareWrappingStreamingAgent(innerAgent, joinerMiddleware);
        return new MiddlewareWrappingStreamingAgent(joinerWrappingAgent, naturalToolUseMiddleware);
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
    public async Task InvokeAsync_WithValidToolCall_ReturnsTextAndToolCallMessages()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string validToolCall = "Here's the weather: <GetWeather>\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</GetWeather>";
        var mockAgent = SetupMockAgent(validToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Tool Call: GetWeather with args", toolCallMessage.Text);
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
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Make schema validator accept the fixed JSON from the fallback parser
        _mockSchemaValidator.SetupSequence(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false)  // First call with invalid JSON
            .Returns(true);  // Second call with fixed JSON from fallback

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Verify the fallback parser was called
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()), Times.Once);

        // Should receive text message and tool call message
        Assert.Equal(2, result.Count());

        // First message should be the original text before the tool call
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);

        // Last message should be the raw JSON returned from the fallback parser
        // The fallback parser returns just the JSON content, not a formatted tool call message
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("location", jsonMessage.Text);
        Assert.Contains("fahrenheit", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithUnknownToolName_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();

        // Create a response with a tool name that doesn't exist in the function contracts
        string unknownToolCall = "Let me search for that: <NonExistentTool>\n```json\n{\n  \"query\": \"test search\"\n}\n```\n</NonExistentTool>";
        var mockAgent = SetupMockAgent(unknownToolCall);

        // Act & Assert
        // The middleware throws an exception for unknown tools
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middleware.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the exception message contains the tool name
        Assert.Contains("NonExistentTool", exception.Message);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithNoToolCall_ReturnsTextMessage()
    {
        // Arrange
        string fullText = "Hi there!";
        var mockStreamingAgent = SetupStreamingAgent(fullText);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        var textMessage = Assert.Single(result.OfType<TextMessage>());
        Assert.Equal("Hi there!", textMessage.Text);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithValidToolCall_ReturnsTextAndToolCallMessages()
    {
        // Arrange
        // The full text containing a tool call
        string fullText = "Here's the weather: <GetWeather>\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</GetWeather>";

        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var mockStreamingAgent = SetupStreamingAgent(fullText, 7);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Tool Call: GetWeather with args", toolCallMessage.Text);
    }

    private static IAsyncEnumerable<IMessage> CreateAsyncEnumerable(IEnumerable<IMessage> messages)
    {
        return new AsyncEnumerableWrapper<IMessage>(messages);
    }

    private static IEnumerable<string> SplitIntoChunks(string input, int chunkSize)
    {
        for (int i = 0; i < input.Length; i += chunkSize)
        {
            yield return input.Substring(i, Math.Min(chunkSize, input.Length - i));
        }
    }

    private class MiddlewareWrappingStreamingAgent : IStreamingAgent
    {
        private readonly IStreamingAgent _agent;
        private readonly IStreamingMiddleware _middleware;

        public MiddlewareWrappingStreamingAgent(
            IStreamingAgent agent,
            IStreamingMiddleware middleware)
        {
            _agent = agent;
            _middleware = middleware;
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("This wrapper is only for streaming responses");
        }

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return _middleware.InvokeStreamingAsync(
                new MiddlewareContext(messages, options),
                _agent,
                cancellationToken);
        }
    }

    private class AsyncEnumerableWrapper<T> : IAsyncEnumerable<T>
    {
        private readonly IEnumerable<T> _source;

        public AsyncEnumerableWrapper(IEnumerable<T> source)
        {
            _source = source;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new AsyncEnumeratorWrapper<T>(_source.GetEnumerator());
        }
    }

    private class AsyncEnumeratorWrapper<T> : IAsyncEnumerator<T>
    {
        private readonly IEnumerator<T> _source;

        public AsyncEnumeratorWrapper(IEnumerator<T> source)
        {
            _source = source;
        }

        public T Current => _source.Current;

        public ValueTask DisposeAsync()
        {
            _source.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> MoveNextAsync()
        {
            return ValueTask.FromResult(_source.MoveNext());
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }
}
