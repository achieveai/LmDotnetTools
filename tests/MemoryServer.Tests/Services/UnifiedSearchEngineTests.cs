using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace MemoryServer.Tests.Services;

/// <summary>
/// Unit tests for UnifiedSearchEngine to verify Phase 6 implementation.
/// Tests the unified search functionality with mocked dependencies.
/// </summary>
public class UnifiedSearchEngineTests
{
    private readonly Mock<IMemoryRepository> _mockMemoryRepository;
    private readonly Mock<IGraphRepository> _mockGraphRepository;
    private readonly Mock<IEmbeddingManager> _mockEmbeddingManager;
    private readonly Mock<IRerankingEngine> _mockRerankingEngine;
    private readonly Mock<IDeduplicationEngine> _mockDeduplicationEngine;
    private readonly Mock<IResultEnricher> _mockResultEnricher;
    private readonly Mock<ILogger<UnifiedSearchEngine>> _mockLogger;
    private readonly UnifiedSearchEngine _unifiedSearchEngine;
    private readonly ITestOutputHelper _output;

    public UnifiedSearchEngineTests(ITestOutputHelper output)
    {
        _output = output;
        _mockMemoryRepository = new Mock<IMemoryRepository>();
        _mockGraphRepository = new Mock<IGraphRepository>();
        _mockEmbeddingManager = new Mock<IEmbeddingManager>();
        _mockRerankingEngine = new Mock<IRerankingEngine>();
        _mockDeduplicationEngine = new Mock<IDeduplicationEngine>();
        _mockResultEnricher = new Mock<IResultEnricher>();
        _mockLogger = new Mock<ILogger<UnifiedSearchEngine>>();

        // Setup default reranking behavior to return original results
        _mockRerankingEngine
            .Setup(x =>
                x.RerankResultsAsync(
                    It.IsAny<string>(),
                    It.IsAny<List<UnifiedSearchResult>>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<RerankingOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (
                    string query,
                    List<UnifiedSearchResult> results,
                    SessionContext context,
                    RerankingOptions options,
                    CancellationToken token
                ) =>
                    new RerankingResults
                    {
                        Results = results,
                        WasReranked = false,
                        Metrics = new RerankingMetrics(),
                    }
            );

        // Setup default deduplication behavior to return original results
        _mockDeduplicationEngine
            .Setup(x =>
                x.DeduplicateResultsAsync(
                    It.IsAny<List<UnifiedSearchResult>>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<DeduplicationOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (
                    List<UnifiedSearchResult> results,
                    SessionContext context,
                    DeduplicationOptions options,
                    CancellationToken token
                ) => new DeduplicationResults { Results = results, Metrics = new DeduplicationMetrics() }
            );

        // Setup default enrichment behavior to return original results
        _mockResultEnricher
            .Setup(x =>
                x.EnrichResultsAsync(
                    It.IsAny<List<UnifiedSearchResult>>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<EnrichmentOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (
                    List<UnifiedSearchResult> results,
                    SessionContext context,
                    EnrichmentOptions options,
                    CancellationToken token
                ) =>
                    new EnrichmentResults
                    {
                        Results = results
                            .Select(r => new EnrichedSearchResult
                            {
                                Type = r.Type,
                                Id = r.Id,
                                Content = r.Content,
                                SecondaryContent = r.SecondaryContent,
                                Source = r.Source,
                                Score = r.Score,
                                CreatedAt = r.CreatedAt,
                                Confidence = r.Confidence,
                                Metadata = r.Metadata,
                                OriginalMemory = r.OriginalMemory,
                                OriginalEntity = r.OriginalEntity,
                                OriginalRelationship = r.OriginalRelationship,
                                RelatedEntities = new List<RelatedItem>(),
                                RelatedRelationships = new List<RelatedItem>(),
                            })
                            .ToList(),
                        Metrics = new EnrichmentMetrics(),
                    }
            );

        _unifiedSearchEngine = new UnifiedSearchEngine(
            _mockMemoryRepository.Object,
            _mockGraphRepository.Object,
            _mockEmbeddingManager.Object,
            _mockRerankingEngine.Object,
            _mockDeduplicationEngine.Object,
            _mockResultEnricher.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    public void UnifiedSearchEngine_Constructor_InitializesCorrectly()
    {
        // Arrange & Act
        var engine = new UnifiedSearchEngine(
            _mockMemoryRepository.Object,
            _mockGraphRepository.Object,
            _mockEmbeddingManager.Object,
            _mockRerankingEngine.Object,
            _mockDeduplicationEngine.Object,
            _mockResultEnricher.Object,
            _mockLogger.Object
        );

        // Assert
        Assert.NotNull(engine);
    }

    [Fact]
    public async Task SearchAllSourcesAsync_WithEmptyQuery_ThrowsArgumentException()
    {
        // Arrange
        var sessionContext = CreateTestSessionContext();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _unifiedSearchEngine.SearchAllSourcesAsync("", sessionContext)
        );

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _unifiedSearchEngine.SearchAllSourcesAsync("   ", sessionContext)
        );

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _unifiedSearchEngine.SearchAllSourcesAsync(null!, sessionContext)
        );
    }

    [Fact]
    public async Task SearchAllSourcesAsync_WithValidQuery_ReturnsResults()
    {
        // Arrange
        var sessionContext = CreateTestSessionContext();
        var query = "test query";

        // Setup mock responses
        var mockMemories = new List<Memory>
        {
            new Memory
            {
                Id = 1,
                Content = "Test memory content",
                UserId = sessionContext.UserId,
                CreatedAt = DateTime.UtcNow,
            },
        };

        var mockEntities = new List<Entity>
        {
            new Entity
            {
                Id = 1,
                Name = "Test Entity",
                Type = "Person",
                UserId = sessionContext.UserId,
                CreatedAt = DateTime.UtcNow,
            },
        };

        var mockRelationships = new List<Relationship>
        {
            new Relationship
            {
                Id = 1,
                Source = "Entity1",
                Target = "Entity2",
                RelationshipType = "WORKS_WITH",
                UserId = sessionContext.UserId,
                CreatedAt = DateTime.UtcNow,
            },
        };

        _mockMemoryRepository
            .Setup(x =>
                x.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(mockMemories);

        _mockGraphRepository
            .Setup(x =>
                x.SearchEntitiesAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(mockEntities);

        _mockGraphRepository
            .Setup(x =>
                x.SearchRelationshipsAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(mockRelationships);

        // Setup embedding manager to return null (no vector search)
        _mockEmbeddingManager
            .Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Embedding generation disabled for this test"));

        var options = new UnifiedSearchOptions
        {
            EnableVectorSearch = false, // Disable vector search for this test
            EnableFtsSearch = true,
        };

        // Act
        var results = await _unifiedSearchEngine.SearchAllSourcesAsync(query, sessionContext, options);

        // Assert
        Assert.NotNull(results);
        Assert.NotNull(results.Results);
        Assert.NotNull(results.Metrics);

        // Should have results from all three sources
        Assert.True(results.TotalResults >= 3);
        Assert.True(results.MemoryResults >= 1);
        Assert.True(results.EntityResults >= 1);
        Assert.True(results.RelationshipResults >= 1);

        // Verify metrics are populated
        Assert.True(results.Metrics.TotalDuration > TimeSpan.Zero);
        Assert.True(results.Metrics.MemoryFtsSearchDuration >= TimeSpan.Zero);
        Assert.True(results.Metrics.EntityFtsSearchDuration >= TimeSpan.Zero);
        Assert.True(results.Metrics.RelationshipFtsSearchDuration >= TimeSpan.Zero);

        _output.WriteLine(
            $"Search completed: {results.TotalResults} total results in {results.Metrics.TotalDuration.TotalMilliseconds}ms"
        );
    }

    [Fact]
    public async Task SearchAllSourcesAsync_WithVectorSearch_CallsVectorMethods()
    {
        // Arrange
        var sessionContext = CreateTestSessionContext();
        var query = "test query";
        var mockEmbedding = new float[] { 0.1f, 0.2f, 0.3f };

        // Setup mock responses for FTS searches
        _mockMemoryRepository
            .Setup(x =>
                x.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Memory>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchEntitiesAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entity>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchRelationshipsAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Relationship>());

        // Setup mock responses for vector searches
        _mockMemoryRepository
            .Setup(x =>
                x.SearchVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<VectorSearchResult>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchEntitiesVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<EntityVectorSearchResult>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchRelationshipsVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<RelationshipVectorSearchResult>());

        _mockEmbeddingManager
            .Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockEmbedding);

        var options = new UnifiedSearchOptions { EnableVectorSearch = true, EnableFtsSearch = true };

        // Act
        var results = await _unifiedSearchEngine.SearchAllSourcesAsync(query, sessionContext, options);

        // Assert
        Assert.NotNull(results);

        // Verify that vector search methods were called
        _mockMemoryRepository.Verify(
            x =>
                x.SearchVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockGraphRepository.Verify(
            x =>
                x.SearchEntitiesVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockGraphRepository.Verify(
            x =>
                x.SearchRelationshipsVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        _output.WriteLine($"Vector search completed: {results.TotalResults} total results");
    }

    [Fact]
    public async Task SearchAllSourcesAsync_WithPreGeneratedEmbedding_SkipsEmbeddingGeneration()
    {
        // Arrange
        var sessionContext = CreateTestSessionContext();
        var query = "test query";
        var preGeneratedEmbedding = new float[] { 0.1f, 0.2f, 0.3f };

        // Setup mock responses
        _mockMemoryRepository
            .Setup(x =>
                x.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Memory>());

        _mockMemoryRepository
            .Setup(x =>
                x.SearchVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<VectorSearchResult>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchEntitiesAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entity>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchEntitiesVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<EntityVectorSearchResult>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchRelationshipsAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Relationship>());

        _mockGraphRepository
            .Setup(x =>
                x.SearchRelationshipsVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<RelationshipVectorSearchResult>());

        var options = new UnifiedSearchOptions { EnableVectorSearch = true, EnableFtsSearch = true };

        // Act
        var results = await _unifiedSearchEngine.SearchAllSourcesAsync(
            query,
            preGeneratedEmbedding,
            sessionContext,
            options
        );

        // Assert
        Assert.NotNull(results);

        // Verify that embedding generation was NOT called since we provided a pre-generated embedding
        _mockEmbeddingManager.Verify(
            x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );

        _output.WriteLine($"Pre-generated embedding search completed: {results.TotalResults} total results");
    }

    [Fact]
    public async Task SearchAllSourcesAsync_WithDisabledOptions_SkipsDisabledSearches()
    {
        // Arrange
        var sessionContext = CreateTestSessionContext();
        var query = "test query";

        var options = new UnifiedSearchOptions
        {
            EnableVectorSearch = false,
            EnableFtsSearch = false, // This should result in no searches being performed
        };

        // Act
        var results = await _unifiedSearchEngine.SearchAllSourcesAsync(query, sessionContext, options);

        // Assert
        Assert.NotNull(results);
        Assert.Empty(results.Results);
        Assert.Equal(0, results.TotalResults);

        // Verify that no search methods were called
        _mockMemoryRepository.Verify(
            x =>
                x.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _mockMemoryRepository.Verify(
            x =>
                x.SearchVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _mockGraphRepository.Verify(
            x =>
                x.SearchEntitiesAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _mockGraphRepository.Verify(
            x =>
                x.SearchEntitiesVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _mockGraphRepository.Verify(
            x =>
                x.SearchRelationshipsAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
        _mockGraphRepository.Verify(
            x =>
                x.SearchRelationshipsVectorAsync(
                    It.IsAny<float[]>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );

        _output.WriteLine("Disabled searches correctly skipped all search operations");
    }

    [Fact]
    public async Task SearchAllSourcesAsync_WithTypeWeights_AppliesWeightsCorrectly()
    {
        // Arrange
        var sessionContext = CreateTestSessionContext();
        var query = "test query";

        // Setup mock responses with different scores
        var mockMemories = new List<Memory>
        {
            new Memory
            {
                Id = 1,
                Content = "Test memory",
                UserId = sessionContext.UserId,
                CreatedAt = DateTime.UtcNow,
            },
        };

        var mockEntities = new List<Entity>
        {
            new Entity
            {
                Id = 1,
                Name = "Test Entity",
                Type = "Person",
                UserId = sessionContext.UserId,
                CreatedAt = DateTime.UtcNow,
            },
        };

        var mockRelationships = new List<Relationship>
        {
            new Relationship
            {
                Id = 1,
                Source = "Entity1",
                Target = "Entity2",
                RelationshipType = "WORKS_WITH",
                UserId = sessionContext.UserId,
                CreatedAt = DateTime.UtcNow,
            },
        };

        _mockMemoryRepository
            .Setup(x =>
                x.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<float>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(mockMemories);

        _mockGraphRepository
            .Setup(x =>
                x.SearchEntitiesAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(mockEntities);

        _mockGraphRepository
            .Setup(x =>
                x.SearchRelationshipsAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionContext>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(mockRelationships);

        var options = new UnifiedSearchOptions
        {
            EnableVectorSearch = false,
            EnableFtsSearch = true,
            TypeWeights = new Dictionary<UnifiedResultType, float>
            {
                { UnifiedResultType.Memory, 1.0f },
                { UnifiedResultType.Entity, 0.8f },
                { UnifiedResultType.Relationship, 0.6f },
            },
        };

        // Act
        var results = await _unifiedSearchEngine.SearchAllSourcesAsync(query, sessionContext, options);

        // Assert
        Assert.NotNull(results);
        Assert.True(results.TotalResults > 0);

        // Results should be ordered by weighted score (Memory > Entity > Relationship)
        var sortedResults = results.Results.OrderByDescending(r => r.Score).ToList();

        if (sortedResults.Count >= 3)
        {
            var memoryResult = sortedResults.FirstOrDefault(r => r.Type == UnifiedResultType.Memory);
            var entityResult = sortedResults.FirstOrDefault(r => r.Type == UnifiedResultType.Entity);
            var relationshipResult = sortedResults.FirstOrDefault(r => r.Type == UnifiedResultType.Relationship);

            if (memoryResult != null && entityResult != null && relationshipResult != null)
            {
                // Memory should have highest score due to weight of 1.0
                // Entity should have middle score due to weight of 0.8
                // Relationship should have lowest score due to weight of 0.6
                Assert.True(memoryResult.Score >= entityResult.Score);
                Assert.True(entityResult.Score >= relationshipResult.Score);
            }
        }

        _output.WriteLine($"Type weights applied: {results.TotalResults} results with proper weighting");
    }

    private SessionContext CreateTestSessionContext()
    {
        return new SessionContext
        {
            UserId = $"test_user_{Guid.NewGuid():N}",
            RunId = $"test_run_{Guid.NewGuid():N}",
            AgentId = $"test_agent_{Guid.NewGuid():N}",
        };
    }
}
