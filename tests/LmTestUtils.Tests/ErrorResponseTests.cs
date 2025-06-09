using System.Net;
using System.Text.Json;
using Xunit;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace LmTestUtils.Tests;

public class ErrorResponseTests
{
    [Fact]
    public async Task RespondWithAnthropicError_ShouldGenerateValidAnthropicErrorFormat()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAnthropicError(HttpStatusCode.BadRequest, "invalid_request_error", "Invalid request parameters")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.anthropic.com/v1/messages");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("Invalid request parameters", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RespondWithAnthropicRateLimit_ShouldGenerateRateLimitError()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAnthropicRateLimit("Rate limit exceeded")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.anthropic.com/v1/messages");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("rate_limit_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("Rate limit exceeded", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RespondWithAnthropicAuthError_ShouldGenerateAuthenticationError()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAnthropicAuthError("Invalid API key")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.anthropic.com/v1/messages");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("authentication_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("Invalid API key", json.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RespondWithOpenAIError_ShouldGenerateValidOpenAIErrorFormat()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithOpenAIError(HttpStatusCode.BadRequest, "invalid_request_error", "Invalid model specified", "model", "invalid_model")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.openai.com/v1/chat/completions");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("Invalid model specified", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("model", json.RootElement.GetProperty("error").GetProperty("param").GetString());
        Assert.Equal("invalid_model", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RespondWithOpenAIRateLimit_ShouldGenerateRateLimitError()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithOpenAIRateLimit("Rate limit exceeded")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.openai.com/v1/chat/completions");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("Rate limit exceeded", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("rate_limit_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task RespondWithOpenAIAuthError_ShouldGenerateAuthenticationError()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithOpenAIAuthError("Invalid API key")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.openai.com/v1/chat/completions");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("Invalid API key", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("invalid_api_key", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RespondWithStatusCodeSequence_ShouldReturnSequenceOfStatusCodes()
    {
        // Arrange
        var statusCodes = new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.OK };
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithStatusCodeSequence(statusCodes, "Success after retries")
            .Build();

        using var client = new HttpClient(handler);

        // Act & Assert - First request (503)
        var response1 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response1.StatusCode);

        // Second request (429)
        var response2 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.TooManyRequests, response2.StatusCode);

        // Third request (200 with success message)
        var response3 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.OK, response3.StatusCode);
        
        var content = await response3.Content.ReadAsStringAsync();
        Assert.Equal("Success after retries", content);
    }

    [Fact]
    public async Task RespondWithRetrySequence_ShouldUseStandardRetryPattern()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithRetrySequence("Final success")
            .Build();

        using var client = new HttpClient(handler);

        // Act & Assert - First request (503)
        var response1 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response1.StatusCode);

        // Second request (429)
        var response2 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.TooManyRequests, response2.StatusCode);

        // Third request (200)
        var response3 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.OK, response3.StatusCode);
        
        var content = await response3.Content.ReadAsStringAsync();
        Assert.Equal("Final success", content);
    }

    [Fact]
    public async Task RespondWithRateLimitError_ShouldIncludeRetryAfterHeader()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithRateLimitError(60, "anthropic")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.anthropic.com/v1/messages");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal("60", response.Headers.GetValues("Retry-After").First());
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("rate_limit_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task RespondWithRateLimitError_Generic_ShouldUseGenericFormat()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithRateLimitError(30)
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.example.com/v1/test");

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal("30", response.Headers.GetValues("Retry-After").First());
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("Rate limit exceeded. Please retry after 30 seconds.", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(30, json.RootElement.GetProperty("error").GetProperty("retry_after").GetInt32());
    }

    [Fact]
    public async Task RespondWithAuthenticationError_ShouldReturn401()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAuthenticationError("openai")
            .Build();

        using var client = new HttpClient(handler);

        // Act
        var response = await client.GetAsync("https://api.openai.com/v1/chat/completions");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        
        Assert.Equal("Invalid API key provided.", json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", json.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("invalid_api_key", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RespondWithTimeout_ShouldThrowTaskCanceledException()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithTimeout(100) // 100ms timeout
            .Build();

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(50) // Client timeout shorter than mock timeout
        };

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await client.GetAsync("https://api.anthropic.com/v1/messages");
        });
    }

    [Fact]
    public async Task RespondWithTimeout_WithLongerClientTimeout_ShouldCompleteAfterDelay()
    {
        // Arrange
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithTimeout(50) // 50ms timeout
            .Build();

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5) // Client timeout longer than mock timeout
        };

        var startTime = DateTime.UtcNow;

        // Act
        var response = await client.GetAsync("https://api.anthropic.com/v1/messages");

        // Assert
        var elapsed = DateTime.UtcNow - startTime;
        Assert.True(elapsed.TotalMilliseconds >= 45, $"Expected at least 45ms delay, got {elapsed.TotalMilliseconds}ms");
        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);
    }

    [Fact]
    public async Task MultipleErrorProviders_ShouldUseFirstMatchingProvider()
    {
        // Arrange - Test demonstrates that the first provider that can handle the request is used
        // This validates the MockHttpHandler's behavior: FirstOrDefault(p => p.CanHandle(request, requestIndex))
        var handler = MockHttpHandlerBuilder.Create()
            .RespondWithAnthropicRateLimit()         // This will be used for ALL requests since CanHandle = true
            .RespondWithAnthropicAuthError()         // These are never reached
            .RespondWithAnthropicMessage("Finally successful")
            .Build();

        using var client = new HttpClient(handler);

        // Act & Assert - All requests should get the same response (rate limit) 
        // because that's the first provider and it can handle all requests
        
        // First request - Anthropic rate limit (expected)
        var response1 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.TooManyRequests, response1.StatusCode);
        var content1 = await response1.Content.ReadAsStringAsync();
        Assert.Contains("rate_limit_error", content1);

        // Second request - Also rate limit (because first provider handles all)
        var response2 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.TooManyRequests, response2.StatusCode);
        var content2 = await response2.Content.ReadAsStringAsync();
        Assert.Contains("rate_limit_error", content2);

        // Third request - Also rate limit (same reason)
        var response3 = await client.GetAsync("https://api.anthropic.com/v1/messages");
        Assert.Equal(HttpStatusCode.TooManyRequests, response3.StatusCode);
        var content3 = await response3.Content.ReadAsStringAsync();
        Assert.Contains("rate_limit_error", content3);
    }
} 