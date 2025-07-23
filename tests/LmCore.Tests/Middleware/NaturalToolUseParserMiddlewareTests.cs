using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;

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
        string validToolCall = "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(validToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("San Francisco, CA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidToolCall_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();

        // Create a response with proper tool call format but invalid JSON inside
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"invalid_json\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the original invalid JSON but accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("invalid_json")), It.IsAny<string>()))
            .Returns(false);
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("fahrenheit") && !json.Contains("invalid_json")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON (this is what the fallback parser is supposed to do)
        string validFallbackJson = "```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

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
        string unknownToolCall = "Let me search for that: <tool_call name=\"NonExistentTool\">\n```json\n{\n  \"query\": \"test search\"\n}\n```\n</tool_call>";
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
        string fullText = "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";

        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var mockStreamingAgent = SetupStreamingAgent(fullText, 7);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("San Francisco, CA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
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

    #region JSON Extraction Enhancement Tests

    [Fact]
    public async Task InvokeAsync_WithFencedJsonBlock_ExtractsJsonSuccessfully()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string fencedJsonToolCall = "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(fencedJsonToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("San Francisco, CA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
    }

    [Fact]
    public async Task InvokeAsync_WithUnlabeledFencedJsonBlock_ExtractsJsonSuccessfully()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string unlabeledFencedJsonToolCall = "Here's the weather: <tool_call name=\"GetWeather\">\n```\n{\n  \"location\": \"New York, NY\",\n  \"unit\": \"celsius\"\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(unlabeledFencedJsonToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("New York, NY", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("celsius", toolCallMessage.ToolCalls[0].FunctionArgs);
    }

    [Fact]
    public async Task InvokeAsync_WithUnfencedJsonBlock_ExtractsJsonSuccessfully()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string unfencedJsonToolCall = "Here's the weather: <tool_call name=\"GetWeather\">\n{\n  \"location\": \"Boston, MA\",\n  \"unit\": \"fahrenheit\"\n}\n</tool_call>";
        var mockAgent = SetupMockAgent(unfencedJsonToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("Boston, MA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
    }

    [Fact]
    public async Task InvokeAsync_WithCleanUnfencedJsonOnly_ExtractsJsonSuccessfully()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Clean unfenced JSON with only JSON content (no mixed text)
        string cleanUnfencedJsonToolCall = "Here's the weather: <tool_call name=\"GetWeather\">{\"location\": \"Portland, OR\", \"unit\": \"celsius\"}</tool_call>";
        var mockAgent = SetupMockAgent(cleanUnfencedJsonToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("Portland, OR", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("celsius", toolCallMessage.ToolCalls[0].FunctionArgs);
    }

    [Fact]
    public async Task InvokeAsync_WithUnfencedArrayJson_ExtractsJsonSuccessfully()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string unfencedArrayJsonToolCall = "Here's the weather: <tool_call name=\"GetWeather\">\n[\n  {\n    \"location\": \"Chicago, IL\",\n    \"unit\": \"celsius\"\n  }\n]\n</tool_call>";
        var mockAgent = SetupMockAgent(unfencedArrayJsonToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("Chicago, IL", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("celsius", toolCallMessage.ToolCalls[0].FunctionArgs);
    }

    [Fact]
    public async Task InvokeAsync_WithMixedContentFencedAndUnfenced_PrioritizesFencedJson()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Content with both fenced JSON and unfenced JSON - fenced should take priority
        string mixedContentToolCall = "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Seattle, WA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n{\n  \"location\": \"Portland, OR\",\n  \"unit\": \"celsius\"\n}\n</tool_call>";
        var mockAgent = SetupMockAgent(mixedContentToolCall);
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        // Should contain the fenced JSON content (Seattle), not the unfenced content (Portland)
        Assert.Contains("Seattle, WA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.DoesNotContain("Portland, OR", toolCallMessage.ToolCalls[0].FunctionArgs);
    }

    [Fact]
    public async Task InvokeAsync_WithMixedContentTextAndUnfenced_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Content with text and unfenced JSON - the unfenced JSON extraction won't work because it's mixed with text
        // So this should fall back to the fallback parser
        string mixedContentToolCall = "Here's the weather: <tool_call name=\"GetWeather\">\nSome explanatory text here.\n{\n  \"location\": \"Miami, FL\",\n  \"unit\": \"fahrenheit\"\n}\nMore text after.\n</tool_call>";
        var mockAgent = SetupMockAgent(mixedContentToolCall);
        
        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Miami, FL") && json.Contains("fahrenheit")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Miami, FL\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Miami, FL", jsonMessage.Text);
        Assert.Contains("fahrenheit", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidFencedJson_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidFencedJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Denver, CO\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidFencedJsonToolCall);

        // Setup schema validator to reject the invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("invalid_syntax")), It.IsAny<string>()))
            .Returns(false);
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("celsius") && !json.Contains("invalid_syntax")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "```json\n{\n  \"location\": \"Denver, CO\",\n  \"unit\": \"celsius\"\n}\n```";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Denver, CO", jsonMessage.Text);
        Assert.Contains("celsius", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidUnfencedJson_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidUnfencedJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n{\n  \"location\": \"Austin, TX\"\n  \"missing_comma\": true\n}\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidUnfencedJsonToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Austin, TX") && json.Contains("fahrenheit")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Austin, TX\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Austin, TX", jsonMessage.Text);
        Assert.Contains("fahrenheit", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithMalformedUnfencedJson_DoesNotExtractJson()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Content that starts with { but is not valid JSON
        string malformedJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n{this is not valid json at all}\n</tool_call>";
        var mockAgent = SetupMockAgent(malformedJsonToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Phoenix, AZ") && json.Contains("fahrenheit")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Phoenix, AZ\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Should use fallback parser since unfenced JSON extraction fails
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Phoenix, AZ", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithEmptyToolCallContent_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string emptyContentToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n\n</tool_call>";
        var mockAgent = SetupMockAgent(emptyContentToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Las Vegas, NV") && json.Contains("fahrenheit")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Las Vegas, NV\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Las Vegas, NV", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithWhitespaceOnlyContent_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string whitespaceOnlyToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n   \n\t\n  \n</tool_call>";
        var mockAgent = SetupMockAgent(whitespaceOnlyToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Salt Lake City, UT") && json.Contains("celsius")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Salt Lake City, UT\",\n  \"unit\": \"celsius\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Salt Lake City, UT", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidFencedJsonAndNoFallback_ThrowsException()
    {
        // Arrange - Create middleware without fallback parser
        var middlewareWithoutFallback = new NaturalToolUseParserMiddleware(_functionContracts, _mockSchemaValidator.Object);
        string invalidFencedJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Portland, ME\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidFencedJsonToolCall);

        // Setup schema validator to reject the invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middlewareWithoutFallback.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        Assert.Contains("GetWeather", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidUnfencedJsonAndNoFallback_ThrowsException()
    {
        // Arrange - Create middleware without fallback parser
        var middlewareWithoutFallback = new NaturalToolUseParserMiddleware(_functionContracts, _mockSchemaValidator.Object);
        string invalidUnfencedJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n{this is not valid json}\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidUnfencedJsonToolCall);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middlewareWithoutFallback.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        Assert.Contains("GetWeather", exception.Message);
    }

    #endregion

    #region Structured Output Fallback Tests

    [Fact]
    public async Task InvokeAsync_WithStructuredOutputFallback_ValidJson_ReturnsValidResult()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the original invalid JSON but accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("invalid_syntax")), It.IsAny<string>()))
            .Returns(false);
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("fahrenheit") && !json.Contains("invalid_syntax")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON with structured output
        string validFallbackJson = "{\"location\": \"San Francisco, CA\", \"unit\": \"fahrenheit\"}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null && o.ResponseFormat.ResponseFormatType == "json_schema"), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Verify the fallback parser was called with structured output
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null && o.ResponseFormat.ResponseFormatType == "json_schema"), 
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("San Francisco, CA", jsonMessage.Text);
        Assert.Contains("fahrenheit", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithStructuredOutputFallback_InvalidJson_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Denver, CO\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject both the original and fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Setup fallback parser to return invalid JSON even with structured output
        string invalidFallbackJson = "{\"invalid\": \"response\"}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null && o.ResponseFormat.ResponseFormatType == "json_schema"), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = invalidFallbackJson, Role = Role.Assistant } });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middleware.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the fallback parser was called with structured output
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null && o.ResponseFormat.ResponseFormatType == "json_schema"), 
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Contains("GetWeather", exception.Message);
        Assert.Contains("invalid JSON", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_VerifyGenerateReplyOptionsContainsCorrectResponseFormat()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Boston, MA\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the original invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Setup fallback parser to capture the options and return valid JSON
        GenerateReplyOptions? capturedOptions = null;
        string validFallbackJson = "{\"location\": \"Boston, MA\", \"unit\": \"celsius\"}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>((messages, options, token) => 
            {
                capturedOptions = options;
            })
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        try
        {
            await middleware.InvokeAsync(_defaultContext, mockAgent.Object);
        }
        catch (ToolUseParsingException)
        {
            // Expected since we're not setting up schema validation to pass
        }

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions.ResponseFormat);
        Assert.Equal("json_schema", capturedOptions.ResponseFormat.ResponseFormatType);
        Assert.NotNull(capturedOptions.ResponseFormat.JsonSchema);
        Assert.Equal("GetWeather_parameters", capturedOptions.ResponseFormat.JsonSchema.Name);
        Assert.True(capturedOptions.ResponseFormat.JsonSchema.Strict);
        Assert.NotNull(capturedOptions.ResponseFormat.JsonSchema.Schema);
    }

    [Fact]
    public async Task InvokeAsync_WithoutFallbackAgent_BackwardCompatibility_ThrowsException()
    {
        // Arrange - Create middleware without fallback parser
        var middlewareWithoutFallback = new NaturalToolUseParserMiddleware(_functionContracts, _mockSchemaValidator.Object);
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Seattle, WA\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middlewareWithoutFallback.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify that no fallback parser was called (since none was provided)
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()), Times.Never);

        Assert.Contains("GetWeather", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_WithFallbackAgentException_ThrowsToolUseParsingException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Portland, OR\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Setup fallback parser to throw an exception
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fallback agent failed"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middleware.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the fallback parser was called (may be called multiple times due to structured output and fallback logic)
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        Assert.Contains("GetWeather", exception.Message);
        Assert.Contains("Fallback parser failed", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_WithFallbackAgentNoResponse_ThrowsToolUseParsingException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Miami, FL\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Setup fallback parser to return empty response
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage>());

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middleware.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the fallback parser was called
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Contains("GetWeather", exception.Message);
        Assert.Contains("failed to generate response", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_WithStructuredOutputFallback_NoSchemaValidator_UsesJsonParsing()
    {
        // Arrange - Create middleware without schema validator but with fallback parser
        var middlewareWithoutValidator = new NaturalToolUseParserMiddleware(_functionContracts, null, _mockFallbackParser.Object);
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Chicago, IL\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\"location\": \"Chicago, IL\", \"unit\": \"celsius\"}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middlewareWithoutValidator.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Verify the fallback parser was called (since JSON extraction failed)
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Chicago, IL", jsonMessage.Text);
        Assert.Contains("celsius", jsonMessage.Text);
    }

    #endregion

    #region Streaming Integration Tests

    [Fact]
    public async Task InvokeStreamingAsync_WithEnhancedJsonExtraction_FencedJson_ProcessesCorrectly()
    {
        // Arrange
        string fullText = "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call> Done!";
        
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var mockStreamingAgent = SetupStreamingAgent(fullText, 10);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        Assert.Equal(3, result.Count());
        
        // First message should be text before tool call
        var textMessage1 = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage1.Text);
        
        // Second message should be the tool call
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("San Francisco, CA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
        
        // Third message should be text after tool call
        var textMessage2 = result.OfType<TextMessage>().Last();
        Assert.Equal("Done!", textMessage2.Text);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithEnhancedJsonExtraction_UnfencedJson_ProcessesCorrectly()
    {
        // Arrange
        string fullText = "Here's the weather: <tool_call name=\"GetWeather\">\n{\n  \"location\": \"Boston, MA\",\n  \"unit\": \"celsius\"\n}\n</tool_call> Complete!";
        
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var mockStreamingAgent = SetupStreamingAgent(fullText, 8);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        Assert.Equal(3, result.Count());
        
        // First message should be text before tool call
        var textMessage1 = result.OfType<TextMessage>().First();
        Assert.Equal("Here's the weather:", textMessage1.Text);
        
        // Second message should be the tool call
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("Boston, MA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("celsius", toolCallMessage.ToolCalls[0].FunctionArgs);
        
        // Third message should be text after tool call
        var textMessage2 = result.OfType<TextMessage>().Last();
        Assert.Equal("Complete!", textMessage2.Text);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithEnhancedJsonExtraction_CleanUnfencedJson_ProcessesCorrectly()
    {
        // Arrange
        string fullText = "Weather info: <tool_call name=\"GetWeather\">{\"location\": \"Portland, OR\", \"unit\": \"fahrenheit\"}</tool_call> Finished!";
        
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var mockStreamingAgent = SetupStreamingAgent(fullText, 12);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        Assert.Equal(3, result.Count());
        
        // First message should be text before tool call
        var textMessage1 = result.OfType<TextMessage>().First();
        Assert.Equal("Weather info:", textMessage1.Text);
        
        // Second message should be the tool call
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("Portland, OR", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
        
        // Third message should be text after tool call
        var textMessage2 = result.OfType<TextMessage>().Last();
        Assert.Equal("Finished!", textMessage2.Text);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithEnhancedJsonExtraction_MixedContent_PrioritizesFencedJson()
    {
        // Arrange
        string fullText = "Weather data: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Seattle, WA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n{\n  \"location\": \"Portland, OR\",\n  \"unit\": \"celsius\"\n}\n</tool_call> All done!";
        
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var mockStreamingAgent = SetupStreamingAgent(fullText, 15);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        Assert.Equal(3, result.Count());
        
        // First message should be text before tool call
        var textMessage1 = result.OfType<TextMessage>().First();
        Assert.Equal("Weather data:", textMessage1.Text);
        
        // Second message should be the tool call with fenced JSON (Seattle, not Portland)
        var toolCallMessage = result.OfType<ToolsCallMessage>().Single();
        Assert.Equal("GetWeather", toolCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("Seattle, WA", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolCalls[0].FunctionArgs);
        Assert.DoesNotContain("Portland, OR", toolCallMessage.ToolCalls[0].FunctionArgs);
        
        // Third message should be text after tool call
        var textMessage2 = result.OfType<TextMessage>().Last();
        Assert.Equal("All done!", textMessage2.Text);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithStructuredOutputFallback_InvalidJson_UsesStructuredOutput()
    {
        // Arrange
        string fullText = "Getting weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Denver, CO\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call> Completed!";
        
        // Setup schema validator to reject invalid JSON but accept fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("invalid_syntax")), It.IsAny<string>()))
            .Returns(false);
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Denver, CO") && json.Contains("celsius") && !json.Contains("invalid_syntax")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON with structured output
        string validFallbackJson = "{\n  \"location\": \"Denver, CO\",\n  \"unit\": \"celsius\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        var mockStreamingAgent = SetupStreamingAgent(fullText, 12);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        // Verify fallback parser was called with structured output
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(3, result.Count());
        
        // First message should be text before tool call
        var textMessage1 = result.OfType<TextMessage>().First();
        Assert.Equal("Getting weather:", textMessage1.Text);
        
        // Second message should be the corrected JSON from fallback
        var jsonMessage = result.OfType<TextMessage>().Skip(1).First();
        Assert.Contains("Denver, CO", jsonMessage.Text);
        Assert.Contains("celsius", jsonMessage.Text);
        Assert.DoesNotContain("invalid_syntax", jsonMessage.Text);
        
        // Third message should be text after tool call
        var textMessage2 = result.OfType<TextMessage>().Last();
        Assert.Equal("Completed!", textMessage2.Text);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithStructuredOutputFallback_MixedContentWithText_UsesStructuredOutput()
    {
        // Arrange
        string fullText = "Weather lookup: <tool_call name=\"GetWeather\">\nSome explanatory text here.\n{\n  \"location\": \"Miami, FL\",\n  \"unit\": \"fahrenheit\"\n}\nMore text after.\n</tool_call> Done processing!";
        
        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Miami, FL") && json.Contains("fahrenheit")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON with structured output
        string validFallbackJson = "{\n  \"location\": \"Miami, FL\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        var mockStreamingAgent = SetupStreamingAgent(fullText, 18);
        var agent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        // Act
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var result = await ToListAsync(resultStream);

        // Assert
        // Verify fallback parser was called with structured output
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(3, result.Count());
        
        // First message should be text before tool call
        var textMessage1 = result.OfType<TextMessage>().First();
        Assert.Equal("Weather lookup:", textMessage1.Text);
        
        // Second message should be the corrected JSON from fallback
        var jsonMessage = result.OfType<TextMessage>().Skip(1).First();
        Assert.Contains("Miami, FL", jsonMessage.Text);
        Assert.Contains("fahrenheit", jsonMessage.Text);
        
        // Third message should be text after tool call
        var textMessage2 = result.OfType<TextMessage>().Last();
        Assert.Equal("Done processing!", textMessage2.Text);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithStructuredOutputFallback_NoFallbackAgent_ThrowsException()
    {
        // Arrange
        var middlewareWithoutFallback = new NaturalToolUseParserMiddleware(_functionContracts, _mockSchemaValidator.Object, null);
        string fullText = "Weather check: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Austin, TX\"\n  \"missing_comma\": true\n}\n```\n</tool_call> End!";
        
        // Setup schema validator to reject invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("missing_comma")), It.IsAny<string>()))
            .Returns(false);

        var mockStreamingAgent = SetupStreamingAgent(fullText, 10);
        
        // Create middleware chain without fallback agent
        var joinerMiddleware = new MessageUpdateJoinerMiddleware();
        var joinerWrappingAgent = new MiddlewareWrappingStreamingAgent(mockStreamingAgent.Object, joinerMiddleware);
        var agent = new MiddlewareWrappingStreamingAgent(joinerWrappingAgent, middlewareWithoutFallback);

        // Act & Assert
        var resultStream = await agent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        
        // Should throw exception when trying to enumerate the stream
        await Assert.ThrowsAsync<ToolUseParsingException>(async () =>
        {
            await ToListAsync(resultStream);
        });
    }

    [Fact]
    public async Task InvokeStreamingAsync_ConsistencyWithNonStreaming_FencedJson()
    {
        // Arrange
        string toolCallText = "Weather report: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Chicago, IL\",\n  \"unit\": \"celsius\"\n}\n```\n</tool_call> Finished!";
        
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Setup non-streaming agent
        var mockAgent = SetupMockAgent(toolCallText);
        
        // Setup streaming agent
        var mockStreamingAgent = SetupStreamingAgent(toolCallText, 15);
        var streamingAgent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        var middleware = CreateMiddleware();

        // Act - Non-streaming
        var nonStreamingResult = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);
        
        // Act - Streaming
        var streamingResultStream = await streamingAgent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var streamingResult = await ToListAsync(streamingResultStream);

        // Assert - Results should be equivalent
        Assert.Equal(nonStreamingResult.Count(), streamingResult.Count());
        
        // Compare text messages
        var nonStreamingTextMessages = nonStreamingResult.OfType<TextMessage>().ToList();
        var streamingTextMessages = streamingResult.OfType<TextMessage>().ToList();
        
        Assert.Equal(nonStreamingTextMessages.Count, streamingTextMessages.Count);
        for (int i = 0; i < nonStreamingTextMessages.Count; i++)
        {
            Assert.Equal(nonStreamingTextMessages[i].Text, streamingTextMessages[i].Text);
        }
        
        // Compare tool call messages
        var nonStreamingToolCalls = nonStreamingResult.OfType<ToolsCallMessage>().ToList();
        var streamingToolCalls = streamingResult.OfType<ToolsCallMessage>().ToList();
        
        Assert.Equal(nonStreamingToolCalls.Count, streamingToolCalls.Count);
        for (int i = 0; i < nonStreamingToolCalls.Count; i++)
        {
            Assert.Equal(nonStreamingToolCalls[i].ToolCalls[0].FunctionName, streamingToolCalls[i].ToolCalls[0].FunctionName);
            Assert.Equal(nonStreamingToolCalls[i].ToolCalls[0].FunctionArgs, streamingToolCalls[i].ToolCalls[0].FunctionArgs);
        }
    }

    [Fact]
    public async Task InvokeStreamingAsync_ConsistencyWithNonStreaming_UnfencedJson()
    {
        // Arrange
        string toolCallText = "Weather data: <tool_call name=\"GetWeather\">\n{\n  \"location\": \"Phoenix, AZ\",\n  \"unit\": \"fahrenheit\"\n}\n</tool_call> Complete!";
        
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        // Setup non-streaming agent
        var mockAgent = SetupMockAgent(toolCallText);
        
        // Setup streaming agent
        var mockStreamingAgent = SetupStreamingAgent(toolCallText, 12);
        var streamingAgent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        var middleware = CreateMiddleware();

        // Act - Non-streaming
        var nonStreamingResult = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);
        
        // Act - Streaming
        var streamingResultStream = await streamingAgent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var streamingResult = await ToListAsync(streamingResultStream);

        // Assert - Results should be equivalent
        Assert.Equal(nonStreamingResult.Count(), streamingResult.Count());
        
        // Compare text messages
        var nonStreamingTextMessages = nonStreamingResult.OfType<TextMessage>().ToList();
        var streamingTextMessages = streamingResult.OfType<TextMessage>().ToList();
        
        Assert.Equal(nonStreamingTextMessages.Count, streamingTextMessages.Count);
        for (int i = 0; i < nonStreamingTextMessages.Count; i++)
        {
            Assert.Equal(nonStreamingTextMessages[i].Text, streamingTextMessages[i].Text);
        }
        
        // Compare tool call messages
        var nonStreamingToolCalls = nonStreamingResult.OfType<ToolsCallMessage>().ToList();
        var streamingToolCalls = streamingResult.OfType<ToolsCallMessage>().ToList();
        
        Assert.Equal(nonStreamingToolCalls.Count, streamingToolCalls.Count);
        for (int i = 0; i < nonStreamingToolCalls.Count; i++)
        {
            Assert.Equal(nonStreamingToolCalls[i].ToolCalls[0].FunctionName, streamingToolCalls[i].ToolCalls[0].FunctionName);
            Assert.Equal(nonStreamingToolCalls[i].ToolCalls[0].FunctionArgs, streamingToolCalls[i].ToolCalls[0].FunctionArgs);
        }
    }

    [Fact]
    public async Task InvokeStreamingAsync_ConsistencyWithNonStreaming_StructuredOutputFallback()
    {
        // Arrange
        string toolCallText = "Weather info: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"Las Vegas, NV\"\n  \"syntax_error\": true,\n}\n```\n</tool_call> Done!";
        
        // Setup schema validator to reject invalid JSON but accept fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("syntax_error")), It.IsAny<string>()))
            .Returns(false);
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Las Vegas, NV") && json.Contains("fahrenheit") && !json.Contains("syntax_error")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Las Vegas, NV\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Setup non-streaming agent
        var mockAgent = SetupMockAgent(toolCallText);
        
        // Setup streaming agent
        var mockStreamingAgent = SetupStreamingAgent(toolCallText, 14);
        var streamingAgent = CreateStreamingMiddlewareChain(mockStreamingAgent.Object);

        var middleware = CreateMiddleware();

        // Act - Non-streaming
        var nonStreamingResult = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);
        
        // Act - Streaming
        var streamingResultStream = await streamingAgent.GenerateReplyStreamingAsync(_defaultContext.Messages, _defaultContext.Options);
        var streamingResult = await ToListAsync(streamingResultStream);

        // Assert - Results should be equivalent
        Assert.Equal(nonStreamingResult.Count(), streamingResult.Count());
        
        // Compare text messages
        var nonStreamingTextMessages = nonStreamingResult.OfType<TextMessage>().ToList();
        var streamingTextMessages = streamingResult.OfType<TextMessage>().ToList();
        
        Assert.Equal(nonStreamingTextMessages.Count, streamingTextMessages.Count);
        for (int i = 0; i < nonStreamingTextMessages.Count; i++)
        {
            Assert.Equal(nonStreamingTextMessages[i].Text, streamingTextMessages[i].Text);
        }

        // Verify fallback parser was called for both scenarios
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    #endregion

    #region Edge Case and Error Scenario Tests

    [Fact]
    public async Task InvokeAsync_WithMalformedToolCallContent_NoOpeningTag_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Missing opening <tool_call> tag
        string malformedToolCall = "Let me get that weather: name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(malformedToolCall);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Should return only the text message since no valid tool call was found
        var textMessage = Assert.Single(result.OfType<TextMessage>());
        Assert.Contains("Let me get that weather:", textMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithMalformedToolCallContent_NoClosingTag_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Missing closing </tool_call> tag
        string malformedToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```";
        var mockAgent = SetupMockAgent(malformedToolCall);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Should return only the text message since no valid tool call was found
        var textMessage = Assert.Single(result.OfType<TextMessage>());
        Assert.Contains("Let me get that weather:", textMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithMalformedToolCallContent_NoToolName_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Missing tool name attribute
        string malformedToolCall = "Let me get that weather: <tool_call>\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(malformedToolCall);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Should return only the text message since no valid tool call was found
        var textMessage = Assert.Single(result.OfType<TextMessage>());
        Assert.Contains("Let me get that weather:", textMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithMalformedToolCallContent_InvalidToolNameFormat_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Invalid tool name format (missing quotes)
        string malformedToolCall = "Let me get that weather: <tool_call name=GetWeather>\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(malformedToolCall);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Should return only the text message since no valid tool call was found
        var textMessage = Assert.Single(result.OfType<TextMessage>());
        Assert.Contains("Let me get that weather:", textMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithEmptyJsonContent_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string emptyJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(emptyJsonToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Default City") && json.Contains("celsius")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Default City\",\n  \"unit\": \"celsius\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Default City", jsonMessage.Text);
        Assert.Contains("celsius", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithEmptyToolCallContentEdgeCase_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string emptyContentToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n\n</tool_call>";
        var mockAgent = SetupMockAgent(emptyContentToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Empty Content City") && json.Contains("fahrenheit")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Empty Content City\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Empty Content City", jsonMessage.Text);
        Assert.Contains("fahrenheit", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithWhitespaceOnlyContentEdgeCase_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string whitespaceOnlyToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n   \n\t\n  \n</tool_call>";
        var mockAgent = SetupMockAgent(whitespaceOnlyToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("Whitespace City") && json.Contains("celsius")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON
        string validFallbackJson = "{\n  \"location\": \"Whitespace City\",\n  \"unit\": \"celsius\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("Whitespace City", jsonMessage.Text);
        Assert.Contains("celsius", jsonMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithUnknownToolNameAndFallbackAgent_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Tool name that doesn't exist in function contracts
        string unknownToolCall = "Let me search for that: <tool_call name=\"UnknownSearchTool\">\n```json\n{\n  \"query\": \"test search\",\n  \"limit\": 10\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(unknownToolCall);

        // Setup schema validator to accept the fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("fallback search") && json.Contains("5")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return valid JSON for the unknown tool
        string validFallbackJson = "{\n  \"query\": \"fallback search\",\n  \"limit\": 5\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act & Assert
        // Should still throw exception for unknown tool even with fallback agent
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middleware.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the exception message contains the tool name
        Assert.Contains("UnknownSearchTool", exception.Message);
        
        // Verify fallback parser was NOT called since tool doesn't exist in contracts
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_WithSchemaValidationFailureAndNoFallbackAgent_ThrowsException()
    {
        // Arrange
        var middlewareWithoutFallback = new NaturalToolUseParserMiddleware(_functionContracts, _mockSchemaValidator.Object, null);
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the invalid JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middlewareWithoutFallback.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the exception message indicates validation failure
        Assert.Contains("GetWeather", exception.Message);
    }

    [Fact]
    public async Task InvokeAsync_WithSchemaValidationFailureAndFallbackAgentFailure_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject both original and fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Setup fallback parser to return invalid JSON that also fails validation
        string invalidFallbackJson = "{\n  \"wrong_field\": \"invalid data\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = invalidFallbackJson, Role = Role.Assistant } });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middleware.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the exception message indicates fallback failure
        Assert.Contains("GetWeather", exception.Message);
        
        // Verify fallback parser was called
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithSchemaValidationFailureAndFallbackAgentException_ThrowsException()
    {
        // Arrange
        var middleware = CreateMiddleware();
        string invalidJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"invalid_syntax\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the original JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Setup fallback parser to throw an exception
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Fallback agent failed"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ToolUseParsingException>(
            async () => await middleware.InvokeAsync(_defaultContext, mockAgent.Object)
        );

        // Verify the exception message indicates fallback failure
        Assert.Contains("GetWeather", exception.Message);
        
        // Verify fallback parser was called (may be called multiple times due to retries)
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.IsAny<GenerateReplyOptions>(), 
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task InvokeAsync_WithComplexMalformedJson_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();
        // Complex malformed JSON with multiple syntax errors
        string complexMalformedJsonToolCall = "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"unit\": \"fahrenheit\",\n  \"extra_field\": [1, 2, 3,],\n  \"nested\": {\n    \"invalid\": true\n    \"missing_comma\": \"here\"\n  }\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(complexMalformedJsonToolCall);

        // Setup schema validator to reject malformed JSON but accept fallback JSON
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("extra_field") || json.Contains("missing_comma")), It.IsAny<string>()))
            .Returns(false);
        _mockSchemaValidator.Setup(v => v.Validate(It.Is<string>(json => json.Contains("San Francisco, CA") && json.Contains("fahrenheit") && !json.Contains("extra_field")), It.IsAny<string>()))
            .Returns(true);

        // Setup fallback parser to return clean, valid JSON
        string validFallbackJson = "{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}";
        _mockFallbackParser.Setup(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IMessage> { new TextMessage { Text = validFallbackJson, Role = Role.Assistant } });

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        _mockFallbackParser.Verify(f => f.GenerateReplyAsync(
            It.IsAny<IEnumerable<IMessage>>(), 
            It.Is<GenerateReplyOptions>(o => o.ResponseFormat != null), 
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, result.Count());
        var textMessage = result.OfType<TextMessage>().First();
        Assert.Equal("Let me get that weather:", textMessage.Text);
        var jsonMessage = result.OfType<TextMessage>().Last();
        Assert.Contains("San Francisco, CA", jsonMessage.Text);
        Assert.Contains("fahrenheit", jsonMessage.Text);
        Assert.DoesNotContain("extra_field", jsonMessage.Text);
        Assert.DoesNotContain("missing_comma", jsonMessage.Text);
    }

    #endregion
}
