using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmEmbeddings.Models;
using LmEmbeddings.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmEmbeddings.Tests.Core;

/// <summary>
/// HTTP-based tests for API type functionality in BaseEmbeddingService
/// Following patterns from mocking-httpclient.md
/// </summary>
public class BaseEmbeddingServiceApiTypeTests
{
    private readonly ILogger<TestEmbeddingService> _logger;

    public BaseEmbeddingServiceApiTypeTests()
    {
        _logger = TestLoggerFactory.CreateLogger<TestEmbeddingService>();
    }

    [Theory]
    [MemberData(nameof(OpenAIRequestFormattingTestCases))]
    public async Task GenerateEmbeddingsAsync_OpenAIApiType_SendsCorrectHttpRequest(
        EmbeddingRequest request,
        Dictionary<string, object> expectedPayloadFields,
        string description
    )
    {
        Debug.WriteLine($"Testing OpenAI HTTP request formatting: {description}");
        Debug.WriteLine(
            $"Input: Model={request.Model}, ApiType={request.ApiType}, Inputs={string.Join(",", request.Inputs)}"
        );

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(
            (httpRequest, cancellationToken) =>
            {
                capturedRequest = httpRequest;

                // Return a valid response
                var response = EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(
                    request.Inputs.Count
                );
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            response,
                            System.Text.Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
            }
        );

        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };

        var service = new TestEmbeddingService(_logger, httpClient);

        // Act
        var result = await service.GenerateEmbeddingsAsync(request);

