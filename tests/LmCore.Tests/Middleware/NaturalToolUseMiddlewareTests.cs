using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class NaturalToolUseMiddlewareTests
{
    // Common test context
    private readonly MiddlewareContext _defaultContext;
    private readonly List<FunctionContract> _functionContracts;
    private readonly Dictionary<string, Func<string, Task<string>>> _functionMap;
    private readonly Mock<IAgent> _mockFallbackParser;
    private readonly Mock<IJsonSchemaValidator> _mockSchemaValidator;

    public NaturalToolUseMiddlewareTests()
    {
        _mockSchemaValidator = new Mock<IJsonSchemaValidator>();
        _mockFallbackParser = new Mock<IAgent>();
        _functionContracts =
        [
            new FunctionContract
            {
                Name = "GetWeather",
                Description = "A test tool",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "location",
                        ParameterType = new JsonSchemaObject { Type = "string" },
                        Description = "First parameter",
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "unit",
                        ParameterType = new JsonSchemaObject { Type = "string" },
                        Description = "Second parameter",
                        IsRequired = true,
                    },
                ],
            },
        ];

        _functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            { "GetWeather", async args => await Task.FromResult("{\"temperature\": 72, \"conditions\": \"sunny\"}") },
        };

        // Initialize default context
        _defaultContext = new MiddlewareContext([new TextMessage { Text = "Hello", Role = Role.User }]);
    }

    // Helper method to create middleware with default configuration
    private NaturalToolUseMiddleware CreateMiddleware()
    {
        return new NaturalToolUseMiddleware(
            _functionContracts,
            _functionMap,
            _mockFallbackParser.Object,
            "TestMiddleware",
            _mockSchemaValidator.Object
        );
    }

    // Helper method to setup mock agent with text response
    private static Mock<IAgent> SetupMockAgent(string responseText)
    {
        var mockAgent = new Mock<IAgent>();
        _ = mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new TextMessage { Text = responseText, Role = Role.Assistant }]);
        return mockAgent;
    }

    // Helper method to setup a streaming agent with a single text message
    private static Mock<IStreamingAgent> SetupStreamingAgent(string text)
    {
        var mockStreamingAgent = new Mock<IStreamingAgent>();
        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(CreateSimpleAsyncEnumerable(text)));

        return mockStreamingAgent;
    }

    private static async IAsyncEnumerable<IMessage> CreateSimpleAsyncEnumerable(string text)
    {
        await Task.Yield(); // Add await to make this truly async

        if (string.IsNullOrEmpty(text))
        {
            yield return new TextUpdateMessage { Text = text, Role = Role.Assistant };
            yield break;
        }

        // Split text into random-sized chunks (3-8 characters each)
        var random = new Random(42); // Use fixed seed for reproducible tests
        var currentPosition = 0;

        while (currentPosition < text.Length)
        {
            // Generate random chunk size between 3-8 characters
            var chunkSize = Math.Min(9, text.Length - currentPosition);
            chunkSize = chunkSize > 3 ? random.Next(3, chunkSize) : chunkSize;
            var chunk = text.Substring(currentPosition, chunkSize);

            yield return new TextUpdateMessage { Text = chunk, Role = Role.Assistant };

            currentPosition += chunkSize;
            await Task.Delay(1); // Small delay to simulate streaming
        }
    }

    private static async Task<List<IMessage>> CollectAsyncEnumerable(IAsyncEnumerable<IMessage> asyncEnumerable)
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
        var validToolCall =
            "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(validToolCall);
        _ = _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        Assert.NotEmpty(result);

        // Should get both text messages and tool call aggregate messages
        var textMessages = result.OfType<TextMessage>().ToList();
        var toolCallAggregates = result.OfType<ToolsCallAggregateMessage>().ToList();

        Assert.NotEmpty(textMessages); // Should have prefix text message
        _ = Assert.Single(toolCallAggregates); // Should have exactly one tool call

        // Verify the tool call was processed correctly
        var toolCallMessage = toolCallAggregates.First();
        Assert.Equal("GetWeather", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("San Francisco, CA", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionArgs);

        // Verify prefix text is present
        var prefixTextMessage = textMessages.First();
        Assert.Equal("Here's the weather:", prefixTextMessage.Text);
    }

    [Fact]
    public async Task InvokeAsync_WithInvalidToolCall_UsesFallbackParser()
    {
        // Arrange
        var middleware = CreateMiddleware();

        // Create a response with proper tool call format but invalid JSON inside
        var invalidJsonToolCall =
            "Let me get that weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\"\n  \"invalid_json\": true,\n}\n```\n</tool_call>";
        var mockAgent = SetupMockAgent(invalidJsonToolCall);

        // Setup schema validator to reject the JSON (invalid schema)
        _ = _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        // Setup fallback parser to return valid JSON (this is what the fallback parser is supposed to do)
        var validFallbackJson =
            "```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```";
        _ = _mockFallbackParser
            .Setup(f =>
                f.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([new TextMessage { Text = validFallbackJson, Role = Role.Assistant }]);

        // Make schema validator accept the fixed JSON from the fallback parser
        _ = _mockSchemaValidator
            .SetupSequence(v => v.Validate(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false) // First call with invalid JSON
            .Returns(true); // Second call with fixed JSON from fallback

        // Act
        var result = await middleware.InvokeAsync(_defaultContext, mockAgent.Object);

        // Assert
        // Verify the fallback parser was called
        _mockFallbackParser.Verify(
            f =>
                f.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.AtLeastOnce
        );

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

        // Since we're now streaming TextUpdateMessage objects, check for those instead
        Assert.Contains(result, m => m is TextUpdateMessage);

        // Verify that we get the expected text content in the update messages
        var textUpdates = result.OfType<TextUpdateMessage>().ToList();
        Assert.NotEmpty(textUpdates);

        // Verify all messages have correct role and properties
        Assert.All(
            textUpdates,
            msg =>
            {
                Assert.Equal(Role.Assistant, msg.Role);
                Assert.NotNull(msg.Text);
                Assert.False(string.IsNullOrEmpty(msg.Text));
            }
        );

        // Check that the concatenation of all text updates equals the complete text
        var concatenated = string.Concat(textUpdates.Select(t => t.Text));
        Assert.Equal("Hi there!", concatenated);

        // Verify no tool call messages were generated
        var toolCallMessages = result.OfType<TextMessage>().Where(m => m.Text.StartsWith("Tool Call:")).ToList();
        Assert.Empty(toolCallMessages);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithValidToolCall_ReturnsMessages()
    {
        // Arrange
        var middleware = CreateMiddleware();
        var validToolCall =
            "Here's the weather: <tool_call name=\"GetWeather\">\n```json\n{\n  \"location\": \"San Francisco, CA\",\n  \"unit\": \"fahrenheit\"\n}\n```\n</tool_call>";
        var mockAgent = SetupStreamingAgent(validToolCall);
        _ = _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Act
        var streamingResult = await middleware.InvokeStreamingAsync(_defaultContext, mockAgent.Object);
        var result = await CollectAsyncEnumerable(streamingResult);

        // Assert
        Assert.NotEmpty(result);

        // Verify we get both text updates and tool call messages
        var textUpdates = result.OfType<TextUpdateMessage>().ToList();
        var toolCallMessages = result.OfType<ToolsCallAggregateMessage>().ToList();

        Assert.NotEmpty(textUpdates);
        _ = Assert.Single(toolCallMessages); // Should have exactly one tool call

        // Verify the tool call was parsed correctly
        var toolCallMessage = toolCallMessages.First();
        Assert.Equal("GetWeather", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("San Francisco, CA", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("fahrenheit", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionArgs);

        // Verify prefix text is present in updates
        var allUpdateText = string.Concat(textUpdates.Select(t => t.Text));
        Assert.Contains("Here's the weather:", allUpdateText);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithPartialToolCallAcrossChunks_ReturnsMessages()
    {
        // Arrange
        var middleware = CreateMiddleware();
        var mockStreamingAgent = new Mock<IStreamingAgent>();

        // Create a streaming response where tool call spans multiple chunks
        var chunks = new List<string>
        {
            "Let me check the weather for you: <tool_call name=\"GetWeat",
            "her\">\n```json\n{\n  \"location\": \"New York\",\n  \"unit\": \"cel",
            "sius\"\n}\n```\n</tool_call> There you go!",
        };

        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(CreateChunkedAsyncEnumerable(chunks)));

        _ = _mockSchemaValidator.Setup(v => v.Validate(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        // Act
        var streamingResult = await middleware.InvokeStreamingAsync(_defaultContext, mockStreamingAgent.Object);
        var result = await CollectAsyncEnumerable(streamingResult);

        // Assert
        Assert.NotEmpty(result);

        // Verify we get both text updates and tool call messages
        var textUpdates = result.OfType<TextUpdateMessage>().ToList();
        var toolCallMessages = result.OfType<ToolsCallAggregateMessage>().ToList();

        Assert.NotEmpty(textUpdates);
        _ = Assert.Single(toolCallMessages); // Should have exactly one tool call

        // Verify the tool call was parsed correctly despite being chunked
        var toolCallMessage = toolCallMessages.First();
        Assert.Equal("GetWeather", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionName);
        Assert.Contains("New York", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionArgs);
        Assert.Contains("celsius", toolCallMessage.ToolsCallMessage.ToolCalls[0].FunctionArgs);

        // Verify both prefix and suffix text are present in updates
        var allUpdateText = string.Concat(textUpdates.Select(t => t.Text));
        Assert.Contains("Let me check the weather for you:", allUpdateText);
        Assert.Contains("There you go!", allUpdateText);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WithIncompleteToolCall_FlushesBufferCorrectly()
    {
        // Arrange
        var middleware = CreateMiddleware();
        var mockStreamingAgent = new Mock<IStreamingAgent>();

        // Create a streaming response with incomplete tool call that should be held back
        var chunks = new List<string> { "Here's some text and then a partial <tool_ca" };

        _ = mockStreamingAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.FromResult(CreateChunkedAsyncEnumerable(chunks)));

        // Act
        var streamingResult = await middleware.InvokeStreamingAsync(_defaultContext, mockStreamingAgent.Object);
        var result = await CollectAsyncEnumerable(streamingResult);

        // Assert
        Assert.NotEmpty(result);

        var textUpdates = result.OfType<TextUpdateMessage>().ToList();
        Assert.NotEmpty(textUpdates);

        // The safe part should be emitted, but the potential tool call start should be in the final flush
        var allUpdateText = string.Concat(textUpdates.Select(t => t.Text));
        Assert.Contains("Here's some text and then a partial <tool_ca", allUpdateText);
    }

    private static async IAsyncEnumerable<IMessage> CreateChunkedAsyncEnumerable(IEnumerable<string> chunks)
    {
        await Task.Yield();

        foreach (var chunk in chunks)
        {
            yield return new TextUpdateMessage { Text = chunk, Role = Role.Assistant };
            await Task.Delay(1); // Small delay to simulate streaming
        }
    }
}
