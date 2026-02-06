using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
///     HTTP-level unit tests for OpenClient using shared test infrastructure
///     Tests retry logic, performance tracking, and validation
/// </summary>
public class OpenClientHttpTests : LoggingTestBase
{
    private readonly ILogger<OpenClient> _openClientLogger;
    private readonly IPerformanceTracker _performanceTracker;

    public OpenClientHttpTests(ITestOutputHelper output) : base(output)
    {
        _openClientLogger = LoggerFactory.CreateLogger<OpenClient>();
        _performanceTracker = new PerformanceTracker();
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_WithRetryOnTransientFailure_ShouldSucceed()
    {
        // Arrange
        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(
            LoggerFactory,
            statusSequence: [HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK],
            chunkDelayMs: 0
        );
        var client = new OpenClient(httpClient, GetApiBaseUrl(), _performanceTracker, _openClientLogger, RetryOptions.FastForTests);

        var request = new ChatCompletionRequest(
            "qwen/qwen3-235b-a22b",
            [new ChatMessage { Role = RoleEnum.User, Content = ChatMessage.CreateContent("Hello") }]
        );

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.NotNull(response.Choices);
        Assert.True(response.Choices.Count > 0);
        Assert.False(string.IsNullOrWhiteSpace(response.Choices![0]!.Message!.Content?.Get<string>()));

        // Verify performance tracking
        var metrics = _performanceTracker.GetProviderStatistics("OpenAI");
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.SuccessfulRequests);
        Assert.True(metrics.AverageRequestDuration > TimeSpan.Zero);
    }

    [Fact]
    public void CreateChatCompletionsAsync_WithInvalidApiKey_ShouldThrowValidationException()
    {
        // Arrange & Act & Assert
        _ = Assert.Throws<ArgumentException>(() => new OpenClient("test key", GetApiBaseUrl()));
    }

    [Fact]
    public void CreateChatCompletionsAsync_WithInvalidUrl_ShouldThrowValidationException()
    {
        // Arrange & Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new OpenClient("valid-api-key-test", "invalid-url"));

        Assert.Contains("Base URL must be a valid HTTP or HTTPS URL", exception.Message);
    }

    [Theory]
    [MemberData(nameof(GetRetryScenarios))]
    public async Task CreateChatCompletionsAsync_RetryScenarios_ShouldHandleCorrectly(
        HttpStatusCode[] statusCodes,
        bool shouldSucceed
    )
    {
        // Arrange
        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(
            LoggerFactory,
            statusSequence: statusCodes,
            chunkDelayMs: 0
        );
        var client = new OpenClient(httpClient, GetApiBaseUrl(), _performanceTracker, _openClientLogger, RetryOptions.FastForTests);

        var request = new ChatCompletionRequest(
            "qwen/qwen3-235b-a22b",
            [new ChatMessage { Role = RoleEnum.User, Content = ChatMessage.CreateContent("Test") }]
        );

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
        var metrics = _performanceTracker.GetProviderStatistics("OpenAI");
        Assert.NotNull(metrics);
        Assert.True(metrics.TotalRequests > 0);
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_PerformanceTracking_ShouldRecordMetrics()
    {
        // Arrange
        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(LoggerFactory, chunkDelayMs: 0);
        var client = new OpenClient(httpClient, GetApiBaseUrl(), _performanceTracker, _openClientLogger, RetryOptions.FastForTests);

        var request = new ChatCompletionRequest(
            "qwen/qwen3-235b-a22b",
            [new ChatMessage { Role = RoleEnum.User, Content = ChatMessage.CreateContent("Hello") }]
        );

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert
        var metrics = _performanceTracker.GetProviderStatistics("OpenAI");
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.SuccessfulRequests);
        Assert.Equal(0, metrics.FailedRequests);
        Assert.True(metrics.AverageRequestDuration > TimeSpan.Zero);
        Assert.True(metrics.TotalTokensProcessed > 0);
    }

