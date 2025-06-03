using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmEmbeddings.Providers.OpenAI;
using LmEmbeddings.Models;
using LmEmbeddings.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit;

namespace LmEmbeddings.Tests.Providers.OpenAI;

/// <summary>
/// HTTP-based tests for OpenAIEmbeddingService using proper HttpClient mocking
/// Following patterns from mocking-httpclient.md
/// </summary>
public class OpenAIEmbeddingServiceHttpTests
{
    private readonly ILogger<OpenAIEmbeddingService> _logger;

    public OpenAIEmbeddingServiceHttpTests()
    {
        _logger = new TestLogger<OpenAIEmbeddingService>();
    }

    [Theory]
    [MemberData(nameof(SuccessfulOpenAIResponseTestCases))]
    public async Task GenerateEmbeddingsAsync_SuccessfulOpenAIResponse_ReturnsCorrectEmbeddings(
        EmbeddingRequest request,
        string mockApiResponse,
        int expectedEmbeddingCount,
        string description)
    {
        Debug.WriteLine($"Testing successful OpenAI response: {description}");
        Debug.WriteLine($"Request: {request.Inputs.Count} inputs, Model: {request.Model}, ApiType: {request.ApiType}");
        Debug.WriteLine($"Expected embedding count: {expectedEmbeddingCount}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(mockApiResponse);
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.openai.com")
        };

        var options = new OpenAIEmbeddingOptions
        {
            ApiKey = "test-api-key",
            BaseUrl = "https://api.openai.com",
            DefaultModel = "text-embedding-3-small"
        };

        var service = new OpenAIEmbeddingService(_logger, httpClient, options);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.GenerateEmbeddingsAsync(request);
        stopwatch.Stop();

        // Assert
        Debug.WriteLine($"Execution time: {stopwatch.ElapsedMilliseconds}ms");
        Debug.WriteLine($"Result: {result.Embeddings.Count} embeddings returned");
        Debug.WriteLine($"Model: {result.Model}");

        Assert.NotNull(result);
        Assert.Equal(expectedEmbeddingCount, result.Embeddings.Count);
        Assert.NotNull(result.Model);

        foreach (var embedding in result.Embeddings)
        {
            Debug.WriteLine($"Embedding {embedding.Index}: Vector length = {embedding.Vector.Length}");
            Assert.NotNull(embedding.Vector);
            Assert.True(embedding.Vector.Length > 0);
            Assert.All(embedding.Vector, v => Assert.True(v >= -1.0f && v <= 1.0f));
        }

        Debug.WriteLine("✓ All embeddings validated successfully");
    }

    [Theory]
    [MemberData(nameof(HttpErrorResponseTestCases))]
    public async Task GenerateEmbeddingsAsync_HttpErrors_HandlesErrorsCorrectly(
        EmbeddingRequest request,
        HttpStatusCode errorStatusCode,
        string errorResponse,
        Type expectedExceptionType,
        string description)
    {
        Debug.WriteLine($"Testing HTTP error handling: {description}");
        Debug.WriteLine($"Error status: {errorStatusCode}");
        Debug.WriteLine($"Expected exception: {expectedExceptionType.Name}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(errorResponse, errorStatusCode);
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.openai.com")
        };

        var options = new OpenAIEmbeddingOptions
        {
            ApiKey = "test-api-key",
            BaseUrl = "https://api.openai.com",
            DefaultModel = "text-embedding-3-small"
        };

        var service = new OpenAIEmbeddingService(_logger, httpClient, options);

        // Act & Assert
        var exception = await Assert.ThrowsAsync(expectedExceptionType,
            () => service.GenerateEmbeddingsAsync(request));

        Debug.WriteLine($"Exception caught: {exception.Message}");
        Assert.NotNull(exception);
        Debug.WriteLine("✓ Error handled correctly");
    }

