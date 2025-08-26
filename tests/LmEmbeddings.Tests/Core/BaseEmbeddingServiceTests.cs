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
/// Core functionality tests for BaseEmbeddingService
/// Tests business logic, validation, and non-HTTP specific functionality
/// </summary>
public class BaseEmbeddingServiceTests
{
    private readonly ILogger<TestEmbeddingService> _logger;

    public BaseEmbeddingServiceTests()
    {
        _logger = TestLoggerFactory.CreateLogger<TestEmbeddingService>();
    }

    [Theory]
    [MemberData(nameof(GetEmbeddingAsyncTestCases))]
    public async Task GetEmbeddingAsync_WithVariousInputs_ReturnsExpectedResults(
        string input,
        bool shouldSucceed,
        string expectedErrorMessage,
        string description
    )
    {
        Debug.WriteLine($"Testing GetEmbeddingAsync: {description}");
        Debug.WriteLine($"Input: '{input}', Expected to succeed: {shouldSucceed}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(1)
        );
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };
        var service = new TestEmbeddingService(_logger, httpClient);

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await service.GetEmbeddingAsync(input);
            Assert.NotNull(result);
            Assert.Equal(1536, result.Length); // Expected embedding size
            Debug.WriteLine($"✓ Successfully generated embedding with {result.Length} dimensions");
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GetEmbeddingAsync(input)
            );
            Assert.Contains(expectedErrorMessage, exception.Message);
            Debug.WriteLine($"✓ Validation failed as expected: {exception.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(GenerateEmbeddingAsyncTestCases))]
    public async Task GenerateEmbeddingAsync_WithVariousInputs_ReturnsExpectedResults(
        string text,
        string model,
        bool shouldSucceed,
        string expectedErrorMessage,
        string description
    )
    {
        Debug.WriteLine($"Testing GenerateEmbeddingAsync: {description}");
        Debug.WriteLine($"Text: '{text}', Model: '{model}', Expected to succeed: {shouldSucceed}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(1)
        );
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };
        var service = new TestEmbeddingService(_logger, httpClient);

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await service.GenerateEmbeddingAsync(text, model);
            Assert.NotNull(result);
            Assert.NotNull(result.Embeddings);
            Assert.Single(result.Embeddings);
            Debug.WriteLine($"✓ Successfully generated embedding for model '{model}'");
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GenerateEmbeddingAsync(text, model)
            );
            Assert.Contains(expectedErrorMessage, exception.Message);
            Debug.WriteLine($"✓ Validation failed as expected: {exception.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(RequestValidationTestCases))]
    public async Task GenerateEmbeddingsAsync_WithValidation_HandlesValidationCorrectly(
        EmbeddingRequest request,
        bool shouldSucceed,
        string expectedErrorMessage,
        string description
    )
    {
        Debug.WriteLine($"Testing request validation: {description}");
        Debug.WriteLine(
            $"Request: Model='{request?.Model}', Inputs={request?.Inputs?.Count() ?? 0}, Expected to succeed: {shouldSucceed}"
        );

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(request?.Inputs?.Count() ?? 1)
        );
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };
        var service = new TestEmbeddingService(_logger, httpClient);

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await service.GenerateEmbeddingsAsync(request!);
            Assert.NotNull(result);
            Assert.NotNull(result.Embeddings);
            Debug.WriteLine(
                $"✓ Request validation passed, generated {result.Embeddings.Count} embeddings"
            );
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GenerateEmbeddingsAsync(request!)
            );
            Assert.Contains(expectedErrorMessage, exception.Message);
            Debug.WriteLine($"✓ Request validation failed as expected: {exception.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(PayloadFormattingTestCases))]
    public void FormatRequestPayload_WithDifferentApiTypes_FormatsCorrectly(
        EmbeddingRequest request,
        string[] expectedKeys,
        string[] unexpectedKeys,
        string description
    )
    {
        Debug.WriteLine($"Testing payload formatting: {description}");
        Debug.WriteLine($"API Type: {request.ApiType}, Model: {request.Model}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{}");
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };
        var service = new TestEmbeddingService(_logger, httpClient);

        // Act
        var payload = service.TestFormatRequestPayload(request);

        // Assert
        Assert.NotNull(payload);

        foreach (var expectedKey in expectedKeys)
        {
            Assert.True(payload.ContainsKey(expectedKey), $"Missing expected key: {expectedKey}");
            Debug.WriteLine($"✓ Found expected key: {expectedKey}");
        }

        foreach (var unexpectedKey in unexpectedKeys)
        {
            Assert.False(
                payload.ContainsKey(unexpectedKey),
                $"Found unexpected key: {unexpectedKey}"
            );
            Debug.WriteLine($"✓ Correctly excluded key: {unexpectedKey}");
        }

        Debug.WriteLine($"✓ Payload formatted correctly with {payload.Count} fields");
    }

    [Theory]
    [MemberData(nameof(DisposalTestCases))]
    public async Task DisposedService_ThrowsObjectDisposedException(
        string operationName,
        Func<TestEmbeddingService, Task> operation,
        string description
    )
    {
        Debug.WriteLine($"Testing disposal behavior: {description}");
        Debug.WriteLine($"Operation: {operationName}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(1)
        );
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };
        var service = new TestEmbeddingService(_logger, httpClient);

        // Act - Dispose the service
        service.Dispose();
        Debug.WriteLine("✓ Service disposed");

        // Assert - Operations should throw ObjectDisposedException
        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(() => operation(service));
        Assert.Contains("TestEmbeddingService", exception.ObjectName);
        Debug.WriteLine($"✓ ObjectDisposedException thrown as expected: {exception.Message}");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_ReturnsExpectedModels()
    {
        Debug.WriteLine("Testing GetAvailableModelsAsync");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{}");
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };
        var service = new TestEmbeddingService(_logger, httpClient);

        // Act
        var models = await service.GetAvailableModelsAsync();

        // Assert
        Assert.NotNull(models);
        Assert.Contains("test-model-1", models);
        Assert.Contains("test-model-2", models);
        Debug.WriteLine($"✓ Retrieved {models.Count} available models");
    }

    [Fact]
    public void EmbeddingSize_ReturnsExpectedValue()
    {
        Debug.WriteLine("Testing EmbeddingSize property");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{}");
        var httpClient = new HttpClient(fakeHandler)
        {
            BaseAddress = new Uri("https://api.test.com"),
        };
        var service = new TestEmbeddingService(_logger, httpClient);

        // Act
        var embeddingSize = service.EmbeddingSize;

        // Assert
        Assert.Equal(1536, embeddingSize);
        Debug.WriteLine($"✓ EmbeddingSize is {embeddingSize} as expected");
    }

    #region Test Data

    public static IEnumerable<object[]> GetEmbeddingAsyncTestCases =>
        new List<object[]>
        {
            new object[] { "Valid text input", true, "", "Valid input should succeed" },
            new object[]
            {
                "",
                false,
                "Value cannot be null, empty, or whitespace",
                "Empty string should fail",
            },
            new object[]
            {
                "   ",
                false,
                "Value cannot be null, empty, or whitespace",
                "Whitespace-only string should fail",
            },
            new object[]
            {
                "A very long text that should still work fine for embedding generation",
                true,
                "",
                "Long text should succeed",
            },
        };

    public static IEnumerable<object[]> GenerateEmbeddingAsyncTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "Valid text",
                "test-model",
                true,
                "",
                "Valid text and model should succeed",
            },
            new object[]
            {
                "",
                "test-model",
                false,
                "Value cannot be null, empty, or whitespace",
                "Empty text should fail",
            },
            new object[]
            {
                "Valid text",
                "",
                false,
                "Value cannot be null, empty, or whitespace",
                "Empty model should fail",
            },
            new object[]
            {
                "   ",
                "test-model",
                false,
                "Value cannot be null, empty, or whitespace",
                "Whitespace text should fail",
            },
            new object[]
            {
                "Valid text",
                "   ",
                false,
                "Value cannot be null, empty, or whitespace",
                "Whitespace model should fail",
            },
        };

    public static IEnumerable<object[]> RequestValidationTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest { Model = "test-model", Inputs = new[] { "text1", "text2" } },
                true,
                "",
                "Valid request should succeed",
            },
            new object[]
            {
                new EmbeddingRequest { Model = "", Inputs = new[] { "text1" } },
                false,
                "Value cannot be null, empty, or whitespace",
                "Empty model should fail",
            },
            new object[]
            {
                new EmbeddingRequest { Model = "test-model", Inputs = new string[0] },
                false,
                "Collection cannot be empty",
                "Empty inputs should fail",
            },
            new object[]
            {
                new EmbeddingRequest { Model = "test-model", Inputs = new[] { "text1", "" } },
                false,
                "Collection cannot contain null, empty, or whitespace elements",
                "Empty input text should fail",
            },
            new object[]
            {
                new EmbeddingRequest { Model = "test-model", Inputs = new[] { "text1", "   " } },
                false,
                "Collection cannot contain null, empty, or whitespace elements",
                "Whitespace input text should fail",
            },
        };

    public static IEnumerable<object[]> PayloadFormattingTestCases =>
        new List<object[]>
        {
            new object[]
            {
                new EmbeddingRequest
                {
                    Model = "test-model",
                    Inputs = new[] { "text1" },
                    ApiType = EmbeddingApiType.Default,
                    EncodingFormat = "float",
                    Dimensions = 512,
                    User = "test-user",
                },
                new[] { "input", "model", "encoding_format", "dimensions", "user" },
                new[] { "normalized", "embedding_type" },
                "OpenAI format with all optional parameters",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Model = "jina-model",
                    Inputs = new[] { "text1" },
                    ApiType = EmbeddingApiType.Jina,
                    Normalized = true,
                    EncodingFormat = "float",
                    Dimensions = 768,
                },
                new[] { "input", "model", "normalized", "embedding_type", "dimensions" },
                new[] { "encoding_format", "user" },
                "Jina format with all optional parameters",
            },
            new object[]
            {
                new EmbeddingRequest
                {
                    Model = "basic-model",
                    Inputs = new[] { "text1" },
                    ApiType = EmbeddingApiType.Default,
                },
                new[] { "input", "model" },
                new[] { "normalized", "embedding_type", "encoding_format", "dimensions", "user" },
                "Basic OpenAI format with minimal parameters",
            },
        };

    public static IEnumerable<object[]> DisposalTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "GetEmbeddingAsync",
                new Func<TestEmbeddingService, Task>(service => service.GetEmbeddingAsync("test")),
                "GetEmbeddingAsync should throw after disposal",
            },
            new object[]
            {
                "GenerateEmbeddingAsync",
                new Func<TestEmbeddingService, Task>(service =>
                    service.GenerateEmbeddingAsync("test", "model")
                ),
                "GenerateEmbeddingAsync should throw after disposal",
            },
            new object[]
            {
                "GenerateEmbeddingsAsync",
                new Func<TestEmbeddingService, Task>(service =>
                    service.GenerateEmbeddingsAsync(
                        new EmbeddingRequest { Model = "test", Inputs = new[] { "text" } }
                    )
                ),
                "GenerateEmbeddingsAsync should throw after disposal",
            },
        };

    #endregion

    #region Test Implementation

    public class TestEmbeddingService : BaseEmbeddingService
    {
        public TestEmbeddingService(ILogger<TestEmbeddingService> logger, HttpClient httpClient)
            : base(logger, httpClient) { }

        public override int EmbeddingSize => 1536;

        public override async Task<EmbeddingResponse> GenerateEmbeddingsAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken = default
        )
        {
            // Validate the request (this will call the base class validation)
            ValidateRequest(request);

            // Format the payload (this tests the base class formatting logic)
            var payload = FormatRequestPayload(request);

            // Simulate HTTP call
            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(
                jsonContent,
                System.Text.Encoding.UTF8,
                "application/json"
            );
            var response = await HttpClient.PostAsync(
                "/embeddings",
                httpContent,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync();
            var embeddingResponse = JsonSerializer.Deserialize<TestEmbeddingResponse>(
                responseContent
            );

            return new EmbeddingResponse
            {
                Embeddings = embeddingResponse!
                    .Embeddings.Select(e => new EmbeddingItem
                    {
                        Vector = e.Vector,
                        Index = e.Index,
                        Text = e.Text,
                    })
                    .ToList(),
                Model = embeddingResponse.Model,
                Usage = new EmbeddingUsage
                {
                    PromptTokens = embeddingResponse.Usage.PromptTokens,
                    TotalTokens = embeddingResponse.Usage.TotalTokens,
                },
            };
        }

        public override Task<IReadOnlyList<string>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default
        )
        {
            var models = new List<string> { "test-model-1", "test-model-2" };
            return Task.FromResult<IReadOnlyList<string>>(models);
        }

        // Expose protected method for testing
        public Dictionary<string, object> TestFormatRequestPayload(EmbeddingRequest request)
        {
            return FormatRequestPayload(request);
        }

        private class TestEmbeddingResponse
        {
            [JsonPropertyName("Embeddings")]
            public List<TestEmbeddingItem> Embeddings { get; set; } = new();

            [JsonPropertyName("Model")]
            public string Model { get; set; } = "";

            [JsonPropertyName("Usage")]
            public TestUsage Usage { get; set; } = new();
        }

        private class TestEmbeddingItem
        {
            [JsonPropertyName("Vector")]
            public float[] Vector { get; set; } = Array.Empty<float>();

            [JsonPropertyName("Index")]
            public int Index { get; set; }

            [JsonPropertyName("Text")]
            public string Text { get; set; } = "";
        }

        private class TestUsage
        {
            [JsonPropertyName("PromptTokens")]
            public int PromptTokens { get; set; }

            [JsonPropertyName("TotalTokens")]
            public int TotalTokens { get; set; }
        }
    }

    #endregion
}