    [Fact]
    public async Task StreamingChatCompletionsAsync_WithRetryOnFailure_ShouldSucceed()
    {
        // Arrange
        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(
            LoggerFactory,
            statusSequence: [HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK],
            chunkDelayMs: 0
        );
        var client = new OpenClient(httpClient, GetApiBaseUrl(), _performanceTracker, _openClientLogger, RetryOptions.FastForTests);

        var request = new ChatCompletionRequest(
            "qwen/qwen3-235b-a22b",
            [new ChatMessage { Role = RoleEnum.User, Content = ChatMessage.CreateContent("Hello") }]
        );

        // Act
        var responseStream = client.StreamingChatCompletionsAsync(request);
        var chunks = new List<ChatCompletionResponse>();

        await foreach (var chunk in responseStream)
        {
            chunks.Add(chunk);
        }

        // Assert
        Assert.NotEmpty(chunks);

        // Verify performance tracking for streaming
        var metrics = _performanceTracker.GetProviderStatistics("OpenAI");
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.SuccessfulRequests);
    }

    /// <summary>
    ///     Tests streaming with InstructionChainParser pattern.
    ///     Uses TestSseMessageHandler for unified test setup.
    /// </summary>
    [Fact]
    public async Task StreamingChatCompletionsAsync_WithInstructionChain_ShouldSucceed()
    {
        // Arrange - Using TestSseMessageHandler with instruction chain in user message
        Logger.LogInformation("Starting StreamingChatCompletionsAsync_WithInstructionChain_ShouldSucceed test");

        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(
            LoggerFactory,
            wordsPerChunk: 3,
            chunkDelayMs: 10
        );

        // Create OpenClient with test handler - using test-mode URL
        var client = new OpenClient(httpClient, GetApiBaseUrl(), _performanceTracker, _openClientLogger);

        Logger.LogDebug("Created OpenClient with TestSseMessageHandler");

        // User message with instruction chain embedded
        var userMessage = """
            Hello, can you help me with a task?
            <|instruction_start|>
            {"instruction_chain": [
                {"id_message": "Streaming test response", "messages":[{"text_message":{"length":20}}]}
            ]}
            <|instruction_end|>
            """;

        var request = new ChatCompletionRequest(
            "test-model",
            [new ChatMessage { Role = RoleEnum.User, Content = ChatMessage.CreateContent(userMessage) }]
        )
        {
            Stream = true,
        };

        Logger.LogDebug("Created ChatCompletionRequest with instruction chain");

        // Act
        var responseStream = client.StreamingChatCompletionsAsync(request);
        var chunks = new List<ChatCompletionResponse>();
        var allContent = new System.Text.StringBuilder();

        await foreach (var chunk in responseStream)
        {
            chunks.Add(chunk);
            var content = chunk.Choices?.FirstOrDefault()?.Delta?.Content;
            if (content != null)
            {
                allContent.Append(content);
            }
        }

        Logger.LogInformation("Received {ChunkCount} chunks, total content: {Content}", chunks.Count, allContent.ToString());

        // Assert
        Assert.NotEmpty(chunks);
        Assert.True(allContent.Length > 0, "Should have received text content from instruction chain");

        Logger.LogInformation("StreamingChatCompletionsAsync_WithInstructionChain_ShouldSucceed completed successfully");
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

        // Scenario: Max retries exceeded (BaseHttpService.ExecuteHttpWithRetryAsync has maxRetries=2)
        yield return new object[]
        {
            new[]
            {
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.ServiceUnavailable,
            },
            false, // should fail after max retries (1 initial + 2 retries = 3 attempts needed to exhaust)
        };

        // Scenario: Success on first try
        yield return new object[]
        {
            new[] { HttpStatusCode.OK },
            true, // should succeed
        };
    }

    private static string GetApiBaseUrl()
    {
        return "http://test-mode/v1";
    }
}
