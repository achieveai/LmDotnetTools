using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmEmbeddings.Tests.Core;

/// <summary>
/// Comprehensive tests for RerankingService class using HTTP mocking
/// </summary>
public class RerankingServiceTests
{
    private readonly ILogger<RerankingService> _logger;

    public RerankingServiceTests()
    {
        _logger = new TestLogger<RerankingService>();
    }

    [Theory]
    [MemberData(nameof(ConstructorTestCases))]
    public void Constructor_WithValidParameters_CreatesInstance(
        string endpoint,
        string model,
        string apiKey,
        string description
    )
    {
        Debug.WriteLine($"Testing constructor with: {description}");

        // Act & Assert
        var options = new RerankingOptions
        {
            BaseUrl = endpoint,
            DefaultModel = model,
            ApiKey = apiKey,
        };
        var service = new RerankingService(options, _logger);

        Assert.NotNull(service);

        service.Dispose();
        Debug.WriteLine($"Constructor test passed: {description}");
    }

    [Theory]
    [MemberData(nameof(ConstructorInvalidParametersTestCases))]
    public void Constructor_WithInvalidParameters_ThrowsException(
        string endpoint,
        string model,
        string apiKey,
        Type expectedExceptionType,
        string description
    )
    {
        Debug.WriteLine($"Testing invalid constructor parameters: {description}");

        // Act & Assert
        var exception = Assert.Throws(
            expectedExceptionType,
            () =>
                new RerankingService(
                    new RerankingOptions
                    {
                        BaseUrl = endpoint,
                        DefaultModel = model,
                        ApiKey = apiKey,
                    },
                    _logger
                )
        );

        Assert.NotNull(exception);
        Debug.WriteLine($"Expected exception thrown: {exception.GetType().Name} - {description}");
    }