    [Theory]
    [MemberData(nameof(RetryScenarioTestCases))]
    public async Task GenerateEmbeddingsAsync_RetryScenarios_RetriesCorrectly(
        EmbeddingRequest request,
        int failureCount,
        string successResponse,
        HttpStatusCode failureStatus,
        string description)
    {
        Debug.WriteLine($"Testing retry scenario: {description}");
        Debug.WriteLine($"Failure count: {failureCount}, Failure status: {failureStatus}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            failureCount, successResponse, failureStatus);
        
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.openai.com")
        };

        var options = new OpenAIEmbeddingOptions
        {
            ApiKey = "test-api-key",
            BaseUrl = "https://api.openai.com",
            DefaultModel = "text-embedding-3-small"
        };

        var service = new OpenAIEmbeddingService(_logger, httpClient, options);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.GenerateEmbeddingsAsync(request);
        stopwatch.Stop();

        // Assert
        Debug.WriteLine($"Execution time: {stopwatch.ElapsedMilliseconds}ms (including retries)");
        Debug.WriteLine($"Result: {result.Embeddings.Count} embeddings returned after retries");

        Assert.NotNull(result);
        Assert.True(result.Embeddings.Count > 0);
        Debug.WriteLine("✓ Retry logic worked correctly");
    }

