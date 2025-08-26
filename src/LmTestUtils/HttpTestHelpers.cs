using System.Net;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Common HTTP testing helpers and utilities
/// Provides standardized HTTP mocking and testing patterns
/// Shared utility for all LmDotnetTools provider testing
/// </summary>
public static class HttpTestHelpers
{
    /// <summary>
    /// Creates an HttpClient with a base address for testing
    /// </summary>
    /// <param name="handler">Message handler to use</param>
    /// <param name="baseAddress">Base address for the client</param>
    /// <returns>Configured HttpClient for testing</returns>
    public static HttpClient CreateTestHttpClient(
        HttpMessageHandler handler,
        string baseAddress = "https://api.test.com"
    )
    {
        return new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
    }

    /// <summary>
    /// Creates an HttpClient with a simple JSON response handler
    /// </summary>
    /// <param name="jsonResponse">JSON response to return</param>
    /// <param name="statusCode">HTTP status code to return</param>
    /// <param name="baseAddress">Base address for the client</param>
    /// <returns>HttpClient configured with the specified response</returns>
    public static HttpClient CreateTestHttpClientWithJsonResponse(
        string jsonResponse,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string baseAddress = "https://api.test.com"
    )
    {
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler(jsonResponse, statusCode);
        return CreateTestHttpClient(handler, baseAddress);
    }

    /// <summary>
    /// Creates an HttpClient that simulates retry scenarios
    /// </summary>
    /// <param name="failureCount">Number of failures before success</param>
    /// <param name="successResponse">Response to return on success</param>
    /// <param name="failureStatusCode">Status code to return for failures</param>
    /// <param name="baseAddress">Base address for the client</param>
    /// <returns>HttpClient configured for retry testing</returns>
    public static HttpClient CreateRetryTestHttpClient(
        int failureCount,
        string successResponse,
        HttpStatusCode failureStatusCode = HttpStatusCode.InternalServerError,
        string baseAddress = "https://api.test.com"
    )
    {
        var handler = FakeHttpMessageHandler.CreateRetryHandler(
            failureCount,
            successResponse,
            failureStatusCode
        );
        return CreateTestHttpClient(handler, baseAddress);
    }

    /// <summary>
    /// Creates an HttpClient that returns different responses based on request patterns
    /// </summary>
    /// <param name="responses">Dictionary mapping request patterns to responses</param>
    /// <param name="baseAddress">Base address for the client</param>
    /// <returns>HttpClient configured with conditional responses</returns>
    public static HttpClient CreateMultiResponseHttpClient(
        Dictionary<string, (string json, HttpStatusCode status)> responses,
        string baseAddress = "https://api.test.com"
    )
    {
        var handler = FakeHttpMessageHandler.CreateMultiResponseHandler(responses);
        return CreateTestHttpClient(handler, baseAddress);
    }

    /// <summary>
    /// Creates an HttpClient with OpenAI-formatted response for testing
    /// </summary>
    /// <param name="content">The response content text</param>
    /// <param name="model">The model name</param>
    /// <param name="promptTokens">Number of prompt tokens</param>
    /// <param name="completionTokens">Number of completion tokens</param>
    /// <param name="baseAddress">Base address for the client</param>
    /// <returns>HttpClient configured with OpenAI response</returns>
    public static HttpClient CreateOpenAITestHttpClient(
        string content = "Hello! How can I help you today?",
        string model = "gpt-4",
        int promptTokens = 10,
        int completionTokens = 20,
        string baseAddress = "https://api.openai.com/v1"
    )
    {
        var handler = FakeHttpMessageHandler.CreateOpenAIResponseHandler(
            content,
            model,
            promptTokens,
            completionTokens
        );
        return CreateTestHttpClient(handler, baseAddress);
    }

    /// <summary>
    /// Creates an HttpClient with Anthropic-formatted response for testing
    /// </summary>
    /// <param name="content">The response content text</param>
    /// <param name="model">The model name</param>
    /// <param name="inputTokens">Number of input tokens</param>
    /// <param name="outputTokens">Number of output tokens</param>
    /// <param name="baseAddress">Base address for the client</param>
    /// <returns>HttpClient configured with Anthropic response</returns>
    public static HttpClient CreateAnthropicTestHttpClient(
        string content = "Hello! How can I help you today?",
        string model = "claude-3-sonnet-20240229",
        int inputTokens = 10,
        int outputTokens = 20,
        string baseAddress = "https://api.anthropic.com/v1"
    )
    {
        var handler = FakeHttpMessageHandler.CreateAnthropicResponseHandler(
            content,
            model,
            inputTokens,
            outputTokens
        );
        return CreateTestHttpClient(handler, baseAddress);
    }

    /// <summary>
    /// Creates an HttpClient with request capture capability for testing
    /// </summary>
    /// <param name="responseJson">JSON response to return</param>
    /// <param name="capturedRequest">Out parameter to receive captured request</param>
    /// <param name="statusCode">HTTP status code to return</param>
    /// <param name="baseAddress">Base address for the client</param>
    /// <returns>HttpClient configured with request capture</returns>
    public static HttpClient CreateRequestCaptureHttpClient(
        string responseJson,
        out CapturedRequestContainer capturedRequest,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string baseAddress = "https://api.test.com"
    )
    {
        var handler = FakeHttpMessageHandler.CreateRequestCaptureHandler(
            responseJson,
            out capturedRequest,
            statusCode
        );
        return CreateTestHttpClient(handler, baseAddress);
    }

