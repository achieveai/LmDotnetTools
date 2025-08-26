using FluentAssertions;
using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.DocumentSegmentation.Utils;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MemoryServer.DocumentSegmentation.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the complete document segmentation workflow.
/// </summary>
public class DocumentSegmentationWorkflowTests : IAsyncDisposable
{
    private readonly DocumentSegmentationSessionIntegration _integration;
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly SessionContext _testSessionContext;

    public DocumentSegmentationWorkflowTests()
    {
        var loggerFactory = new LoggerFactory();

        // Set up services
        var sizeAnalyzer = new DocumentSizeAnalyzer(
            loggerFactory.CreateLogger<DocumentSizeAnalyzer>(),
            Options.Create(CreateTestOptions())
        );

        var promptManager = new SegmentationPromptManager(
            loggerFactory.CreateLogger<SegmentationPromptManager>(),
            Options.Create(CreateTestOptions())
        );

        var repository = new DocumentSegmentRepository(
            loggerFactory.CreateLogger<DocumentSegmentRepository>()
        );

        _sessionFactory = new TestSqliteSessionFactory(loggerFactory);

        _integration = new DocumentSegmentationSessionIntegration(
            sizeAnalyzer,
            promptManager,
            repository,
            _sessionFactory,
            loggerFactory.CreateLogger<DocumentSegmentationSessionIntegration>()
        );

        _testSessionContext = new SessionContext
        {
            UserId = "integration-test-user",
            AgentId = "integration-test-agent",
            RunId = "integration-test-run",
        };
    }

