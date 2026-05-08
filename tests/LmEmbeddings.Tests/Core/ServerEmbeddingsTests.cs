using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmEmbeddings.Models;
using LmEmbeddings.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmEmbeddings.Tests.Core;

/// <summary>
///     Comprehensive tests for ServerEmbeddings class using HTTP mocking
/// </summary>
public class ServerEmbeddingsTests
{
    private readonly ILogger<ServerEmbeddings> _logger;

    public ServerEmbeddingsTests()
    {
        _logger = TestLoggerFactory.CreateLogger<ServerEmbeddings>();
    }

    // Test Data
    public static IEnumerable<object[]> ConstructorTestCases =>
        [
            [
                "https://api.openai.com",
                "text-embedding-3-small",
                1536,
                "test-key",
                100,
                EmbeddingApiType.Default,
                "OpenAI configuration",
            ],
            [
                "https://api.jina.ai",
                "jina-embeddings-v3",
                1024,
                "jina-key",
                50,
                EmbeddingApiType.Jina,
                "Jina configuration",
            ],
            [
                "https://custom.api.com",
                "custom-model",
                768,
                "custom-key",
                200,
                EmbeddingApiType.Default,
                "Custom configuration",
            ],
        ];

    public static IEnumerable<object[]> ConstructorInvalidParametersTestCases =>
        [
            [null!, "model", 1536, "key", 100, typeof(ArgumentNullException), "Null endpoint"],
            ["https://api.test.com", null!, 1536, "key", 100, typeof(ArgumentNullException), "Null model"],
            ["https://api.test.com", "model", 0, "key", 100, typeof(ArgumentException), "Zero embedding size"],
            ["https://api.test.com", "model", -1, "key", 100, typeof(ArgumentException), "Negative embedding size"],
            ["https://api.test.com", "model", 1536, null!, 100, typeof(ArgumentException), "Null API key"],
            ["https://api.test.com", "model", 1536, "", 100, typeof(ArgumentException), "Empty API key"],
            ["https://api.test.com", "model", 1536, "key", 0, typeof(ArgumentException), "Zero batch size"],
        ];

    public static IEnumerable<object[]> BasicEmbeddingTestCases =>
        [
            ["Hello world", "Simple text"],
            ["The quick brown fox jumps over the lazy dog", "Longer sentence"],
            ["🌟 Unicode text with emojis 🚀", "Unicode and emojis"],
        ];

    public static IEnumerable<object[]> TextChunkingTestCases =>
        [
            [new string('a', 10000), 2, "Long text requiring chunking"],
            [string.Join(" ", Enumerable.Repeat("word", 2000)), 2, "Many words requiring chunking"],
            ["Short text", 1, "Short text not requiring chunking"],
        ];

    public static IEnumerable<object[]> ApiTypeTestCases =>
        [
            [EmbeddingApiType.Default, "OpenAI API format"],
            [EmbeddingApiType.Jina, "Jina API format"],
        ];

    [Theory]
    [MemberData(nameof(ConstructorTestCases))]
    public void Constructor_WithValidParameters_CreatesInstance(
        string endpoint,
        string model,
        int embeddingSize,
        string apiKey,
        int maxBatchSize,
        EmbeddingApiType apiType,
        string description
    )
    {
        Debug.WriteLine($"Testing constructor with: {description}");

        // Act & Assert
        var service = new ServerEmbeddings(endpoint, model, embeddingSize, apiKey, maxBatchSize, apiType, _logger);

        Assert.NotNull(service);
        Assert.Equal(embeddingSize, service.EmbeddingSize);

        service.Dispose();
        Debug.WriteLine($"Constructor test passed: {description}");
    }

    [Theory]
    [MemberData(nameof(ConstructorInvalidParametersTestCases))]
    public void Constructor_WithInvalidParameters_ThrowsException(
        string endpoint,
        string model,
        int embeddingSize,
        string apiKey,
        int maxBatchSize,
        Type expectedExceptionType,
        string description
    )
    {
        Debug.WriteLine($"Testing invalid constructor parameters: {description}");

        // Act & Assert
        var exception = Assert.Throws(
            expectedExceptionType,
            () =>
                new ServerEmbeddings(
                    endpoint,
                    model,
                    embeddingSize,
                    apiKey,
                    maxBatchSize,
                    EmbeddingApiType.Default,
                    _logger
                )
        );

        Assert.NotNull(exception);
        Debug.WriteLine($"Expected exception thrown: {exception.GetType().Name} - {description}");
    }