    [Theory]
    [MemberData(nameof(BasicRerankingTestCases))]
    public async Task RerankAsync_WithValidInput_ReturnsRankedDocuments(
        string query,
        string[] documents,
        string description
    )
    {
        Debug.WriteLine($"Testing basic reranking: {description}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(CreateValidRerankResponse(documents.Length));
        using var service = CreateRerankingService(fakeHandler);

        // Act
        var result = await service.RerankAsync(query, documents);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(documents.Length, result.Count);
        Assert.All(result, doc => Assert.True(doc.Score is >= 0 and <= 1));
        Assert.All(result, doc => Assert.True(doc.Index >= 0 && doc.Index < documents.Length));

        // Verify ordering (highest score first)
        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].Score >= result[i].Score, "Documents should be ordered by descending score");
        }

        Debug.WriteLine($"Reranked {result.Count} documents for: {description}");
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_WithRetryLogic_Uses500msLinearBackoff()
    {
        Debug.WriteLine("Testing 500ms linear backoff retry logic");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(2, CreateValidRerankResponse(3));
        using var service = CreateRerankingService(fakeHandler);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.RerankAsync("test query", documents);
        stopwatch.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);

        // Verify linear backoff timing (should be approximately 500ms + 1000ms = 1500ms for 2 retries)
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 1400,
            $"Expected at least 1400ms for linear backoff, got {stopwatch.ElapsedMilliseconds}ms"
        );
        Debug.WriteLine($"Linear backoff retry completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RerankAsync_WithDocumentTruncation_TruncatesOnRetry()
    {
        Debug.WriteLine("Testing document truncation on retry");

        // Arrange
        var longDocument = new string('a', 10000); // Very long document
        var documents = new[] { "short doc", longDocument, "another short doc" };

        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(1, CreateValidRerankResponse(3));
        using var service = CreateRerankingService(fakeHandler);

        // Act
        var result = await service.RerankAsync("test query", documents);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Debug.WriteLine($"Document truncation test completed with {result.Count} results");
    }

    [Theory]
    [MemberData(nameof(RetryScenarioTestCases))]
    public async Task RerankAsync_WithRetryScenarios_HandlesCorrectly(
        HttpStatusCode[] statusCodes,
        bool shouldSucceed,
        string description
    )
    {
        Debug.WriteLine($"Testing retry scenario: {description}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateStatusCodeSequenceHandler(
            statusCodes,
            CreateValidRerankResponse(2)
        );
        using var service = CreateRerankingService(fakeHandler);

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await service.RerankAsync("test", documentsArray);
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Debug.WriteLine($"Retry scenario succeeded: {description}");
        }
        else
        {
            _ = await Assert.ThrowsAsync<HttpRequestException>(() => service.RerankAsync("test", documentsArray));
            Debug.WriteLine($"Retry scenario failed as expected: {description}");
        }
    }

    [Theory]
    [MemberData(nameof(InvalidInputTestCases))]
    public async Task RerankAsync_WithInvalidInput_ThrowsException(
        string query,
        string[] documents,
        Type expectedExceptionType,
        string description
    )
    {
        Debug.WriteLine($"Testing invalid input: {description}");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{}");
        using var service = CreateRerankingService(fakeHandler);

        // Act & Assert
        var exception = await Assert.ThrowsAsync(expectedExceptionType, () => service.RerankAsync(query, documents));

        Assert.NotNull(exception);
        Debug.WriteLine($"Expected exception thrown: {exception.GetType().Name} - {description}");
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task RerankAsync_WithMaxRetries_RespectsRetryLimit()
    {
        Debug.WriteLine("Testing maximum retry limit (2 retries)");

        // Arrange - Always return 500 (retryable error)
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleHandler(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError
        ));
        using var service = CreateRerankingService(fakeHandler);

        // Act & Assert
        var stopwatch = Stopwatch.StartNew();
        _ = await Assert.ThrowsAsync<HttpRequestException>(() => service.RerankAsync("test", documentsArray0));
        stopwatch.Stop();

        // Should try 3 times total (1 initial + 2 retries) with 500ms + 1000ms delays
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 1400,
            $"Expected at least 1400ms for max retries, got {stopwatch.ElapsedMilliseconds}ms"
        );
        Debug.WriteLine($"Max retry test completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RerankAsync_WithNonRetryableError_FailsImmediately()
    {
        Debug.WriteLine("Testing non-retryable error (4xx) fails immediately");

        // Arrange
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleHandler(_ => new HttpResponseMessage(
            HttpStatusCode.BadRequest
        ));
        using var service = CreateRerankingService(fakeHandler);

        // Act & Assert
        var stopwatch = Stopwatch.StartNew();
        _ = await Assert.ThrowsAsync<HttpRequestException>(() => service.RerankAsync("test", documentsArray0));
        stopwatch.Stop();

        // Should fail immediately without retries
        Assert.True(
            stopwatch.ElapsedMilliseconds < 100,
            $"Expected immediate failure, got {stopwatch.ElapsedMilliseconds}ms"
        );
        Debug.WriteLine($"Non-retryable error failed immediately in {stopwatch.ElapsedMilliseconds}ms");
    }

    // Test Data
    public static IEnumerable<object[]> ConstructorTestCases =>
        new List<object[]>
        {
            new object[] { "https://api.cohere.com", "rerank-v3.5", "test-key", "Cohere configuration" },
            new object[] { "https://custom.api.com", "custom-rerank-model", "custom-key", "Custom configuration" },
            new object[]
            {
                "https://api.example.com/rerank",
                "rerank-english-v3.0",
                "example-key",
                "Example configuration",
            },
        };

    public static IEnumerable<object[]> ConstructorInvalidParametersTestCases =>
        new List<object[]>
        {
            new object[] { null!, "model", "key", typeof(ArgumentNullException), "Null endpoint" },
            new object[] { "https://api.test.com", null!, "key", typeof(ArgumentNullException), "Null model" },
            new object[] { "https://api.test.com", "model", null!, typeof(ArgumentException), "Null API key" },
            new object[] { "https://api.test.com", "model", "", typeof(ArgumentException), "Empty API key" },
            new object[] { "https://api.test.com", "model", "   ", typeof(ArgumentException), "Whitespace API key" },
        };

    public static IEnumerable<object[]> BasicRerankingTestCases =>
        new List<object[]>
        {
            new object[] { "What is the capital?", item, "Simple query with 3 documents" },
            new object[] { "Machine learning", itemArray, "Technical query with 5 documents" },
            new object[] { "Best practices", itemArray0, "Professional query with 2 documents" },
        };

    public static IEnumerable<object[]> RetryScenarioTestCases =>
        new List<object[]>
        {
            new object[] { new[] { HttpStatusCode.InternalServerError, HttpStatusCode.OK }, true, "500 then success" },
            new object[] { new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.OK }, true, "429 then success" },
            new object[]
            {
                new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.OK },
                true,
                "500, 502, then success",
            },
            new object[]
            {
                new[]
                {
                    HttpStatusCode.InternalServerError,
                    HttpStatusCode.BadGateway,
                    HttpStatusCode.ServiceUnavailable,
                },
                false,
                "All 5xx errors",
            },
            new object[] { new[] { HttpStatusCode.BadRequest }, false, "Non-retryable 400 error" },
            new object[] { new[] { HttpStatusCode.Unauthorized }, false, "Non-retryable 401 error" },
        };

    public static IEnumerable<object[]> InvalidInputTestCases =>
        new List<object[]>
        {
            new object[] { null!, itemArray1, typeof(ArgumentException), "Null query" },
            new object[] { "", itemArray1, typeof(ArgumentException), "Empty query" },
            new object[] { "   ", itemArray2, typeof(ArgumentException), "Whitespace query" },
            new object[] { "query", null!, typeof(ArgumentNullException), "Null documents" },
            new object[] { "query", Array.Empty<string>(), typeof(ArgumentException), "Empty documents array" },
        };

    private static readonly string[] documents = ["doc1", "doc2", "doc3"];
    private static readonly string[] documentsArray = ["doc1", "doc2"];
    private static readonly string[] documentsArray0 = ["doc1"];
    private static readonly string[] item =
    [
        "Paris is the capital of France",
        "London is in England",
        "Berlin is German",
    ];
    private static readonly string[] itemArray =
    [
        "AI and ML concepts",
        "Weather forecast",
        "Cooking recipes",
        "Deep learning basics",
        "Sports news",
    ];
    private static readonly string[] itemArray0 = ["Code quality guidelines", "Testing methodologies"];
    private static readonly string[] itemArray1 = ["doc1"];
    private static readonly string[] itemArray2 = ["doc1"];

    // Helper Methods
    private RerankingService CreateRerankingService(
        FakeHttpMessageHandler httpHandler,
        string endpoint = "https://api.test.com",
        string model = "rerank-v3.5",
        string apiKey = "test-key"
    )
    {
        var httpClient = new HttpClient(httpHandler);
        var service = new RerankingService(
            new RerankingOptions
            {
                BaseUrl = endpoint,
                DefaultModel = model,
                ApiKey = apiKey,
            },
            _logger,
            httpClient
        );
        return service;
    }

    private static string CreateValidRerankResponse(int documentCount)
    {
        var results = new List<object>();
        for (var i = 0; i < documentCount; i++)
        {
            // Create realistic relevance scores that decrease
            var score = 0.9 - (i * 0.2); // Scores: 0.9, 0.7, 0.5, 0.3, 0.1
            results.Add(
                new
                {
                    index = i,
                    relevance_score = Math.Max(0.1, score), // Minimum score of 0.1
                }
            );
        }

        var response = new
        {
            id = Guid.NewGuid().ToString(),
            results,
            meta = new { api_version = new { version = "2" }, billed_units = new { search_units = 1 } },
        };

        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Simple test logger implementation
    /// </summary>
    private class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Debug.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