    [Fact]
    public async Task CompleteWorkflow_WithLargeDocument_ProcessesSuccessfully()
    {
        // Arrange
        var largeContent = CreateLargeTestDocument(2000); // 2000 words
        var parentDocumentId = 1001; // Unique document ID

        // Act
        var result = await _integration.ProcessDocumentWorkflowAsync(
            largeContent,
            parentDocumentId,
            _testSessionContext
        );

        // Assert
        result.Should().NotBeNull();
        result.IsComplete.Should().BeTrue();
        result.Error.Should().BeNull();
        result.ShouldSegment.Should().BeTrue();
        result.DocumentStatistics.WordCount.Should().BeGreaterThan(1500);
        result.Segments.Should().NotBeEmpty();
        result.StoredSegmentIds.Should().NotBeEmpty();
        result.StoredSegmentIds.Should().HaveCount(result.Segments.Count);
        result.VerificationSuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteWorkflow_WithSmallDocument_SkipsSegmentation()
    {
        // Arrange
        var smallContent = CreateSmallTestDocument(500); // 500 words
        var parentDocumentId = 2001; // Unique document ID

        // Act
        var result = await _integration.ProcessDocumentWorkflowAsync(
            smallContent,
            parentDocumentId,
            _testSessionContext
        );

        // Assert
        result.Should().NotBeNull();
        result.IsComplete.Should().BeTrue();
        result.Error.Should().BeNull();
        result.ShouldSegment.Should().BeFalse();
        result.DocumentStatistics.WordCount.Should().BeLessThan(1500);
        result.Segments.Should().BeEmpty();
        result.StoredSegmentIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteWorkflow_WithDifferentDocumentTypes_HandlesCorrectly()
    {
        // Arrange
        var content = CreateLargeTestDocument(1800);
        var parentDocumentId = 3001; // Unique document ID

        // Act - Test with Email document type (lower threshold)
        var emailResult = await _integration.ProcessDocumentWorkflowAsync(
            content,
            parentDocumentId,
            _testSessionContext,
            DocumentType.Email
        );

        // Assert
        emailResult.Should().NotBeNull();
        emailResult.ShouldSegment.Should().BeTrue();
        emailResult.DocumentType.Should().Be(DocumentType.Email);
        emailResult.DomainInstructions.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SessionIsolation_DifferentSessions_KeepDataSeparate()
    {
        // Arrange
        var content = CreateLargeTestDocument(1600);

        var session1Context = new SessionContext
        {
            UserId = "user1",
            AgentId = "agent1",
            RunId = "run1",
        };

        var session2Context = new SessionContext
        {
            UserId = "user2",
            AgentId = "agent2",
            RunId = "run2",
        };

        // Use different parent document IDs to avoid conflicts
        var parentDocumentId1 = 4001;
        var parentDocumentId2 = 4002;

        // Act
        var result1 = await _integration.ProcessDocumentWorkflowAsync(
            content,
            parentDocumentId1,
            session1Context
        );

        var result2 = await _integration.ProcessDocumentWorkflowAsync(
            content,
            parentDocumentId2,
            session2Context
        );

        // Assert
        result1.Should().NotBeNull();
        result2.Should().NotBeNull();

        result1.IsComplete.Should().BeTrue();
        result2.IsComplete.Should().BeTrue();

        result1.SessionContext.UserId.Should().Be("user1");
        result2.SessionContext.UserId.Should().Be("user2");

        // Both should have segments but they should be isolated
        result1.StoredSegmentIds.Should().NotBeEmpty();
        result2.StoredSegmentIds.Should().NotBeEmpty();
        result1.StoredSegmentIds.Should().NotIntersectWith(result2.StoredSegmentIds);
    }

    [Fact]
    public async Task WorkflowSteps_ExecuteInCorrectOrder()
    {
        // Arrange
        var content = CreateLargeTestDocument(1500);
        var parentDocumentId = 5001; // Unique document ID

        // Act
        var result = await _integration.ProcessDocumentWorkflowAsync(
            content,
            parentDocumentId,
            _testSessionContext
        );

        // Assert - Verify all workflow steps completed
        result.DocumentStatistics.Should().NotBeNull();
        result.DocumentStatistics.WordCount.Should().BeGreaterThan(0);

        result.PromptsValid.Should().BeTrue();
        result.AvailablePrompts.Should().NotBeEmpty();
        result.DomainInstructions.Should().NotBeEmpty();

        result.Segments.Should().NotBeEmpty();
        result.Relationships.Should().NotBeEmpty();

        result.StoredSegmentIds.Should().NotBeEmpty();
        result.StoredRelationshipCount.Should().BeGreaterThan(0);

        result.VerificationSuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task SegmentQuality_MeetsExpectedStandards()
    {
        // Arrange
        var content = CreateLargeTestDocument(1800);
        var parentDocumentId = 6001; // Unique document ID

        // Act
        var result = await _integration.ProcessDocumentWorkflowAsync(
            content,
            parentDocumentId,
            _testSessionContext
        );

        // Assert
        result.Segments.Should().NotBeEmpty();

        foreach (var segment in result.Segments)
        {
            // Quality scores should be reasonable
            segment.Quality.CoherenceScore.Should().BeInRange(0.0, 1.0);
            segment.Quality.IndependenceScore.Should().BeInRange(0.0, 1.0);
            segment.Quality.TopicConsistencyScore.Should().BeInRange(0.0, 1.0);

            // Content should not be empty
            segment.Content.Should().NotBeEmpty();
            segment.Id.Should().NotBeEmpty();
            segment.SequenceNumber.Should().BeGreaterThan(0);

            // Metadata should contain test markers
            segment.Metadata.Should().ContainKey("created_by");
            segment.Metadata["created_by"].Should().Be("workflow_demo");
        }
    }

    private string CreateLargeTestDocument(int targetWordCount)
    {
        var words = new[]
        {
            "document",
            "analysis",
            "processing",
            "artificial",
            "intelligence",
            "machine",
            "learning",
            "technology",
            "implementation",
            "framework",
            "architecture",
            "development",
            "software",
            "system",
            "integration",
            "performance",
            "optimization",
            "algorithm",
            "data",
            "structure",
            "methodology",
            "approach",
            "solution",
            "innovation",
            "research",
            "academic",
            "scientific",
        };

        var random = new Random(42); // Fixed seed for reproducible tests
        var result = new List<string>();

        for (int i = 0; i < targetWordCount; i++)
        {
            result.Add(words[random.Next(words.Length)]);

            // Add punctuation occasionally
            if (i > 0 && i % 15 == 0)
            {
                result[^1] += ".";
            }

            // Add paragraph breaks occasionally
            if (i > 0 && i % 100 == 0)
            {
                result.Add("\n\n");
            }
        }

        return string.Join(" ", result);
    }

    private string CreateSmallTestDocument(int targetWordCount)
    {
        var words = new[] { "short", "brief", "concise", "summary", "overview", "introduction" };
        var random = new Random(42);
        var result = new List<string>();

        for (int i = 0; i < targetWordCount; i++)
        {
            result.Add(words[random.Next(words.Length)]);
        }

        return string.Join(" ", result);
    }

    private DocumentSegmentationOptions CreateTestOptions()
    {
        return new DocumentSegmentationOptions
        {
            Thresholds = new SegmentationThresholds
            {
                MinDocumentSizeWords = 1500,
                MaxDocumentSizeWords = 50000,
                TargetSegmentSizeWords = 1000,
                MaxSegmentSizeWords = 2000,
                MinSegmentSizeWords = 100,
            },
            LlmOptions = new LlmSegmentationOptions
            {
                EnableLlmSegmentation = false, // Disabled for testing
                MaxRetries = 3,
                TimeoutSeconds = 30,
            },
            Prompts = new PromptOptions
            {
                FilePath = "prompts.yml",
                DefaultLanguage = "en",
                EnableHotReload = false,
                CacheExpiration = TimeSpan.FromMinutes(30),
            },
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionFactory is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_sessionFactory is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
