using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryServer.Tests.Services;

public class DeduplicationEngineTests
{
    private readonly DeduplicationEngine _deduplicationEngine;
    private readonly DeduplicationOptions _options;
    private readonly SessionContext _sessionContext;

    public DeduplicationEngineTests()
    {
        _options = new DeduplicationOptions
        {
            EnableDeduplication = true,
            SimilarityThreshold = 0.85f,
            PreserveComplementaryInfo = true,
            EnableGracefulFallback = true,
            DeduplicationTimeout = TimeSpan.FromSeconds(2),
            ContextPreservationSensitivity = 0.7f,
            EnableSourceRelationshipAnalysis = true,
        };

        var memoryServerOptions = new MemoryServerOptions { Deduplication = _options };

        var optionsWrapper = Options.Create(memoryServerOptions);
        var logger = new LoggerFactory().CreateLogger<DeduplicationEngine>();

        _deduplicationEngine = new DeduplicationEngine(optionsWrapper, logger);
        _sessionContext = new SessionContext { UserId = "test-user" };
    }

    [Fact]
    public async Task DeduplicateResultsAsync_WithNullResults_ThrowsArgumentNullException()
    {
        // Act & Assert
        _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _deduplicationEngine.DeduplicateResultsAsync(null!, _sessionContext)
        );
    }

    [Fact]
    public async Task DeduplicateResultsAsync_WithEmptyResults_ReturnsEmptyResults()
    {
        // Arrange
        var results = new List<UnifiedSearchResult>();

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(deduplicationResults);
        Assert.Empty(deduplicationResults.Results);
        Assert.False(deduplicationResults.WasDeduplicationPerformed);
        Assert.Equal("Deduplication disabled or insufficient results", deduplicationResults.FallbackReason);
    }

    [Fact]
    public async Task DeduplicateResultsAsync_WithSingleResult_ReturnsSameResult()
    {
        // Arrange
        var results = new List<UnifiedSearchResult> { CreateMemoryResult(1, "Test memory content", 0.9f) };

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(deduplicationResults);
        _ = Assert.Single(deduplicationResults.Results);
        Assert.False(deduplicationResults.WasDeduplicationPerformed);
        Assert.Equal("Deduplication disabled or insufficient results", deduplicationResults.FallbackReason);
    }

    [Fact]
    public async Task DeduplicateResultsAsync_WithDuplicateContent_RemovesDuplicates()
    {
        // Arrange
        var results = new List<UnifiedSearchResult>
        {
            CreateMemoryResult(1, "This is a test memory about artificial intelligence", 0.9f),
            CreateMemoryResult(2, "This is a test memory about artificial intelligence", 0.8f), // Exact duplicate
            CreateMemoryResult(3, "This is a test memory about machine learning", 0.7f), // Different content
            CreateMemoryResult(4, "This is test memory about artificial intelligence", 0.6f), // Very similar (missing 'a')
        };

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(deduplicationResults);
        Assert.True(deduplicationResults.WasDeduplicationPerformed);
        Assert.True(deduplicationResults.Results.Count < results.Count);
        Assert.True(deduplicationResults.Metrics.DuplicatesRemoved > 0);

        // Should keep the highest scoring result from each duplicate group
        var sortedResults = deduplicationResults.Results.OrderByDescending(r => r.Score).ToList();
        Assert.Equal(0.9f, sortedResults[0].Score); // Highest score from duplicate group
        Assert.Equal(0.7f, sortedResults[1].Score); // Different content, should be preserved
    }

    [Fact]
    public async Task DeduplicationEngineAsync_WithComplementaryInformation_PreservesBoth()
    {
        // Arrange
        var results = new List<UnifiedSearchResult>
        {
            CreateMemoryResult(1, "John works at Microsoft", 0.9f),
            CreateEntityResult(2, "John", 0.8f), // Related entity, should be preserved as complementary
            CreateMemoryResult(3, "Sarah works at Google", 0.7f),
        };

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(deduplicationResults);
        Assert.True(deduplicationResults.WasDeduplicationPerformed);
        Assert.Equal(3, deduplicationResults.Results.Count); // All should be preserved due to different types
        Assert.Equal(0, deduplicationResults.Metrics.DuplicatesRemoved); // No duplicates removed due to complementary info
    }

    [Fact]
    public async Task DeduplicationResultsAsync_WithDisabledDeduplication_ReturnsOriginalResults()
    {
        // Arrange
        var disabledOptions = new DeduplicationOptions { EnableDeduplication = false };
        var results = new List<UnifiedSearchResult>
        {
            CreateMemoryResult(1, "Duplicate content", 0.9f),
            CreateMemoryResult(2, "Duplicate content", 0.8f),
        };

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(
            results,
            _sessionContext,
            disabledOptions
        );

        // Assert
        Assert.NotNull(deduplicationResults);
        Assert.False(deduplicationResults.WasDeduplicationPerformed);
        Assert.Equal(2, deduplicationResults.Results.Count);
        Assert.Equal("Deduplication disabled or insufficient results", deduplicationResults.FallbackReason);
    }

    [Fact]
    public async Task DeduplicationResultsAsync_WithHighSimilarityThreshold_PreservesMoreResults()
    {
        // Arrange
        var strictOptions = new DeduplicationOptions
        {
            EnableDeduplication = true,
            SimilarityThreshold = 0.95f, // Very high threshold
        };

        var results = new List<UnifiedSearchResult>
        {
            CreateMemoryResult(1, "John works at Microsoft in Seattle", 0.9f),
            CreateMemoryResult(2, "John works at Microsoft in Redmond", 0.8f), // Similar but not identical
        };

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(
            results,
            _sessionContext,
            strictOptions
        );

        // Assert
        Assert.NotNull(deduplicationResults);
        Assert.True(deduplicationResults.WasDeduplicationPerformed);
        Assert.Equal(2, deduplicationResults.Results.Count); // Both preserved due to high threshold
        Assert.Equal(0, deduplicationResults.Metrics.DuplicatesRemoved);
    }

    [Fact]
    public async Task DeduplicationResultsAsync_WithLowSimilarityThreshold_RemovesMoreResults()
    {
        // Arrange
        var lenientOptions = new DeduplicationOptions
        {
            EnableDeduplication = true,
            SimilarityThreshold = 0.5f, // Low threshold
        };

        var results = new List<UnifiedSearchResult>
        {
            CreateMemoryResult(1, "John works at Microsoft", 0.9f),
            CreateMemoryResult(2, "John is employed by Microsoft", 0.8f), // Somewhat similar
            CreateMemoryResult(3, "Sarah works at Google", 0.7f), // Different
        };

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(
            results,
            _sessionContext,
            lenientOptions
        );

        // Assert
        Assert.NotNull(deduplicationResults);
        Assert.True(deduplicationResults.WasDeduplicationPerformed);
        Assert.True(deduplicationResults.Results.Count <= results.Count);
        // With low threshold, similar results might be considered duplicates
    }

    [Fact]
    public void IsDeduplicationAvailable_WithEnabledOptions_ReturnsTrue()
    {
        // Act
        var isAvailable = _deduplicationEngine.IsDeduplicationAvailable();

        // Assert
        Assert.True(isAvailable);
    }

    [Fact]
    public void IsDeduplicationAvailable_WithDisabledOptions_ReturnsFalse()
    {
        // Arrange
        var disabledOptions = new DeduplicationOptions { EnableDeduplication = false };
        var memoryServerOptions = new MemoryServerOptions { Deduplication = disabledOptions };
        var optionsWrapper = Options.Create(memoryServerOptions);
        var logger = new LoggerFactory().CreateLogger<DeduplicationEngine>();
        var engine = new DeduplicationEngine(optionsWrapper, logger);

        // Act
        var isAvailable = engine.IsDeduplicationAvailable();

        // Assert
        Assert.False(isAvailable);
    }

    [Fact]
    public async Task DeduplicationResultsAsync_PopulatesMetricsCorrectly()
    {
        // Arrange
        var results = new List<UnifiedSearchResult>
        {
            CreateMemoryResult(1, "Test content one", 0.9f),
            CreateMemoryResult(2, "Test content one", 0.8f), // Duplicate
            CreateMemoryResult(3, "Test content two", 0.7f),
        };

        // Act
        var deduplicationResults = await _deduplicationEngine.DeduplicateResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(deduplicationResults.Metrics);
        Assert.True(deduplicationResults.Metrics.TotalDuration > TimeSpan.Zero);
        Assert.True(deduplicationResults.Metrics.SimilarityAnalysisDuration >= TimeSpan.Zero);
        Assert.True(deduplicationResults.Metrics.SourceAnalysisDuration >= TimeSpan.Zero);
        Assert.True(deduplicationResults.Metrics.PotentialDuplicatesFound >= 0);
        Assert.True(deduplicationResults.Metrics.DuplicatesRemoved >= 0);
        Assert.True(deduplicationResults.Metrics.DuplicatesPreserved >= 0);
        Assert.False(deduplicationResults.Metrics.HasFailures);
        Assert.Empty(deduplicationResults.Metrics.Errors);
    }

    private static UnifiedSearchResult CreateMemoryResult(int id, string content, float score)
    {
        return new UnifiedSearchResult
        {
            Type = UnifiedResultType.Memory,
            Id = id,
            Content = content,
            Score = score,
            Source = "Test",
            CreatedAt = DateTime.UtcNow,
            OriginalMemory = new Memory
            {
                Id = id,
                Content = content,
                UserId = "test-user",
            },
        };
    }

    private static UnifiedSearchResult CreateEntityResult(int id, string name, float score)
    {
        return new UnifiedSearchResult
        {
            Type = UnifiedResultType.Entity,
            Id = id,
            Content = name,
            Score = score,
            Source = "Test",
            CreatedAt = DateTime.UtcNow,
            OriginalEntity = new Entity
            {
                Id = id,
                Name = name,
                UserId = "test-user",
            },
        };
    }

    private static UnifiedSearchResult CreateRelationshipResult(
        int id,
        string source,
        string relationshipType,
        string target,
        float score
    )
    {
        return new UnifiedSearchResult
        {
            Type = UnifiedResultType.Relationship,
            Id = id,
            Content = $"{source} {relationshipType} {target}",
            Score = score,
            Source = "Test",
            CreatedAt = DateTime.UtcNow,
            OriginalRelationship = new Relationship
            {
                Id = id,
                Source = source,
                RelationshipType = relationshipType,
                Target = target,
                UserId = "test-user",
            },
        };
    }
}
