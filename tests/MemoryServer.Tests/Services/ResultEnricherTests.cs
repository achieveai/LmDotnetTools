using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace MemoryServer.Tests.Services;

public class ResultEnricherTests
{
    private readonly ResultEnricher _resultEnricher;
    private readonly Mock<IGraphRepository> _mockGraphRepository;
    private readonly EnrichmentOptions _options;
    private readonly SessionContext _sessionContext;

    public ResultEnricherTests()
    {
        _options = new EnrichmentOptions
        {
            EnableEnrichment = true,
            MaxRelatedItems = 2,
            IncludeConfidenceScores = true,
            EnableGracefulFallback = true,
            EnrichmentTimeout = TimeSpan.FromSeconds(1),
            GenerateRelevanceExplanations = true,
            MinRelevanceScore = 0.6f,
        };

        var memoryServerOptions = new MemoryServerOptions { Enrichment = _options };

        var optionsWrapper = Options.Create(memoryServerOptions);
        _mockGraphRepository = new Mock<IGraphRepository>();
        var logger = new LoggerFactory().CreateLogger<ResultEnricher>();

        _resultEnricher = new ResultEnricher(optionsWrapper, _mockGraphRepository.Object, logger);
        _sessionContext = new SessionContext { UserId = "test-user" };
    }

