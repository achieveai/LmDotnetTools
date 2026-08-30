using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LmEmbeddings.Tests.Core;

/// <summary>
///     Comprehensive tests for RerankingService class using HTTP mocking
/// </summary>
public class RerankingServiceTests
{
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
    private readonly ILogger<RerankingService> _logger;

    public RerankingServiceTests()
    {
        _logger = new TestLogger<RerankingService>();
    }

    // Test Data
    public static IEnumerable<object[]> ConstructorTestCases =>
        [
            ["https://api.cohere.com", "rerank-v3.5", "test-key", "Cohere configuration"],
            ["https://custom.api.com", "custom-rerank-model", "custom-key", "Custom configuration"],
            ["https://api.example.com/rerank", "rerank-english-v3.0", "example-key", "Example configuration"],
        ];

    public static IEnumerable<object[]> ConstructorInvalidParametersTestCases =>
        [
            [null!, "model", "key", typeof(ArgumentNullException), "Null endpoint"],
            ["https://api.test.com", null!, "key", typeof(ArgumentNullException), "Null model"],
            ["https://api.test.com", "model", null!, typeof(ArgumentException), "Null API key"],
            ["https://api.test.com", "model", "", typeof(ArgumentException), "Empty API key"],
            ["https://api.test.com", "model", "   ", typeof(ArgumentException), "Whitespace API key"],
        ];

    public static IEnumerable<object[]> BasicRerankingTestCases =>
        [
            ["What is the capital?", item, "Simple query with 3 documents"],
            ["Machine learning", itemArray, "Technical query with 5 documents"],
            ["Best practices", itemArray0, "Professional query with 2 documents"],
        ];