        // Assert
        Assert.NotNull(capturedRequest);
        var requestBody = await capturedRequest.Content!.ReadAsStringAsync();
        Debug.WriteLine($"Request body: {requestBody}");

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);
        Assert.NotNull(payload);

        foreach (var expectedField in expectedPayloadFields)
        {
            Assert.True(
                payload.ContainsKey(expectedField.Key),
                $"Missing key: {expectedField.Key}"
            );
            Debug.WriteLine($"✓ {expectedField.Key}: found in payload");
        }

        Debug.WriteLine($"✓ OpenAI request formatted correctly with {payload.Count} fields");
    }

    [Theory]
    [MemberData(nameof(JinaRequestFormattingTestCases))]
    public async Task GenerateEmbeddingsAsync_JinaApiType_SendsCorrectHttpRequest(
        EmbeddingRequest request,
        Dictionary<string, object> expectedPayloadFields,
        string description
    )
    {
        Debug.WriteLine($"Testing Jina HTTP request formatting: {description}");
        Debug.WriteLine(
            $"Input: Model={request.Model}, ApiType={request.ApiType}, Normalized={request.Normalized}"
        );

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(
            (httpRequest, cancellationToken) =>
            {
                capturedRequest = httpRequest;

                var response = EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(
                    request.Inputs.Count
                );
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            response,
                            System.Text.Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
            }
        );

        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.jina.ai"),
        };

        var service = new TestEmbeddingService(_logger, httpClient);

        // Act
        var result = await service.GenerateEmbeddingsAsync(request);

        // Assert
        Assert.NotNull(capturedRequest);
        var requestBody = await capturedRequest.Content!.ReadAsStringAsync();
        Debug.WriteLine($"Request body: {requestBody}");

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);
        Assert.NotNull(payload);

        foreach (var expectedField in expectedPayloadFields)
        {
            Assert.True(
                payload.ContainsKey(expectedField.Key),
                $"Missing key: {expectedField.Key}"
            );
            Debug.WriteLine($"✓ {expectedField.Key}: found in payload");
        }

        Debug.WriteLine($"✓ Jina request formatted correctly with {payload.Count} fields");
    }

    [Theory]
    [MemberData(nameof(ValidationTestCases))]
    public async Task GenerateEmbeddingsAsync_WithValidation_HandlesValidationCorrectly(
        EmbeddingRequest request,
        bool shouldSucceed,
        string expectedErrorMessage,
        string description
    )
    {
        Debug.WriteLine($"Testing request validation via HTTP: {description}");
        Debug.WriteLine(
            $"Input: ApiType={request.ApiType}, EncodingFormat={request.EncodingFormat}"
        );

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(request.Inputs.Count)
        );

        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };

        var service = new TestEmbeddingService(_logger, httpClient);

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await service.GenerateEmbeddingsAsync(request);
            Assert.NotNull(result);
            Debug.WriteLine("✓ Validation passed as expected");
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GenerateEmbeddingsAsync(request)
            );
            Assert.Contains(expectedErrorMessage, exception.Message);
            Debug.WriteLine($"✓ Validation failed as expected: {exception.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(HttpErrorHandlingTestCases))]
    public async Task GenerateEmbeddingsAsync_HttpErrors_HandlesErrorsCorrectly(
        EmbeddingRequest request,
        HttpStatusCode errorStatus,
        string errorResponse,
        Type expectedExceptionType,
        string description
    )
    {
        Debug.WriteLine($"Testing HTTP error handling: {description}");
        Debug.WriteLine($"Error status: {errorStatus}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            errorResponse,
            errorStatus
        );
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };

        var service = new TestEmbeddingService(_logger, httpClient);

        // Act & Assert
        var exception = await Assert.ThrowsAsync(
            expectedExceptionType,
            () => service.GenerateEmbeddingsAsync(request)
        );

        Debug.WriteLine($"Exception caught: {exception.Message}");
        Assert.NotNull(exception);
        Debug.WriteLine("✓ Error handled correctly");
    }

    [Theory]
    [MemberData(nameof(RetryLogicTestCases))]
    [Trait("Category", "Resiliency")]
    public async Task GenerateEmbeddingsAsync_RetryLogic_RetriesOnFailure(
        EmbeddingRequest request,
        int failureCount,
        HttpStatusCode failureStatus,
        string description
    )
    {
        Debug.WriteLine($"Testing retry logic via HTTP: {description}");
        Debug.WriteLine($"Failure count: {failureCount}, Status: {failureStatus}");

        // Arrange
        var successResponse = EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(
            request.Inputs.Count
        );
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            failureCount,
            successResponse,
            failureStatus
        );

        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };

        var service = new TestEmbeddingService(_logger, httpClient);

        // Act
        var stopwatch = Stopwatch.StartNew();
        EmbeddingResponse? result = null;
        Exception? caughtException = null;

        try
        {
            result = await service.GenerateEmbeddingsAsync(request);
        }
        catch (Exception ex)
        {
            caughtException = ex;
            Debug.WriteLine($"Exception caught during test: {ex.GetType().Name}: {ex.Message}");
        }

        stopwatch.Stop();

        // Assert
        Debug.WriteLine($"Execution time: {stopwatch.ElapsedMilliseconds}ms (including retries)");

        if (caughtException != null)
        {
            Debug.WriteLine($"Test failed with exception: {caughtException}");
            throw new Exception(
                $"Expected retry to succeed but got exception: {caughtException.Message}",
                caughtException
            );
        }

        Assert.NotNull(result);
        Debug.WriteLine($"Result is null: {result == null}");
        if (result != null)
        {
            Debug.WriteLine($"Result.Embeddings is null: {result.Embeddings == null}");
            if (result.Embeddings != null)
            {
                Debug.WriteLine($"Result.Embeddings.Count: {result.Embeddings.Count}");
            }
        }
        Assert.True(
            result?.Embeddings?.Count > 0,
            $"Expected embeddings count > 0, but got {result?.Embeddings?.Count ?? 0}"
        );
        Debug.WriteLine("✓ Retry logic worked correctly");
    }

    [Theory]
    [MemberData(nameof(AdditionalOptionsTestCases))]
    public async Task GenerateEmbeddingsAsync_WithAdditionalOptions_IncludesOptionsInRequest(
        EmbeddingRequest request,
        string[] expectedKeys,
        string description
    )
    {
        Debug.WriteLine($"Testing additional options via HTTP: {description}");
        Debug.WriteLine(
            $"Input: ApiType={request.ApiType}, AdditionalOptions={request.AdditionalOptions?.Count ?? 0} items"
        );

        // Arrange
        HttpRequestMessage? capturedRequest = null;
        var fakeHandler = new FakeHttpMessageHandler(
            (httpRequest, cancellationToken) =>
            {
                capturedRequest = httpRequest;

                var response = EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(
                    request.Inputs.Count
                );
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            response,
                            System.Text.Encoding.UTF8,
                            "application/json"
                        ),
                    }
                );
            }
        );

        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };

        var service = new TestEmbeddingService(_logger, httpClient);

        // Act
        var result = await service.GenerateEmbeddingsAsync(request);

        // Assert
        Assert.NotNull(capturedRequest);
        var requestBody = await capturedRequest.Content!.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(requestBody);
        Assert.NotNull(payload);

        foreach (var key in expectedKeys)
        {
            Assert.True(payload.ContainsKey(key), $"Missing expected key: {key}");
            Debug.WriteLine($"✓ Found expected key: {key} = {payload[key]}");
        }

        Debug.WriteLine($"✓ All {expectedKeys.Length} expected keys found in HTTP payload");
    }

    /// <summary>
    /// Test implementation of BaseEmbeddingService for HTTP testing
    /// </summary>
    private class TestEmbeddingService : BaseEmbeddingService
    {
        public TestEmbeddingService(ILogger<TestEmbeddingService> logger, HttpClient httpClient)
            : base(logger, httpClient) { }

        public override int EmbeddingSize => 1536;

        public override async Task<EmbeddingResponse> GenerateEmbeddingsAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default
        )
        {
            ValidateRequest(request);

            return await ExecuteHttpWithRetryAsync(
                async () =>
                {
                    var requestPayload = FormatRequestPayload(request);
                    var json = JsonSerializer.Serialize(requestPayload);
                    var content = new StringContent(
                        json,
                        System.Text.Encoding.UTF8,
                        "application/json"
                    );

                    return await HttpClient.PostAsync("/v1/embeddings", content, cancellationToken);
                },
                async (response) =>
                {
                    var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    var embeddingResponse = JsonSerializer.Deserialize<TestEmbeddingResponse>(
                        responseJson
                    );

                    if (embeddingResponse?.Embeddings == null)
                        throw new InvalidOperationException("Invalid response from API");

                    return new EmbeddingResponse
                    {
                        Embeddings = embeddingResponse
                            .Embeddings.Select(e => new EmbeddingItem
                            {
                                Vector = e.Vector,
                                Index = e.Index,
                                Text = e.Text,
                            })
                            .ToArray(),
                        Model = embeddingResponse.Model,
                        Usage =
                            embeddingResponse.Usage != null
                                ? new EmbeddingUsage
                                {
                                    PromptTokens = embeddingResponse.Usage.PromptTokens,
                                    TotalTokens = embeddingResponse.Usage.TotalTokens,
                                }
                                : null,
                    };
                },
                cancellationToken: cancellationToken
            );
        }

        public override Task<IReadOnlyList<string>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { "test-model" });
        }

        private class TestEmbeddingResponse
        {
            [JsonPropertyName("Embeddings")]
            public TestEmbeddingItem[] Embeddings { get; set; } = Array.Empty<TestEmbeddingItem>();

            [JsonPropertyName("Model")]
            public string Model { get; set; } = "";

            [JsonPropertyName("Usage")]
            public TestUsage? Usage { get; set; }
        }

        private class TestEmbeddingItem
        {
            [JsonPropertyName("Vector")]
            public float[] Vector { get; set; } = Array.Empty<float>();

            [JsonPropertyName("Index")]
            public int Index { get; set; }

            [JsonPropertyName("Text")]
            public string? Text { get; set; }
        }

        private class TestUsage
        {
            [JsonPropertyName("PromptTokens")]
            public int PromptTokens { get; set; }

            [JsonPropertyName("TotalTokens")]
            public int TotalTokens { get; set; }
        }
    }

    public static IEnumerable<object[]> OpenAIRequestFormattingTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "Hello world" },
                    Model = "text-embedding-3-small",
                    ApiType = EmbeddingApiType.Default,
                },
                new Dictionary<string, object>
                {
                    ["input"] = new[] { "Hello world" },
                    ["model"] = "text-embedding-3-small",
                },
                "Basic OpenAI request",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "Hello", "World" },
                    Model = "text-embedding-3-large",
                    ApiType = EmbeddingApiType.Default,
                    EncodingFormat = "float",
                    Dimensions = 1536,
                    User = "test-user",
                },
                new Dictionary<string, object>
                {
                    ["input"] = new[] { "Hello", "World" },
                    ["model"] = "text-embedding-3-large",
                    ["encoding_format"] = "float",
                    ["dimensions"] = 1536,
                    ["user"] = "test-user",
                },
                "OpenAI request with all parameters",
            },
        };

    public static IEnumerable<object[]> JinaRequestFormattingTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "Hello world" },
                    Model = "jina-embeddings-v3",
                    ApiType = EmbeddingApiType.Jina,
                },
                new Dictionary<string, object>
                {
                    ["input"] = new[] { "Hello world" },
                    ["model"] = "jina-embeddings-v3",
                },
                "Basic Jina request",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "Hello", "World" },
                    Model = "jina-embeddings-v3",
                    ApiType = EmbeddingApiType.Jina,
                    EncodingFormat = "float",
                    Normalized = true,
                    Dimensions = 1024,
                },
                new Dictionary<string, object>
                {
                    ["input"] = new[] { "Hello", "World" },
                    ["model"] = "jina-embeddings-v3",
                    ["embedding_type"] = "float",
                    ["normalized"] = true,
                    ["dimensions"] = 1024,
                },
                "Jina request with all parameters",
            },
        };

    public static IEnumerable<object[]> ValidationTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Default,
                    EncodingFormat = "float",
                },
                true,
                "",
                "Valid OpenAI request with float encoding",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Default,
                    EncodingFormat = "binary",
                },
                false,
                "Invalid value 'binary'. Allowed values: float, base64",
                "Invalid OpenAI request with binary encoding",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Jina,
                    EncodingFormat = "binary",
                },
                true,
                "",
                "Valid Jina request with binary encoding",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Jina,
                    EncodingFormat = "invalid",
                },
                false,
                "Invalid value 'invalid'. Allowed values: float, binary, base64",
                "Invalid Jina request with unsupported encoding",
            },
        };

    public static IEnumerable<object[]> HttpErrorHandlingTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Default,
                },
                HttpStatusCode.BadRequest,
                "{\"error\": \"Bad request\"}",
                typeof(HttpRequestException),
                "400 Bad Request error",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Jina,
                },
                HttpStatusCode.Unauthorized,
                "{\"error\": \"Unauthorized\"}",
                typeof(HttpRequestException),
                "401 Unauthorized error",
            },
        };

    public static IEnumerable<object[]> RetryLogicTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Default,
                },
                2, // Fail twice, then succeed
                HttpStatusCode.InternalServerError,
                "Retry after 2 server errors",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test1", "test2" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Jina,
                },
                1, // Fail once, then succeed
                HttpStatusCode.BadGateway,
                "Retry after 1 bad gateway error",
            },
        };

    public static IEnumerable<object[]> AdditionalOptionsTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Default,
                    AdditionalOptions = new Dictionary<string, object>
                    {
                        ["custom_param"] = "custom_value",
                        ["another_param"] = 42,
                    },
                },
                new[] { "input", "model", "custom_param", "another_param" },
                "OpenAI request with additional options",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Inputs = new[] { "test" },
                    Model = "test-model",
                    ApiType = EmbeddingApiType.Jina,
                    AdditionalOptions = new Dictionary<string, object> { ["jina_specific"] = true },
                },
                new[] { "input", "model", "jina_specific" },
                "Jina request with additional options",
            },
        };
}