    [Fact]
    public async Task EnrichResultsAsync_WithNullResults_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _resultEnricher.EnrichResultsAsync(null!, _sessionContext)
        );
    }

    [Fact]
    public async Task EnrichResultsAsync_WithEmptyResults_ReturnsEmptyResults()
    {
        // Arrange
        var results = new List<UnifiedSearchResult>();

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(enrichmentResults);
        Assert.Empty(enrichmentResults.Results);
        Assert.False(enrichmentResults.WasEnrichmentPerformed);
        Assert.Equal("Enrichment disabled or no results", enrichmentResults.FallbackReason);
    }

    [Fact]
    public async Task EnrichResultsAsync_WithMemoryResult_GeneratesRelevanceExplanation()
    {
        // Arrange
        var results = new List<UnifiedSearchResult> { CreateMemoryResult(1, "Test memory content", 0.9f) };

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(enrichmentResults);
        Assert.Single(enrichmentResults.Results);
        Assert.True(enrichmentResults.WasEnrichmentPerformed);

        var enrichedResult = enrichmentResults.Results[0];
        Assert.NotNull(enrichedResult.RelevanceExplanation);
        Assert.Contains("Memory relevant because: High relevance match", enrichedResult.RelevanceExplanation);
    }

    [Fact]
    public async Task EnrichResultsAsync_WithEntityResult_AddsRelatedRelationships()
    {
        // Arrange
        var entityResult = CreateEntityResult(1, "John", 0.9f);
        var results = new List<UnifiedSearchResult> { entityResult };

        var relationships = new List<Relationship>
        {
            new()
            {
                Id = 1,
                Source = "John",
                RelationshipType = "works_at",
                Target = "Microsoft",
                Confidence = 0.8f,
            },
            new()
            {
                Id = 2,
                Source = "John",
                RelationshipType = "lives_in",
                Target = "Seattle",
                Confidence = 0.7f,
            },
        };

        _mockGraphRepository
            .Setup(r => r.GetRelationshipsForEntityAsync("John", _sessionContext, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(enrichmentResults);
        Assert.Single(enrichmentResults.Results);
        Assert.True(enrichmentResults.WasEnrichmentPerformed);

        var enrichedResult = enrichmentResults.Results[0];
        Assert.Equal(2, enrichedResult.RelatedRelationships.Count);
        Assert.Equal(2, enrichmentResults.Metrics.RelatedItemsAdded);

        // Verify relationships are ordered by confidence
        Assert.Equal("John works_at Microsoft", enrichedResult.RelatedRelationships[0].Content);
        Assert.Equal(0.8f, enrichedResult.RelatedRelationships[0].RelevanceScore);
        Assert.Equal("John lives_in Seattle", enrichedResult.RelatedRelationships[1].Content);
        Assert.Equal(0.7f, enrichedResult.RelatedRelationships[1].RelevanceScore);
    }

    [Fact]
    public async Task EnrichResultsAsync_WithRelationshipResult_AddsRelatedEntities()
    {
        // Arrange
        var relationshipResult = CreateRelationshipResult(1, "John", "works_at", "Microsoft", 0.9f);
        var results = new List<UnifiedSearchResult> { relationshipResult };

        var johnEntity = new Entity
        {
            Id = 1,
            Name = "John",
            Confidence = 0.9f,
        };
        var microsoftEntity = new Entity
        {
            Id = 2,
            Name = "Microsoft",
            Confidence = 0.8f,
        };

        _mockGraphRepository
            .Setup(r => r.GetEntityByNameAsync("John", _sessionContext, It.IsAny<CancellationToken>()))
            .ReturnsAsync(johnEntity);

        _mockGraphRepository
            .Setup(r => r.GetEntityByNameAsync("Microsoft", _sessionContext, It.IsAny<CancellationToken>()))
            .ReturnsAsync(microsoftEntity);

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(enrichmentResults);
        Assert.Single(enrichmentResults.Results);
        Assert.True(enrichmentResults.WasEnrichmentPerformed);

        var enrichedResult = enrichmentResults.Results[0];
        Assert.Equal(2, enrichedResult.RelatedEntities.Count);
        Assert.Equal(2, enrichmentResults.Metrics.RelatedItemsAdded);

        // Verify entities are ordered by confidence
        Assert.Equal("John", enrichedResult.RelatedEntities[0].Content);
        Assert.Equal(0.9f, enrichedResult.RelatedEntities[0].RelevanceScore);
        Assert.Equal("Microsoft", enrichedResult.RelatedEntities[1].Content);
        Assert.Equal(0.8f, enrichedResult.RelatedEntities[1].RelevanceScore);
    }

    [Fact]
    public async Task EnrichResultsAsync_WithMinRelevanceScore_FiltersLowConfidenceItems()
    {
        // Arrange
        var entityResult = CreateEntityResult(1, "John", 0.9f);
        var results = new List<UnifiedSearchResult> { entityResult };

        var relationships = new List<Relationship>
        {
            new()
            {
                Id = 1,
                Source = "John",
                RelationshipType = "works_at",
                Target = "Microsoft",
                Confidence = 0.8f,
            }, // Above threshold
            new()
            {
                Id = 2,
                Source = "John",
                RelationshipType = "likes",
                Target = "Coffee",
                Confidence = 0.5f,
            }, // Below threshold
        };

        _mockGraphRepository
            .Setup(r => r.GetRelationshipsForEntityAsync("John", _sessionContext, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(enrichmentResults);
        var enrichedResult = enrichmentResults.Results[0];
        Assert.Single(enrichedResult.RelatedRelationships); // Only high-confidence relationship included
        Assert.Equal("John works_at Microsoft", enrichedResult.RelatedRelationships[0].Content);
    }

    [Fact]
    public async Task EnrichResultsAsync_WithMaxRelatedItems_LimitsResults()
    {
        // Arrange
        var limitedOptions = new EnrichmentOptions
        {
            EnableEnrichment = true,
            MaxRelatedItems = 1, // Limit to 1 item
            MinRelevanceScore = 0.0f,
        };

        var entityResult = CreateEntityResult(1, "John", 0.9f);
        var results = new List<UnifiedSearchResult> { entityResult };

        var relationships = new List<Relationship>
        {
            new()
            {
                Id = 1,
                Source = "John",
                RelationshipType = "works_at",
                Target = "Microsoft",
                Confidence = 0.9f,
            },
            new()
            {
                Id = 2,
                Source = "John",
                RelationshipType = "lives_in",
                Target = "Seattle",
                Confidence = 0.8f,
            },
            new()
            {
                Id = 3,
                Source = "John",
                RelationshipType = "likes",
                Target = "Coffee",
                Confidence = 0.7f,
            },
        };

        _mockGraphRepository
            .Setup(r => r.GetRelationshipsForEntityAsync("John", _sessionContext, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(relationships);

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext, limitedOptions);

        // Assert
        Assert.NotNull(enrichmentResults);
        var enrichedResult = enrichmentResults.Results[0];
        Assert.Single(enrichedResult.RelatedRelationships); // Limited to 1 item
        Assert.Equal("John works_at Microsoft", enrichedResult.RelatedRelationships[0].Content); // Highest confidence
    }

    [Fact]
    public async Task EnrichResultsAsync_WithDisabledEnrichment_ReturnsOriginalResults()
    {
        // Arrange
        var disabledOptions = new EnrichmentOptions { EnableEnrichment = false };
        var results = new List<UnifiedSearchResult> { CreateMemoryResult(1, "Test content", 0.9f) };

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext, disabledOptions);

        // Assert
        Assert.NotNull(enrichmentResults);
        Assert.Single(enrichmentResults.Results);
        Assert.False(enrichmentResults.WasEnrichmentPerformed);
        Assert.Equal("Enrichment disabled or no results", enrichmentResults.FallbackReason);

        // Verify it's converted to EnrichedSearchResult but without enrichment
        var enrichedResult = enrichmentResults.Results[0];
        Assert.Empty(enrichedResult.RelatedEntities);
        Assert.Empty(enrichedResult.RelatedRelationships);
        Assert.Null(enrichedResult.RelevanceExplanation);
    }

    [Fact]
    public async Task EnrichResultsAsync_WithRepositoryFailure_HandlesGracefully()
    {
        // Arrange
        var entityResult = CreateEntityResult(1, "John", 0.9f);
        var results = new List<UnifiedSearchResult> { entityResult };

        _mockGraphRepository
            .Setup(r => r.GetRelationshipsForEntityAsync("John", _sessionContext, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Repository error"));

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(enrichmentResults);
        Assert.Single(enrichmentResults.Results);
        Assert.True(enrichmentResults.WasEnrichmentPerformed); // Still performed for other parts

        var enrichedResult = enrichmentResults.Results[0];
        Assert.Empty(enrichedResult.RelatedRelationships); // No relationships due to error
        Assert.NotNull(enrichedResult.RelevanceExplanation); // But explanation still generated

        // Verify error is logged in metrics
        Assert.Single(enrichmentResults.Metrics.Errors);
        Assert.Contains("Entity enrichment failed for ID 1", enrichmentResults.Metrics.Errors[0]);
    }

    [Fact]
    public void IsEnrichmentAvailable_WithEnabledOptions_ReturnsTrue()
    {
        // Act
        var isAvailable = _resultEnricher.IsEnrichmentAvailable();

        // Assert
        Assert.True(isAvailable);
    }

    [Fact]
    public void IsEnrichmentAvailable_WithDisabledOptions_ReturnsFalse()
    {
        // Arrange
        var disabledOptions = new EnrichmentOptions { EnableEnrichment = false };
        var memoryServerOptions = new MemoryServerOptions { Enrichment = disabledOptions };
        var optionsWrapper = Options.Create(memoryServerOptions);
        var logger = new LoggerFactory().CreateLogger<ResultEnricher>();
        var enricher = new ResultEnricher(optionsWrapper, _mockGraphRepository.Object, logger);

        // Act
        var isAvailable = enricher.IsEnrichmentAvailable();

        // Assert
        Assert.False(isAvailable);
    }

    [Fact]
    public async Task EnrichResultsAsync_PopulatesMetricsCorrectly()
    {
        // Arrange
        var results = new List<UnifiedSearchResult>
        {
            CreateMemoryResult(1, "Test memory", 0.9f),
            CreateEntityResult(2, "John", 0.8f),
        };

        _mockGraphRepository
            .Setup(r => r.GetRelationshipsForEntityAsync("John", _sessionContext, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Relationship>());

        // Act
        var enrichmentResults = await _resultEnricher.EnrichResultsAsync(results, _sessionContext);

        // Assert
        Assert.NotNull(enrichmentResults.Metrics);
        Assert.True(enrichmentResults.Metrics.TotalDuration > TimeSpan.Zero);
        Assert.True(enrichmentResults.Metrics.RelationshipDiscoveryDuration >= TimeSpan.Zero);
        Assert.True(enrichmentResults.Metrics.ContextAnalysisDuration >= TimeSpan.Zero);
        Assert.Equal(2, enrichmentResults.Metrics.ResultsEnriched); // Both results enriched (with explanations)
        Assert.Equal(0, enrichmentResults.Metrics.RelatedItemsAdded); // No related items found
        Assert.False(enrichmentResults.Metrics.HasFailures);
        Assert.Empty(enrichmentResults.Metrics.Errors);
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
                Confidence = score,
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
                Confidence = score,
            },
        };
    }
}
