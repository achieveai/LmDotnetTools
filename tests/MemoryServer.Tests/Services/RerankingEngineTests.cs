using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace MemoryServer.Tests.Services;

/// <summary>
///     Unit tests for RerankingEngine to verify Phase 7 implementation.
///     Tests the intelligent reranking functionality with mocked dependencies.
/// </summary>
public class RerankingEngineTests
{
    private readonly Mock<ILogger<RerankingEngine>> _mockLogger;
    private readonly ITestOutputHelper _output;

    public RerankingEngineTests(ITestOutputHelper output)
    {
        _output = output;
        _mockLogger = new Mock<ILogger<RerankingEngine>>();
    }

    [Fact]
    public void RerankingEngine_Constructor_InitializesCorrectly()
    {
        // Arrange
        var options = CreateTestOptions();

        // Act
        var engine = new RerankingEngine(options, _mockLogger.Object);

        // Assert
        Assert.NotNull(engine);
        Assert.False(engine.IsRerankingAvailable()); // No API key configured in test
    }

    [Fact]
    public void RerankingEngine_Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() => new RerankingEngine(null!, _mockLogger.Object));
    }

    [Fact]
    public void RerankingEngine_Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        var options = CreateTestOptions();

        // Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() => new RerankingEngine(options, null!));
    }

    [Fact]
    public async Task RerankResultsAsync_WithEmptyQuery_ThrowsArgumentException()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();
        var results = CreateTestResults();

        // Act & Assert
        _ = await Assert.ThrowsAsync<ArgumentException>(() => engine.RerankResultsAsync("", results, sessionContext));

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.RerankResultsAsync("   ", results, sessionContext)
        );

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.RerankResultsAsync(null!, results, sessionContext)
        );
    }

    [Fact]
    public async Task RerankResultsAsync_WithNullResults_ThrowsArgumentNullException()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();

        // Act & Assert
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            engine.RerankResultsAsync("test query", null!, sessionContext)
        );
    }

    [Fact]
    public async Task RerankResultsAsync_WithEmptyResults_ReturnsEmptyResults()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();
        var emptyResults = new List<UnifiedSearchResult>();

        // Act
        var result = await engine.RerankResultsAsync("test query", emptyResults, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Results);
        Assert.False(result.WasReranked);
        Assert.Equal("Reranking disabled or no results", result.FallbackReason);
        Assert.True(result.Metrics.TotalDuration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task RerankResultsAsync_WithDisabledReranking_ReturnsOriginalResults()
    {
        // Arrange
        var options = CreateTestOptions(false);
        var engine = new RerankingEngine(options, _mockLogger.Object);
        var sessionContext = CreateTestSessionContext();
        var results = CreateTestResults();

        // Act
        var result = await engine.RerankResultsAsync("test query", results, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(results.Count, result.Results.Count);
        Assert.False(result.WasReranked);
        Assert.Equal("Reranking disabled or no results", result.FallbackReason);
        Assert.True(result.Metrics.TotalDuration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task RerankResultsAsync_WithLocalScoring_AppliesMultiDimensionalScoring()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();
        var results = CreateTestResults();
        var originalScores = results.Select(r => r.Score).ToList();

        // Act
        var result = await engine.RerankResultsAsync("test query", results, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(results.Count, result.Results.Count);
        Assert.False(result.WasReranked); // No API key configured, so local scoring
        Assert.Equal("Semantic reranking service not available", result.FallbackReason);

        // Verify that scores were modified by multi-dimensional scoring
        var newScores = result.Results.Select(r => r.Score).ToList();
        Assert.NotEqual(originalScores, newScores);

        // Results should be sorted by score (descending)
        for (var i = 0; i < result.Results.Count - 1; i++)
        {
            Assert.True(result.Results[i].Score >= result.Results[i + 1].Score);
        }

        _output.WriteLine(
            $"Local scoring completed: {result.Results.Count} results in {result.Metrics.TotalDuration.TotalMilliseconds}ms"
        );
    }

    [Fact]
    public async Task RerankResultsAsync_WithRecencyBoost_BoostsRecentContent()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();

        // Create results with different creation dates
        var results = new List<UnifiedSearchResult>
        {
            new()
            {
                Id = 1,
                Type = UnifiedResultType.Memory,
                Content = "Old content",
                Score = 0.5f,
                CreatedAt = DateTime.UtcNow.AddDays(-60), // Old content
            },
            new()
            {
                Id = 2,
                Type = UnifiedResultType.Memory,
                Content = "Recent content",
                Score = 0.5f,
                CreatedAt = DateTime.UtcNow.AddDays(-5), // Recent content
            },
        };

        // Act
        var result = await engine.RerankResultsAsync("test query", results, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Results.Count);

        // Recent content should have higher score due to recency boost
        var recentResult = result.Results.First(r => r.Id == 2);
        var oldResult = result.Results.First(r => r.Id == 1);

        Assert.True(
            recentResult.Score > oldResult.Score,
            $"Recent content score ({recentResult.Score}) should be higher than old content score ({oldResult.Score})"
        );

        _output.WriteLine($"Recency boost test: Recent={recentResult.Score:F3}, Old={oldResult.Score:F3}");
    }

    [Fact]
    public async Task RerankResultsAsync_WithSourceWeights_AppliesHierarchicalWeighting()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();

        // Create results with different types but same initial score
        var results = new List<UnifiedSearchResult>
        {
            new()
            {
                Id = 1,
                Type = UnifiedResultType.Memory,
                Content = "Memory content",
                Score = 1.0f,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = 2,
                Type = UnifiedResultType.Entity,
                Content = "Entity content",
                Score = 1.0f,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = 3,
                Type = UnifiedResultType.Relationship,
                Content = "Relationship content",
                Score = 1.0f,
                CreatedAt = DateTime.UtcNow,
            },
        };

        // Act
        var result = await engine.RerankResultsAsync("test query", results, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Results.Count);

        var memoryResult = result.Results.First(r => r.Type == UnifiedResultType.Memory);
        var entityResult = result.Results.First(r => r.Type == UnifiedResultType.Entity);
        var relationshipResult = result.Results.First(r => r.Type == UnifiedResultType.Relationship);

        // Memory should have highest score, then Entity, then Relationship
        Assert.True(
            memoryResult.Score >= entityResult.Score,
            $"Memory score ({memoryResult.Score}) should be >= Entity score ({entityResult.Score})"
        );
        Assert.True(
            entityResult.Score >= relationshipResult.Score,
            $"Entity score ({entityResult.Score}) should be >= Relationship score ({relationshipResult.Score})"
        );

        _output.WriteLine(
            $"Source weights: Memory={memoryResult.Score:F3}, Entity={entityResult.Score:F3}, Relationship={relationshipResult.Score:F3}"
        );
    }

    [Fact]
    public async Task RerankResultsAsync_WithMaxCandidates_LimitsCandidates()
    {
        // Arrange
        var options = CreateTestOptions(maxCandidates: 2);
        var engine = new RerankingEngine(options, _mockLogger.Object);
        var sessionContext = CreateTestSessionContext();
        var results = CreateTestResults(5); // More than max candidates

        // Act
        var result = await engine.RerankResultsAsync("test query", results, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Metrics.CandidateCount); // Should be limited to max candidates
        Assert.Equal(2, result.Results.Count);

        _output.WriteLine($"Max candidates test: {result.Metrics.CandidateCount} candidates processed");
    }

    [Fact]
    public async Task RerankResultsAsync_WithConfidenceValues_AppliesConfidenceBoost()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();

        var results = new List<UnifiedSearchResult>
        {
            new()
            {
                Id = 1,
                Type = UnifiedResultType.Entity,
                Content = "Low confidence entity",
                Score = 0.5f,
                Confidence = 0.3f,
                CreatedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = 2,
                Type = UnifiedResultType.Entity,
                Content = "High confidence entity",
                Score = 0.5f,
                Confidence = 0.9f,
                CreatedAt = DateTime.UtcNow,
            },
        };

        // Act
        var result = await engine.RerankResultsAsync("test query", results, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Results.Count);

        var lowConfidenceResult = result.Results.First(r => r.Id == 1);
        var highConfidenceResult = result.Results.First(r => r.Id == 2);

        // High confidence should have higher score
        Assert.True(
            highConfidenceResult.Score > lowConfidenceResult.Score,
            $"High confidence score ({highConfidenceResult.Score}) should be higher than low confidence score ({lowConfidenceResult.Score})"
        );

        _output.WriteLine(
            $"Confidence boost test: High={highConfidenceResult.Score:F3}, Low={lowConfidenceResult.Score:F3}"
        );
    }

    [Fact]
    public async Task RerankResultsAsync_TracksPositionChanges()
    {
        // Arrange
        var engine = CreateRerankingEngine();
        var sessionContext = CreateTestSessionContext();

        // Create results that will likely change positions after scoring
        var results = new List<UnifiedSearchResult>
        {
            new()
            {
                Id = 1,
                Type = UnifiedResultType.Relationship, // Lower weight
                Content = "Short",
                Score = 1.0f,
                CreatedAt = DateTime.UtcNow.AddDays(-60),
            },
            new()
            {
                Id = 2,
                Type = UnifiedResultType.Memory, // Higher weight
                Content = "This is a longer content that should score better for content quality",
                Score = 0.8f,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
            },
        };

        // Act
        var result = await engine.RerankResultsAsync("test query", results, sessionContext);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Metrics.PositionChanges >= 0);

        _output.WriteLine($"Position changes: {result.Metrics.PositionChanges}");
    }

    private RerankingEngine CreateRerankingEngine(bool enableReranking = true, int maxCandidates = 100)
    {
        var options = CreateTestOptions(enableReranking, maxCandidates);
        return new RerankingEngine(options, _mockLogger.Object);
    }

    private static IOptions<MemoryServerOptions> CreateTestOptions(bool enableReranking = true, int maxCandidates = 100)
    {
        var memoryServerOptions = new MemoryServerOptions
        {
            Reranking = new RerankingOptions
            {
                EnableReranking = enableReranking,
                MaxCandidates = maxCandidates,
                EnableGracefulFallback = true,
                RerankingTimeout = TimeSpan.FromSeconds(3),
                RerankingEndpoint = "https://api.cohere.ai",
                RerankingModel = "rerank-v3.5",
                ApiKey = "", // No API key for tests - will use local scoring
                SemanticRelevanceWeight = 0.7f,
                ContentQualityWeight = 0.1f,
                RecencyWeight = 0.1f,
                ConfidenceWeight = 0.1f,
                SourceWeights = new Dictionary<UnifiedResultType, float>
                {
                    { UnifiedResultType.Memory, 1.0f },
                    { UnifiedResultType.Entity, 0.8f },
                    { UnifiedResultType.Relationship, 0.7f },
                },
                EnableRecencyBoost = true,
                RecencyBoostDays = 30,
            },
        };

        return Options.Create(memoryServerOptions);
    }

    private static SessionContext CreateTestSessionContext()
    {
        return new SessionContext
        {
            UserId = "test_user",
            AgentId = "test_agent",
            RunId = "test_run",
        };
    }

    private static List<UnifiedSearchResult> CreateTestResults(int count = 3)
    {
        var results = new List<UnifiedSearchResult>();

        for (var i = 0; i < count; i++)
        {
            results.Add(
                new UnifiedSearchResult
                {
                    Id = i + 1,
                    Type = (UnifiedResultType)(i % 3), // Cycle through types
                    Content = $"Test content {i + 1}",
                    SecondaryContent = $"Secondary content {i + 1}",
                    Score = 0.5f + (i * 0.1f),
                    Source = "Test",
                    CreatedAt = DateTime.UtcNow.AddDays(-i),
                    Confidence = i % 2 == 0 ? 0.8f : null,
                }
            );
        }

        return results;
    }
}
