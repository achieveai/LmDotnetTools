using System.Net;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Provides standardized chat completion test data for all providers
/// Contains common scenarios for testing chat completion functionality
/// </summary>
public static class ChatCompletionTestData
{
    /// <summary>
    /// Creates a simple successful chat completion response
    /// </summary>
    /// <param name="content">Response content</param>
    /// <param name="model">Model name</param>
    /// <param name="promptTokens">Number of prompt tokens</param>
    /// <param name="completionTokens">Number of completion tokens</param>
    /// <returns>JSON response string</returns>
    public static string CreateSuccessfulResponse(
        string content = "Hello! How can I help you today?",
        string model = "test-model",
        int promptTokens = 10,
        int completionTokens = 20
    )
    {
        var response = new
        {
            id = "test-response-id",
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
            },
        };

        return System.Text.Json.JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Creates a streaming chat completion response chunk
    /// </summary>
    /// <param name="content">Content delta</param>
    /// <param name="model">Model name</param>
    /// <param name="finishReason">Finish reason (null for ongoing, "stop" for complete)</param>
    /// <returns>JSON response string</returns>
    public static string CreateStreamingChunk(
        string content = "Hello",
        string model = "test-model",
        string? finishReason = null
    )
    {
        var response = new
        {
            id = "test-stream-id",
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = finishReason == null ? (string?)"assistant" : null, content },
                    finish_reason = finishReason,
                },
            },
        };

        return System.Text.Json.JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Creates an error response for testing error handling
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <param name="errorType">Error type</param>
    /// <param name="statusCode">HTTP status code</param>
    /// <returns>JSON error response string</returns>
    public static string CreateErrorResponse(
        string errorMessage = "Invalid request",
        string errorType = "invalid_request_error",
        HttpStatusCode statusCode = HttpStatusCode.BadRequest
    )
    {
        var response = new
        {
            error = new
            {
                message = errorMessage,
                type = errorType,
                code = ((int)statusCode).ToString(),
            },
        };

        return System.Text.Json.JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Creates a rate limit error response
    /// </summary>
    /// <returns>JSON rate limit error response</returns>
    public static string CreateRateLimitErrorResponse()
    {
        return CreateErrorResponse("Rate limit exceeded", "rate_limit_exceeded", HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Creates an authentication error response
    /// </summary>
    /// <returns>JSON authentication error response</returns>
    public static string CreateAuthenticationErrorResponse()
    {
        return CreateErrorResponse("Invalid API key", "authentication_error", HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Creates a server error response
    /// </summary>
    /// <returns>JSON server error response</returns>
    public static string CreateServerErrorResponse()
    {
        return CreateErrorResponse("Internal server error", "server_error", HttpStatusCode.InternalServerError);
    }

    /// <summary>
    /// Creates test data for various chat completion scenarios
    /// </summary>
    /// <returns>Test data for chat completion scenarios</returns>
    public static IEnumerable<object[]> GetChatCompletionTestCases()
    {
        return
        [
            [CreateSuccessfulResponse(), HttpStatusCode.OK, true, "Successful response should work"],
            [CreateErrorResponse(), HttpStatusCode.BadRequest, false, "Bad request should fail"],
            [
                CreateRateLimitErrorResponse(),
                HttpStatusCode.TooManyRequests,
                false,
                "Rate limit should fail",
            ],
            [
                CreateAuthenticationErrorResponse(),
                HttpStatusCode.Unauthorized,
                false,
                "Auth error should fail",
            ],
            [
                CreateServerErrorResponse(),
                HttpStatusCode.InternalServerError,
                false,
                "Server error should fail",
            ],
        ];
    }

    /// <summary>
    /// Creates test data for streaming scenarios
    /// </summary>
    /// <returns>Test data for streaming scenarios</returns>
    public static IEnumerable<string> GetStreamingTestChunks()
    {
        return
        [
            CreateStreamingChunk("Hello", "test-model"),
            CreateStreamingChunk(" there", "test-model"),
            CreateStreamingChunk("!", "test-model"),
            CreateStreamingChunk("", "test-model", "stop"),
        ];
    }

    /// <summary>
    /// Creates a complete streaming response sequence
    /// </summary>
    /// <returns>Complete streaming response as SSE format</returns>
    public static string CreateStreamingResponse()
    {
        var chunks = GetStreamingTestChunks();
        var sseResponse = string.Join("\n\n", chunks.Select(chunk => $"data: {chunk}"));
        return sseResponse + "\n\ndata: [DONE]\n\n";
    }

    /// <summary>
    /// Creates test messages for different conversation scenarios
    /// </summary>
    /// <returns>Test data for different message scenarios</returns>
    public static IEnumerable<object[]> GetMessageTestCases()
    {
        return
        [
            [
                new[] { ProviderTestDataGenerator.CreateTestMessage("user", "Hello") },
                "Single user message",
            ],
            [ProviderTestDataGenerator.CreateTestMessages(), "Multi-turn conversation"],
            [
                new[]
                {
                    ProviderTestDataGenerator.CreateTestMessage("system", "You are a helpful assistant"),
                    ProviderTestDataGenerator.CreateTestMessage("user", "What is 2+2?"),
                },
                "System message with user question",
            ],
        ];
    }

    /// <summary>
    /// Creates test data for token usage scenarios
    /// </summary>
    /// <returns>Test data for token usage scenarios</returns>
    public static IEnumerable<object[]> GetTokenUsageTestCases()
    {
        return
        [
            [10, 20, 30, "Normal token usage"],
            [0, 5, 5, "No prompt tokens"],
            [100, 0, 100, "No completion tokens"],
            [1000, 2000, 3000, "High token usage"],
        ];
    }
}