    /// <summary>
    /// Creates HTTP content from a string with JSON content type
    /// </summary>
    /// <param name="content">String content</param>
    /// <param name="encoding">Text encoding (default: UTF8)</param>
    /// <returns>HttpContent configured for JSON</returns>
    public static HttpContent CreateJsonContent(string content, Encoding? encoding = null)
    {
        return new StringContent(content, encoding ?? Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Creates HTTP content from an object serialized as JSON
    /// </summary>
    /// <param name="obj">Object to serialize</param>
    /// <param name="encoding">Text encoding (default: UTF8)</param>
    /// <returns>HttpContent with serialized object</returns>
    public static HttpContent CreateJsonContent(object obj, Encoding? encoding = null)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(obj);
        return CreateJsonContent(json, encoding);
    }

    /// <summary>
    /// Validates that an HTTP request contains expected headers
    /// </summary>
    /// <param name="request">HTTP request to validate</param>
    /// <param name="expectedHeaders">Dictionary of expected headers</param>
    /// <param name="throwOnMissing">Whether to throw exception if headers are missing</param>
    /// <returns>True if all expected headers are present</returns>
    public static bool ValidateRequestHeaders(
        HttpRequestMessage request,
        Dictionary<string, string> expectedHeaders,
        bool throwOnMissing = true
    )
    {
        foreach (var header in expectedHeaders)
        {
            var found =
                request.Headers.TryGetValues(header.Key, out var values)
                && values.Contains(header.Value);

            if (!found)
            {
                if (throwOnMissing)
                {
                    throw new AssertionException(
                        $"Expected header '{header.Key}: {header.Value}' not found in request"
                    );
                }
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Validates that an HTTP request contains expected JSON content
    /// </summary>
    /// <param name="request">HTTP request to validate</param>
    /// <param name="expectedKeys">Keys that should be present in JSON</param>
    /// <param name="throwOnMissing">Whether to throw exception if keys are missing</param>
    /// <returns>True if all expected keys are present</returns>
    public static async Task<bool> ValidateRequestJsonContent(
        HttpRequestMessage request,
        string[] expectedKeys,
        bool throwOnMissing = true
    )
    {
        if (request.Content == null)
        {
            if (throwOnMissing)
            {
                throw new AssertionException("Request content is null");
            }
            return false;
        }

        var content = await request.Content.ReadAsStringAsync();
        using var document = System.Text.Json.JsonDocument.Parse(content);
        var root = document.RootElement;

        foreach (var key in expectedKeys)
        {
            if (!root.TryGetProperty(key, out _))
            {
                if (throwOnMissing)
                {
                    throw new AssertionException(
                        $"Expected JSON key '{key}' not found in request content"
                    );
                }
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Creates a standardized error response for testing
    /// </summary>
    /// <param name="statusCode">HTTP status code</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="errorCode">Error code (optional)</param>
    /// <returns>HTTP response message with error content</returns>
    public static HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string errorMessage,
        string? errorCode = null
    )
    {
        var errorResponse = new
        {
            error = new
            {
                message = errorMessage,
                code = errorCode ?? statusCode.ToString(),
                type = "error",
            },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(errorResponse);
        var response = new HttpResponseMessage(statusCode) { Content = CreateJsonContent(json) };

        return response;
    }

    /// <summary>
    /// Creates common test scenarios for HTTP status codes
    /// </summary>
    /// <returns>Test data for various HTTP status code scenarios</returns>
    public static IEnumerable<object[]> GetHttpStatusCodeTestCases()
    {
        return new List<object[]>
        {
            new object[] { HttpStatusCode.OK, true, "200 OK should succeed" },
            new object[] { HttpStatusCode.Created, true, "201 Created should succeed" },
            new object[] { HttpStatusCode.BadRequest, false, "400 Bad Request should fail" },
            new object[] { HttpStatusCode.Unauthorized, false, "401 Unauthorized should fail" },
            new object[] { HttpStatusCode.Forbidden, false, "403 Forbidden should fail" },
            new object[] { HttpStatusCode.NotFound, false, "404 Not Found should fail" },
            new object[]
            {
                HttpStatusCode.InternalServerError,
                false,
                "500 Internal Server Error should fail (but be retryable)",
            },
            new object[]
            {
                HttpStatusCode.BadGateway,
                false,
                "502 Bad Gateway should fail (but be retryable)",
            },
            new object[]
            {
                HttpStatusCode.ServiceUnavailable,
                false,
                "503 Service Unavailable should fail (but be retryable)",
            },
        };
    }

    /// <summary>
    /// Custom exception for test assertions
    /// </summary>
    public class AssertionException : Exception
    {
        public AssertionException(string message)
            : base(message) { }

        public AssertionException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
