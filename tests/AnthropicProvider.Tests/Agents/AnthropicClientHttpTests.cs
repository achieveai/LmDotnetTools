using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

/// <summary>
/// HTTP-level unit tests for AnthropicClient using shared test infrastructure
/// Tests retry logic, performance tracking, validation, and Anthropic-specific usage mapping
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
        var successResponse = CreateAnthropicSuccessResponse("Test response from Claude", "claude-3-sonnet-20240229");
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            failureCount: 2,
            successResponse: successResponse,
            failureStatus: HttpStatusCode.ServiceUnavailable);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages = new List<AnthropicMessage>
            {
                new() 
                { 
                    Role = "user", 
                    Content = new List<AnthropicContent>
                    {
                        new AnthropicContent { Type = "text", Text = "Hello Claude" }
                    }
                }
            }
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
    public async Task CreateChatCompletionsAsync_WithInvalidApiKey_ShouldThrowValidationException()
    {
        // Arrange & Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            new AnthropicClient(""));
        
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
            Messages = new List<AnthropicMessage>() // Empty messages
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateChatCompletionsAsync(request));
    }

    [Theory]
    [MemberData(nameof(GetRetryScenarios))]
    public async Task CreateChatCompletionsAsync_RetryScenarios_ShouldHandleCorrectly(
        HttpStatusCode[] statusCodes, 
        bool shouldSucceed, 
        int expectedRetries)
    {
        // Arrange
        var successResponse = CreateAnthropicSuccessResponse("Success", "claude-3-sonnet-20240229");
        var fakeHandler = FakeHttpMessageHandler.CreateStatusCodeSequenceHandler(
            statusCodes,
            successResponse);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages = new List<AnthropicMessage>
            {
                new() 
                { 
                    Role = "user", 
                    Content = new List<AnthropicContent>
                    {
                        new AnthropicContent { Type = "text", Text = "Test" }
                    }
                }
            }
        };

        // Act & Assert
        if (shouldSucceed)
        {
            var response = await client.CreateChatCompletionsAsync(request);
            Assert.NotNull(response);
        }
        else
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => 
                client.CreateChatCompletionsAsync(request));
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
        var successResponse = CreateAnthropicSuccessResponseWithSpecificUsage(inputTokens: 50, outputTokens: 25);
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            successResponse, 
            HttpStatusCode.OK);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages = new List<AnthropicMessage>
            {
                new() 
                { 
                    Role = "user", 
                    Content = new List<AnthropicContent>
                    {
                        new AnthropicContent { Type = "text", Text = "Hello" }
                    }
                }
            }
        };

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert - Verify Anthropic-specific usage mapping
        var metrics = _performanceTracker.GetProviderStatistics("Anthropic");
        Assert.NotNull(metrics);
        
        // Anthropic InputTokens → LmCore PromptTokens, OutputTokens → CompletionTokens
        Assert.True(metrics.TotalTokensProcessed == 75); // 50 + 25
        
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
            failureCount: 1,
            successResponse: string.Join("", streamingEvents),
            failureStatus: HttpStatusCode.ServiceUnavailable);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Stream = true,
            Messages = new List<AnthropicMessage>
            {
                new() 
                { 
                    Role = "user", 
                    Content = new List<AnthropicContent>
                    {
                        new AnthropicContent { Type = "text", Text = "Hello" }
                    }
                }
            }
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
        var successResponse = CreateAnthropicSuccessResponseWithSpecificUsage(inputTokens: 100, outputTokens: 50);
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            successResponse, 
            HttpStatusCode.OK);

        var httpClient = new HttpClient(fakeHandler);
        var client = new AnthropicClient(httpClient, _performanceTracker, _logger);

        var request = new AnthropicRequest
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1000,
            Messages = new List<AnthropicMessage>
            {
                new() 
                { 
                    Role = "user", 
                    Content = new List<AnthropicContent>
                    {
                        new AnthropicContent { Type = "text", Text = "Complex reasoning task" }
                    }
                }
            }
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
            Content = new List<AnthropicResponseContent>
            {
                new AnthropicResponseTextContent
                {
                    Type = "text",
                    Text = "Test response from Claude"
                }
            },
            Usage = new AnthropicUsage
            {
                InputTokens = 10,
                OutputTokens = 5
            }
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
            Content = new List<AnthropicResponseContent>
            {
                new AnthropicResponseTextContent
                {
                    Type = "text",
                    Text = "Test response with specific usage"
                }
            },
            Usage = new AnthropicUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            }
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
            2     // expected retries
        };

        // Scenario: Non-retryable error (Bad Request)
        yield return new object[]
        {
            new[] { HttpStatusCode.BadRequest },
            false, // should fail
            0      // expected retries (no retry)
        };

        // Scenario: Max retries exceeded
        yield return new object[]
        {
            new[] { 
                HttpStatusCode.ServiceUnavailable, 
                HttpStatusCode.ServiceUnavailable, 
                HttpStatusCode.ServiceUnavailable, 
                HttpStatusCode.ServiceUnavailable 
            },
            false, // should fail after max retries
            3      // expected retries
        };

        // Scenario: Success on first try
        yield return new object[]
        {
            new[] { HttpStatusCode.OK },
            true, // should succeed
            0     // expected retries
        };
    }

    /// <summary>
    /// Creates an Anthropic-specific successful response JSON
    /// </summary>
    private static string CreateAnthropicSuccessResponse(string content = "Hello! How can I help you today?", string model = "claude-3-sonnet-20240229")
    {
        var response = new
        {
            type = "message",  // Must be first property for polymorphic deserialization
            id = "msg_test123",
            role = "assistant",
            content = new[]
            {
                new
                {
                    type = "text",  // Must be first property for polymorphic deserialization
                    text = content
                }
            },
            model = model,
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = 10,
                output_tokens = 20
            }
        };

        return System.Text.Json.JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Creates an Anthropic-specific successful response JSON with specific usage
    /// </summary>
    private static string CreateAnthropicSuccessResponseWithSpecificUsage(int inputTokens, int outputTokens)
    {
        var response = new
        {
            type = "message",  // Must be first property for polymorphic deserialization
            id = "msg_test123",
            role = "assistant",
            content = new[]
            {
                new
                {
                    type = "text",  // Must be first property for polymorphic deserialization
                    text = "Test response with specific usage"
                }
            },
            model = "claude-3-sonnet-20240229",
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens
            }
        };

        return System.Text.Json.JsonSerializer.Serialize(response);
    }
} 