    [Theory]
    [MemberData(nameof(RequestValidationTestCases))]
    public async Task GenerateEmbeddingsAsync_RequestValidation_ValidatesHttpPayload(
        EmbeddingRequest request,
        string expectedHttpMethod,
        string expectedEndpoint,
        Dictionary<string, object> expectedPayloadFields,
        string description)
    {
        Debug.WriteLine($"Testing HTTP request validation: {description}");
        Debug.WriteLine($"Expected method: {expectedHttpMethod}, Endpoint: {expectedEndpoint}");

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler((httpRequest, cancellationToken) =>
        {
            capturedRequest = httpRequest;
            
            // Return a valid response
            var response = CreateValidOpenAIResponse(request.Inputs.Count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.openai.com")
        };

        var options = new OpenAIEmbeddingOptions
        {
            ApiKey = "test-api-key",
            BaseUrl = "https://api.openai.com",
            DefaultModel = "text-embedding-3-small"
        };

        var service = new OpenAIEmbeddingService(_logger, httpClient, options);

        // Act
        await service.GenerateEmbeddingsAsync(request);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Equal(expectedHttpMethod, capturedRequest.Method.Method);
        Assert.Equal(expectedEndpoint, capturedRequest.RequestUri?.PathAndQuery);

        // Validate request payload
        if (capturedRequest.Content != null)
        {
            var requestBody = await capturedRequest.Content.ReadAsStringAsync();
            Debug.WriteLine($"Request body: {requestBody}");

            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);
            Assert.NotNull(payload);

            foreach (var expectedField in expectedPayloadFields)
            {
                Assert.True(payload.ContainsKey(expectedField.Key), 
                    $"Missing expected field: {expectedField.Key}");
                Debug.WriteLine($"✓ Found expected field: {expectedField.Key}");
            }
        }

        Debug.WriteLine("✓ HTTP request validation passed");
    }

    [Theory]
    [MemberData(nameof(ApiTypeFormattingTestCases))]
    public async Task GenerateEmbeddingsAsync_ApiTypeFormatting_FormatsRequestCorrectly(
        EmbeddingRequest request,
        Dictionary<string, object> expectedFields,
        Dictionary<string, object> forbiddenFields,
        string description)
    {
        Debug.WriteLine($"Testing API type formatting: {description}");
        
        HttpRequestMessage? capturedRequest = null;
        
        var fakeHandler = new FakeHttpMessageHandler((httpRequest, cancellationToken) =>
        {
            capturedRequest = httpRequest;
            
            // Return a simple success response without processing it
            // We're only testing the request formatting, not the response processing
            var simpleResponse = new
            {
                @object = "list",
                data = new object[0], // Empty array to avoid processing issues
                model = request.Model,
                usage = new { prompt_tokens = 0, total_tokens = 0 }
            };
            
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(simpleResponse), System.Text.Encoding.UTF8, "application/json")
            });
        });

        var httpClient = new HttpClient(fakeHandler);
        var options = new OpenAIEmbeddingOptions
        {
            ApiKey = "test-api-key",
            BaseUrl = "https://api.openai.com",
            DefaultModel = "text-embedding-3-small"
        };
        var service = new OpenAIEmbeddingService(_logger, httpClient, options);

        // Act - This should fail because we're returning empty data, but we only care about the request
        try
        {
            await service.GenerateEmbeddingsAsync(request);
        }
        catch
        {
            // Expected to fail due to empty response, but we captured the request
        }

        // Assert - Validate the HTTP request was formatted correctly
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        
        var requestContent = await capturedRequest.Content!.ReadAsStringAsync();
        var requestJson = JsonDocument.Parse(requestContent);
        var root = requestJson.RootElement;

        Debug.WriteLine($"Request payload: {requestContent}");

        // Validate expected fields are present and correct
        foreach (var expectedField in expectedFields)
        {
            Assert.True(root.TryGetProperty(expectedField.Key, out var property), 
                $"Expected field '{expectedField.Key}' not found in request");
            
            var expectedValue = expectedField.Value;
            if (expectedValue is string[] stringArray)
            {
                var actualArray = property.EnumerateArray().Select(e => e.GetString()).ToArray();
                Assert.Equal(stringArray, actualArray);
            }
            else if (expectedValue is bool boolValue)
            {
                Assert.Equal(boolValue, property.GetBoolean());
            }
            else if (expectedValue is string stringValue)
            {
                Assert.Equal(stringValue, property.GetString());
            }
            
            Debug.WriteLine($"✓ Field '{expectedField.Key}' = {expectedValue}");
        }

        // Validate forbidden fields are not present
        foreach (var forbiddenField in forbiddenFields)
        {
            Assert.False(root.TryGetProperty(forbiddenField.Key, out _), 
                $"Forbidden field '{forbiddenField.Key}' found in request");
            Debug.WriteLine($"✓ Forbidden field '{forbiddenField.Key}' correctly absent");
        }

        Debug.WriteLine($"✓ API type formatting test passed: {description}");
    }

    private static string CreateValidOpenAIResponse(int embeddingCount, string format = "base64")
    {
        var embeddings = new List<object>();
        for (int i = 0; i < embeddingCount; i++)
        {
            var floatArray = GenerateTestEmbeddingArray(1536);
            object embeddingData;
            
            if (format == "float")
            {
                embeddingData = floatArray; // Return float array directly
            }
            else
            {
                // Generate a proper base64-encoded embedding
                var bytes = new byte[floatArray.Length * 4];
                Buffer.BlockCopy(floatArray, 0, bytes, 0, bytes.Length);
                embeddingData = Convert.ToBase64String(bytes);
            }
            
            embeddings.Add(new
            {
                @object = "embedding",
                embedding = embeddingData,
                index = i
            });
        }

        var response = new
        {
            @object = "list",
            data = embeddings,
            model = "text-embedding-3-small",
            usage = new
            {
                prompt_tokens = 10,
                total_tokens = 10
            }
        };

        return JsonSerializer.Serialize(response);
    }

    private static float[] GenerateTestEmbeddingArray(int size)
    {
        var random = new Random(42); // Fixed seed for consistent tests
        var embedding = new float[size];
        for (int i = 0; i < size; i++)
        {
            embedding[i] = (float)(random.NextDouble() * 2.0 - 1.0); // Range [-1, 1]
        }
        return embedding;
    }

    public static IEnumerable<object[]> SuccessfulOpenAIResponseTestCases => new List<object[]>
    {
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "Hello world" },
                Model = "text-embedding-3-small",
                ApiType = EmbeddingApiType.Default
            },
            CreateValidOpenAIResponse(1, "base64"), // Default is base64
            1,
            "Single input with Default API type"
        },
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "Hello", "World", "Test" },
                Model = "text-embedding-3-large",
                ApiType = EmbeddingApiType.Default,
                EncodingFormat = "float"
            },
            CreateValidOpenAIResponse(3, "float"), // Use float format
            3,
            "Multiple inputs with encoding format"
        }
    };

    public static IEnumerable<object[]> HttpErrorResponseTestCases => new List<object[]>
    {
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test" },
                Model = "invalid-model",
                ApiType = EmbeddingApiType.Default
            },
            HttpStatusCode.BadRequest,
            "{\"error\": {\"message\": \"Invalid model\", \"type\": \"invalid_request_error\"}}",
            typeof(HttpRequestException),
            "Invalid model returns 400 Bad Request"
        },
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test" },
                Model = "text-embedding-3-small",
                ApiType = EmbeddingApiType.Default
            },
            HttpStatusCode.Unauthorized,
            "{\"error\": {\"message\": \"Invalid API key\", \"type\": \"invalid_request_error\"}}",
            typeof(HttpRequestException),
            "Invalid API key returns 401 Unauthorized"
        },
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test" },
                Model = "text-embedding-3-small",
                ApiType = EmbeddingApiType.Default
            },
            HttpStatusCode.InternalServerError,
            "{\"error\": {\"message\": \"Internal server error\", \"type\": \"server_error\"}}",
            typeof(HttpRequestException),
            "Server error returns 500 Internal Server Error"
        }
    };

    public static IEnumerable<object[]> RetryScenarioTestCases => new List<object[]>
    {
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test" },
                Model = "text-embedding-3-small",
                ApiType = EmbeddingApiType.Default
            },
            2, // Fail twice, then succeed
            CreateValidOpenAIResponse(1, "base64"), // Default is base64
            HttpStatusCode.InternalServerError,
            "Retry after 2 server errors"
        },
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test1", "test2" },
                Model = "text-embedding-3-large",
                ApiType = EmbeddingApiType.Default,
                EncodingFormat = "float" // Explicitly set to float
            },
            1, // Fail once, then succeed
            CreateValidOpenAIResponse(2, "float"), // Use float format
            HttpStatusCode.BadGateway,
            "Retry after 1 bad gateway error"
        }
    };

    public static IEnumerable<object[]> RequestValidationTestCases => new List<object[]>
    {
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test" },
                Model = "text-embedding-3-small",
                ApiType = EmbeddingApiType.Default
            },
            "POST",
            "/v1/embeddings",
            new Dictionary<string, object>
            {
                ["input"] = new[] { "test" },
                ["model"] = "text-embedding-3-small"
            },
            "Basic POST request validation"
        }
    };

    public static IEnumerable<object[]> ApiTypeFormattingTestCases => new List<object[]>
    {
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test" },
                Model = "text-embedding-3-small",
                ApiType = EmbeddingApiType.Default,
                EncodingFormat = "float",
                User = "test-user"
            },
            new Dictionary<string, object>
            {
                ["input"] = new[] { "test" },
                ["model"] = "text-embedding-3-small",
                ["encoding_format"] = "float",
                ["user"] = "test-user"
            },
            new Dictionary<string, object>
            {
                ["normalized"] = true,
                ["embedding_type"] = "float"
            },
            "OpenAI API formatting with user and encoding_format"
        },
        new object[]
        {
            new EmbeddingRequest
            {
                Inputs = new[] { "test" },
                Model = "jina-embeddings-v3",
                ApiType = EmbeddingApiType.Jina,
                EncodingFormat = "float",
                Normalized = true
            },
            new Dictionary<string, object>
            {
                ["input"] = new[] { "test" },
                ["model"] = "jina-embeddings-v3",
                ["embedding_type"] = "float",
                ["normalized"] = true
            },
            new Dictionary<string, object>
            {
                ["encoding_format"] = "float",
                ["user"] = "test-user"
            },
            "Jina API formatting with normalized and embedding_type"
        }
    };

    /// <summary>
    /// Test logger implementation for capturing log output
    /// </summary>
    private class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Debug.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }
} 