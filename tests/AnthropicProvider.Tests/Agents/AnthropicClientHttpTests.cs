using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

/// <summary>
///     HTTP-level unit tests for AnthropicClient using shared test infrastructure
///     Tests retry logic, performance tracking, validation, and Anthropic-specific usage mapping
/// </summary>
public class AnthropicClientHttpTests
{
    private readonly ILogger<AnthropicClient> _logger;
    private readonly IPerformanceTracker _performanceTracker;

    public AnthropicClientHttpTests()
    {
        _logger = TestLoggerFactory.CreateLogger<AnthropicClient>();
        _performanceTracker = new PerformanceTracker();
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_WithRetryOnTransientFailure_ShouldSucceed()
    {
        // Arrange
        var successResponse = CreateAnthropicSuccessResponse("Test response from Claude");
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            2,
            successResponse,
            HttpStatusCode.ServiceUnavailable
        );

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "Hello Claude" }],
                },
            ],
        };

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert
        Assert.NotNull(response);
        var textContent = response.Content[0] as AnthropicResponseTextContent;
        Assert.NotNull(textContent);
        Assert.Equal("Test response from Claude", textContent.Text);

        // Verify performance tracking with Anthropic-specific usage mapping
        var metrics = _performanceTracker.GetProviderStatistics("Anthropic");
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.SuccessfulRequests);
        Assert.True(metrics.AverageRequestDuration > TimeSpan.Zero);
        Assert.True(metrics.TotalTokensProcessed > 0);
    }

    [Fact]
    public void CreateChatCompletionsAsync_WithInvalidApiKey_ShouldThrowValidationException()
    {
        // Arrange & Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new AnthropicClient(""));

        Assert.Contains("Value cannot be null, empty, or whitespace", exception.Message);
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_WithEmptyMessages_ShouldThrowValidationException()
    {
        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{}");
        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages = [], // Empty messages
        };

        // Act & Assert
        _ = await Assert.ThrowsAsync<ArgumentException>(() => client.CreateChatCompletionsAsync(request));
    }

    [Theory]
    [MemberData(nameof(GetRetryScenarios))]
    public async Task CreateChatCompletionsAsync_RetryScenarios_ShouldHandleCorrectly(
        HttpStatusCode[] statusCodes,
        bool shouldSucceed
    )
    {
        // Arrange
        var successResponse = CreateAnthropicSuccessResponse("Success");
        var fakeHandler = FakeHttpMessageHandler.CreateStatusCodeSequenceHandler(statusCodes, successResponse);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "Test" }],
                },
            ],
        };

        // Act & Assert
        if (shouldSucceed)
        {
            var response = await client.CreateChatCompletionsAsync(request);
            Assert.NotNull(response);
        }
        else
        {
            _ = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.CreateChatCompletionsAsync(request));
        }

        // Verify performance tracking captured the metrics
        var metrics = _performanceTracker.GetProviderStatistics("Anthropic");
        Assert.NotNull(metrics);
        Assert.True(metrics.TotalRequests > 0);
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_AnthropicUsageMapping_ShouldMapTokensCorrectly()
    {
        // Arrange
        var successResponse = CreateAnthropicSuccessResponseWithSpecificUsage(50, 25);
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(successResponse);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "Hello" }],
                },
            ],
        };

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert - Verify Anthropic-specific usage mapping
        var metrics = _performanceTracker.GetProviderStatistics("Anthropic");
        Assert.NotNull(metrics);

        // Anthropic InputTokens → LmCore PromptTokens, OutputTokens → CompletionTokens
        Assert.Equal(75, metrics.TotalTokensProcessed); // 50 + 25

        // Verify response usage
        Assert.NotNull(response.Usage);
        Assert.Equal(50, response.Usage.InputTokens);
        Assert.Equal(25, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task StreamingChatCompletionsAsync_WithRetryOnFailure_ShouldSucceed()
    {
        // Arrange
        var streamingEvents = CreateAnthropicStreamingResponse();
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            1,
            string.Join("", streamingEvents),
            HttpStatusCode.ServiceUnavailable
        );

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Stream = true,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "Hello" }],
                },
            ],
        };

        // Act
        var responseStream = await client.StreamingChatCompletionsAsync(request);
        var events = new List<AnthropicStreamEvent>();

        await foreach (var streamEvent in responseStream)
        {
            events.Add(streamEvent);
        }

        // Assert
        Assert.NotEmpty(events);

        // Verify performance tracking for streaming
        var metrics = _performanceTracker.GetProviderStatistics("Anthropic");
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.SuccessfulRequests);
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_PerformanceTracking_ShouldRecordDetailedMetrics()
    {
        // Arrange
        var successResponse = CreateAnthropicSuccessResponseWithSpecificUsage(100, 50);
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(successResponse);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "Complex reasoning task" }],
                },
            ],
        };

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert comprehensive performance metrics
        var metrics = _performanceTracker.GetProviderStatistics("Anthropic");
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.SuccessfulRequests);
        Assert.Equal(0, metrics.FailedRequests);
        Assert.True(metrics.AverageRequestDuration > TimeSpan.Zero);
        Assert.Equal(150, metrics.TotalTokensProcessed); // 100 input + 50 output

        // Verify the actual response
        Assert.NotNull(response);
        Assert.NotNull(response.Usage);
        Assert.Equal(100, response.Usage.InputTokens);
        Assert.Equal(50, response.Usage.OutputTokens);
    }

    private static AnthropicResponse CreateAnthropicSuccessResponse()
    {
        return new AnthropicResponse
        {
            Id = "msg_test123",
            Type = "message",
            Role = "assistant",
            Model = "claude-3-sonnet-20240229",
            Content = [new AnthropicResponseTextContent { Type = "text", Text = "Test response from Claude" }],
            Usage = new AnthropicUsage { InputTokens = 10, OutputTokens = 5 },
        };
    }

    private static AnthropicResponse CreateAnthropicSuccessResponseWithUsage(int inputTokens, int outputTokens)
    {
        return new AnthropicResponse
        {
            Id = "msg_test123",
            Type = "message",
            Role = "assistant",
            Model = "claude-3-sonnet-20240229",
            Content = [new AnthropicResponseTextContent { Type = "text", Text = "Test response with specific usage" }],
            Usage = new AnthropicUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
        };
    }

    private static IEnumerable<string> CreateAnthropicStreamingResponse()
    {
        yield return "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_123\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-3-sonnet-20240229\"}}\n\n";
        yield return "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n";
        yield return "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Streaming\"}}\n\n";
        yield return "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" response\"}}\n\n";
        yield return "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";
        yield return "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":15}}\n\n";
        yield return "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    }

    public static IEnumerable<object[]> GetRetryScenarios()
    {
        // Scenario: Transient failures that should retry and eventually succeed
        yield return new object[]
        {
            new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.OK },
            true, // should succeed
        };

        // Scenario: Non-retryable error (Bad Request)
        yield return new object[]
        {
            new[] { HttpStatusCode.BadRequest },
            false, // should fail
        };

        // Scenario: Max retries exceeded
        yield return new object[]
        {
            new[]
            {
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
            },
            false, // should fail after max retries
        };

        // Scenario: Success on first try
        yield return new object[]
        {
            new[] { HttpStatusCode.OK },
            true, // should succeed
        };
    }

    /// <summary>
    ///     Creates an Anthropic-specific successful response JSON
    /// </summary>
    private static string CreateAnthropicSuccessResponse(
        string content = "Hello! How can I help you today?",
        string model = "claude-3-sonnet-20240229"
    )
    {
        var response = new
        {
            type = "message", // Must be first property for polymorphic deserialization
            id = "msg_test123",
            role = "assistant",
            content = new[]
            {
                new
                {
                    type = "text", // Must be first property for polymorphic deserialization
                    text = content,
                },
            },
            model,
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 20 },
        };

        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    ///     Creates an Anthropic-specific successful response JSON with specific usage
    /// </summary>
    private static string CreateAnthropicSuccessResponseWithSpecificUsage(int inputTokens, int outputTokens)
    {
        var response = new
        {
            type = "message", // Must be first property for polymorphic deserialization
            id = "msg_test123",
            role = "assistant",
            content = new[]
            {
                new
                {
                    type = "text", // Must be first property for polymorphic deserialization
                    text = "Test response with specific usage",
                },
            },
            model = "claude-3-sonnet-20240229",
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        };

        return JsonSerializer.Serialize(response);
    }
}