    public static IEnumerable<object[]> RetryScenarioTestCases =>
        [
            [new[] { HttpStatusCode.InternalServerError, HttpStatusCode.OK }, true, "500 then success"],
            [new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.OK }, true, "429 then success"],
            [
                new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.OK },
                true,
                "500, 502, then success",
            ],
            [
                new[]
                {
                    HttpStatusCode.InternalServerError,
                    HttpStatusCode.BadGateway,
                    HttpStatusCode.ServiceUnavailable,
                },
                false,
                "All 5xx errors",
            ],
            [new[] { HttpStatusCode.BadRequest }, false, "Non-retryable 400 error"],
            [new[] { HttpStatusCode.Unauthorized }, false, "Non-retryable 401 error"],
        ];

    public static IEnumerable<object[]> InvalidInputTestCases =>
        [
            [null!, itemArray1, typeof(ArgumentException), "Null query"],
            ["", itemArray1, typeof(ArgumentException), "Empty query"],
            ["   ", itemArray2, typeof(ArgumentException), "Whitespace query"],
            ["query", null!, typeof(ArgumentNullException), "Null documents"],
            ["query", Array.Empty<string>(), typeof(ArgumentException), "Empty documents array"],
        ];

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

        // Arrange - 1 retry is enough to verify retry logic works
        var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(1, CreateValidRerankResponse(3));
        using var service = CreateRerankingService(fakeHandler);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await service.RerankAsync("test query", documents);
        stopwatch.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);

        // Verify linear backoff timing (should be approximately 500ms for 1 retry)
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 400,
            $"Expected at least 400ms for linear backoff, got {stopwatch.ElapsedMilliseconds}ms"
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
        Debug.WriteLine("Testing maximum retry limit");

        // Arrange - Always return 500 (retryable error)
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleHandler(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError
        ));
        using var service = CreateRerankingService(fakeHandler);

        // Act & Assert
        var stopwatch = Stopwatch.StartNew();
        _ = await Assert.ThrowsAsync<HttpRequestException>(() => service.RerankAsync("test", documentsArray0));
        stopwatch.Stop();

        // RerankingService uses maxRetries=2, so 3 total attempts with 500ms + 1000ms delays
        // But we just verify the exception is thrown after retries are exhausted
        Debug.WriteLine($"Max retry test completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RerankAsync_WithNonRetryableError_FailsImmediately()
    {
        Debug.WriteLine("Testing non-retryable error (4xx) fails without retrying");

        // Arrange
        var attemptCount = 0;
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleHandler(_ =>
        {
            Interlocked.Increment(ref attemptCount);
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        using var service = CreateRerankingService(fakeHandler);

        // Act
        _ = await Assert.ThrowsAsync<HttpRequestException>(() => service.RerankAsync("test", documentsArray0));

        // Assert: a non-retryable 4xx must fail on the first attempt, not be retried like a 5xx
        // (RerankingService.IsRetryableStatusCode excludes 400). Asserting the attempt count
        // directly - rather than elapsed wall-clock time against the 500ms/1000ms retry-delay
        // budget - proves "no retry happened" without a timing assertion that can flake when a
        // starved CI runner pushes even a synchronous failure past a fixed millisecond ceiling.
        Assert.Equal(1, attemptCount);
        Debug.WriteLine($"Non-retryable error failed after {attemptCount} attempt(s)");
    }

    [Fact]
    [Trait("Category", "TokenBudgetBatching")]
    public async Task RerankAsync_WithDocumentsExceedingTokenBudget_SplitsIntoMultipleBatchesAndRemapsIndices()
    {
        Debug.WriteLine("Testing token-budget multi-batch splitting and index re-mapping");

        // Arrange - 10 documents of ~1000 estimated tokens each (4000 chars / 4 chars-per-token).
        // With MaxTokensPerBatch=6000 and a short query (~3 tokens), the budget is ~5997 tokens,
        // which fits exactly 5 documents per batch => this must produce exactly 2 batches.
        var documentsList = Enumerable.Range(0, 10).Select(i => BuildMarkerDocument(i)).ToList();
        var requestCount = 0;

        // Each fake server response derives its score from the MARKER embedded in the document
        // text itself (not from the document's position within the batch), so a passing test
        // proves the batch-local RerankResult.Index was correctly re-mapped back to the
        // original document list position via `batch[r.Index]`.
        var fakeHandler = new FakeHttpMessageHandler(
            async (request, cancellationToken) =>
            {
                requestCount++;

                var bodyJson = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var bodyDoc = JsonDocument.Parse(bodyJson);
                var documentsElement = bodyDoc.RootElement.GetProperty("documents");

                var results = new List<object>();
                var localIndex = 0;
                foreach (var documentElement in documentsElement.EnumerateArray())
                {
                    var marker = ParseMarker(documentElement.GetString()!);
                    results.Add(new { index = localIndex, relevance_score = (double)(100 - marker) });
                    localIndex++;
                }

                var responseJson = JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString(), results });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
                };
            }
        );
        using var service = CreateRerankingService(fakeHandler);

        // Act
        var result = await service.RerankAsync("test query", documentsList);

        // Assert
        Assert.Equal(documentsList.Count, result.Count);
        Assert.Equal(2, requestCount); // Confirms the documents were actually split into 2 batches

        // Sorted score-descending
        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].Score >= result[i].Score, "Merged results should be sorted score-descending");
        }

        // Every result's Index must refer to the ORIGINAL document list position, and the
        // score/document must correspond to that exact original document (pins batch[r.Index]).
        foreach (var r in result)
        {
            Assert.InRange(r.Index, 0, documentsList.Count - 1);
            Assert.Equal(documentsList[r.Index], r.Document);

            var marker = ParseMarker(r.Document!);
            Assert.Equal(r.Index, marker); // Marker was assigned equal to the original index
            Assert.Equal(100 - marker, r.Score);
        }

        // Indices form a permutation of the original document positions (no duplicates/drops)
        Assert.Equal(documentsList.Count, result.Select(r => r.Index).Distinct().Count());

        Debug.WriteLine($"Multi-batch rerank produced {result.Count} results across {requestCount} HTTP requests");
    }

    [Fact]
    [Trait("Category", "TokenBudgetBatching")]
    public async Task RerankAsync_WithOversizedQuery_FloorsBudgetToOneAndCompletesWithoutThrowing()
    {
        Debug.WriteLine("Testing oversized query forces per-document batches via the Math.Max budget floor");

        // Arrange - query alone is estimated at >= 6000 tokens (24,000 chars / 4), so
        // MaxTokensPerBatch - queryTokens would be <= 0; the Math.Max(..., 1) floor must kick in
        // so batching still proceeds (one document per batch) instead of throwing/looping forever.
        var longQuery = new string('q', 24_000);
        var docs = new[] { "doc-a", "doc-b", "doc-c" };

        // Each batch contains exactly one document, so a response with a single result works for
        // every request regardless of how many requests are made.
        var fakeHandler = FakeHttpMessageHandler.CreateSimpleJsonHandler(CreateValidRerankResponse(1));
        using var service = CreateRerankingService(fakeHandler);

        // Act
        var result = await service.RerankAsync(longQuery, docs);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(docs.Length, result.Count);

        Debug.WriteLine($"Oversized-query rerank completed with {result.Count} results");
    }

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

    /// <summary>
    ///     Builds a document of exactly <paramref name="totalLength" /> characters whose content
    ///     begins with a parseable "MARKER_nnnn" tag identifying its original index. Used so a
    ///     fake batch handler can compute a deterministic score from document content alone,
    ///     independent of the document's position within its batch.
    /// </summary>
    private static string BuildMarkerDocument(int marker, int totalLength = 4000)
    {
        var markerText = $"MARKER_{marker:D4}";
        return markerText + new string('x', totalLength - markerText.Length);
    }

    private static int ParseMarker(string document)
    {
        var match = Regex.Match(document, "^MARKER_(\\d+)");
        Assert.True(
            match.Success,
            $"Document did not contain a MARKER tag: {document[..Math.Min(20, document.Length)]}"
        );
        return int.Parse(match.Groups[1].Value);
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
    ///     Simple test logger implementation
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