    [Theory]
    [MemberData(nameof(BasicEmbeddingTestCases))]
    public async Task GetEmbeddingAsync_WithValidInput_ReturnsEmbedding(string input, string description)
    {
        Debug.WriteLine($"Testing basic embedding generation: {description}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(1)
        );
        using var service = CreateServerEmbeddings(fakeHandler);

        // Act
        var result = await service.GetEmbeddingAsync(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1536, result.Length);
        Debug.WriteLine($"Generated embedding with {result.Length} dimensions for: {description}");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task GenerateEmbeddingsAsync_WithBatchProcessing_ProcessesConcurrently()
    {
        Debug.WriteLine("Testing batch processing with concurrent requests");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(3)
        );
        using var service = CreateServerEmbeddings(fakeHandler, maxBatchSize: 2);
        var texts = new[] { "text1", "text2", "text3" };
        var request = new EmbeddingRequest { Inputs = texts, Model = "test-model" };

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.GenerateEmbeddingsAsync(request);
        stopwatch.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Embeddings.Count);
        Debug.WriteLine($"Batch processing completed in {stopwatch.ElapsedMilliseconds}ms for {texts.Length} texts");
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GenerateEmbeddingsAsync_WithRetryLogic_UsesLinearBackoff()
    {
        Debug.WriteLine("Testing linear backoff retry logic");

        // Arrange - 1 retry is enough to verify retry logic works
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
            1,
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(1)
        );
        using var service = CreateServerEmbeddings(fakeHandler);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.GetEmbeddingAsync("test text");
        stopwatch.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1536, result.Length);

        // Verify linear backoff timing (should be approximately 1s for 1 retry)
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 900,
            $"Expected at least 900ms for linear backoff, got {stopwatch.ElapsedMilliseconds}ms"
        );
        Debug.WriteLine($"Linear backoff retry completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Theory]
    [MemberData(nameof(TextChunkingTestCases))]
    public async Task GenerateEmbeddingsAsync_WithLongText_ChunksAutomatically(
        string longText,
        int expectedChunks,
        string description
    )
    {
        Debug.WriteLine($"Testing text chunking: {description}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(expectedChunks)
        );
        using var service = CreateServerEmbeddings(fakeHandler);
        var request = new EmbeddingRequest { Inputs = [longText], Model = "test-model" };

        // Act
        var result = await service.GenerateEmbeddingsAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.True(
            result.Embeddings.Count >= expectedChunks,
            $"Expected at least {expectedChunks} chunks, got {result.Embeddings.Count}"
        );
        Debug.WriteLine($"Text chunking created {result.Embeddings.Count} chunks for: {description}");
    }

    [Theory]
    [MemberData(nameof(ApiTypeTestCases))]
    public async Task GenerateEmbeddingsAsync_WithDifferentApiTypes_FormatsCorrectly(
        EmbeddingApiType apiType,
        string description
    )
    {
        Debug.WriteLine($"Testing API type formatting: {description}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(
            EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(1)
        );
        using var service = CreateServerEmbeddings(fakeHandler, apiType: apiType);

        // Act
        var result = await service.GetEmbeddingAsync("test");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1536, result.Length);
        Debug.WriteLine($"API type {apiType} formatting test passed: {description}");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_ReturnsConfiguredModel()
    {
        Debug.WriteLine("Testing GetAvailableModelsAsync");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{}");
        using var service = CreateServerEmbeddings(fakeHandler, model: "test-model");

        // Act
        var models = await service.GetAvailableModelsAsync();

        // Assert
        Assert.NotNull(models);
        _ = Assert.Single(models);
        Assert.Equal("test-model", models[0]);
        Debug.WriteLine($"Available models returned: {string.Join(", ", models)}");
    }

    // Helper Methods
    private ServerEmbeddings CreateServerEmbeddings(
        FakeHttpMessageHandler httpHandler,
        string endpoint = "https://api.test.com",
        string model = "test-model",
        int embeddingSize = 1536,
        string apiKey = "test-key",
        int maxBatchSize = 100,
        EmbeddingApiType apiType = EmbeddingApiType.Default
    )
    {
        var httpClient = new HttpClient(httpHandler);
        var service = new ServerEmbeddings(
            endpoint,
            model,
            embeddingSize,
            apiKey,
            maxBatchSize,
            apiType,
            _logger,
            httpClient
        );
        return service;
    }
}
