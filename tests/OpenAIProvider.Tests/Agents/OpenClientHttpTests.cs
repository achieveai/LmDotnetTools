using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using dotenv.net;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
/// HTTP-level unit tests for OpenClient using shared test infrastructure
/// Tests retry logic, performance tracking, and validation
/// </summary>
public class OpenClientHttpTests
{
    private readonly ILogger<OpenClient> _logger;
    private readonly IPerformanceTracker _performanceTracker;

    public OpenClientHttpTests()
    {
        _logger = TestLoggerFactory.CreateLogger<OpenClient>();
        _performanceTracker = new PerformanceTracker();
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_WithRetryOnTransientFailure_ShouldSucceed()
    {
        // Arrange
        var successResponse = ChatCompletionTestData.CreateSuccessfulResponse(
            "Test response",
            "qwen/qwen3-235b-a22b");

        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            failureCount: 2, 
            successResponse: successResponse,
            failureStatus: HttpStatusCode.ServiceUnavailable);

        var httpClient = new HttpClient(fakeHandler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv(), _performanceTracker, _logger);

        var request = new ChatCompletionRequest(
            model: "qwen/qwen3-235b-a22b",
            messages: new List<ChatMessage>
            {
                new() { Role = RoleEnum.User, Content = ChatMessage.CreateContent("Hello") }
            });

        // Act
        var response = await client.CreateChatCompletionsAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.Equal("Test response", response.Choices[0].Message.Content?.Get<string>());
        
        // Verify performance tracking
        var metrics = _performanceTracker.GetProviderStatistics("OpenAI");
        Assert.NotNull(metrics);
        Assert.Equal(1, metrics.TotalRequests);
        Assert.Equal(1, metrics.SuccessfulRequests);
        Assert.True(metrics.AverageRequestDuration > TimeSpan.Zero);
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_WithInvalidApiKey_ShouldThrowValidationException()
    {
        // Arrange & Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            new OpenClient("test key", GetApiBaseUrlFromEnv()));
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_WithInvalidUrl_ShouldThrowValidationException()
    {
        // Arrange & Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            new OpenClient("valid-api-key-test", "invalid-url"));
        
        Assert.Contains("Base URL must be a valid HTTP or HTTPS URL", exception.Message);
    }

    [Theory]
    [MemberData(nameof(GetRetryScenarios))]
    public async Task CreateChatCompletionsAsync_RetryScenarios_ShouldHandleCorrectly(
        HttpStatusCode[] statusCodes, 
        bool shouldSucceed, 
        int expectedRetries)
    {
        // Arrange
        var successResponse = ChatCompletionTestData.CreateSuccessfulResponse(
            "Success",
            "qwen/qwen3-235b-a22b");

        var fakeHandler = FakeHttpMessageHandler.CreateStatusCodeSequenceHandler(
            statusCodes,
            successResponse);

        var httpClient = new HttpClient(fakeHandler);
        var client = new OpenClient(
            httpClient,
            GetApiBaseUrlFromEnv(),
            _performanceTracker,
            _logger);

        var request = new ChatCompletionRequest(
            model: "qwen/qwen3-235b-a22b",
            messages: new List<ChatMessage>
            {
                new() {
                    Role = RoleEnum.User,
                    Content = ChatMessage.CreateContent("Test")
                }
            });

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
        var metrics = _performanceTracker.GetProviderStatistics("OpenAI");
        Assert.NotNull(metrics);
        Assert.True(metrics.TotalRequests > 0);
    }

    [Fact]
    public async Task CreateChatCompletionsAsync_PerformanceTracking_ShouldRecordMetrics()
    {
        // Arrange
        var successResponse = ChatCompletionTestData.CreateSuccessfulResponse("Test response", "qwen/qwen3-235b-a22b");
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            successResponse, 
            HttpStatusCode.OK);

        var httpClient = new HttpClient(fakeHandler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv(), _performanceTracker, _logger);

        var request = new ChatCompletionRequest(
            model: "qwen/qwen3-235b-a22b",
            messages: new List<ChatMessage>
            {
                new() { Role = RoleEnum.User, Content = ChatMessage.CreateContent("Hello") }
            });

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
        var streamingResponse = ChatCompletionTestData.CreateStreamingResponse();
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            failureCount: 1,
            successResponse: streamingResponse,
            failureStatus: HttpStatusCode.ServiceUnavailable);

        var httpClient = new HttpClient(fakeHandler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv(), _performanceTracker, _logger);

        var request = new ChatCompletionRequest(
            model: "qwen/qwen3-235b-a22b",
            messages: new List<ChatMessage>
            {
                new() { Role = RoleEnum.User, Content = ChatMessage.CreateContent("Hello") }
            });

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
    /// Helper method to get API key from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiKeyFromEnv()
    {
        return EnvironmentHelper.GetApiKeyFromEnv("LLM_API_KEY", 
            new[] { "OPENAI_API_KEY" }, 
            "test-api-key");
    }

    /// <summary>
    /// Helper method to get API base URL from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiBaseUrlFromEnv()
    {
        return EnvironmentHelper.GetApiBaseUrlFromEnv("LLM_API_BASE_URL", 
            new[] { "OPENAI_API_URL" }, 
            "https://api.openai.com/v1");
    }
} 
