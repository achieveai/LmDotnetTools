using MemoryServer.DocumentSegmentation.Models;
using MemoryServer.DocumentSegmentation.Services;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Xunit;
using FluentAssertions;

namespace MemoryServer.DocumentSegmentation.Tests.Services;

/// <summary>
/// Integration tests for DocumentSegmentRepository service.
/// </summary>
public class DocumentSegmentRepositoryTests : IAsyncDisposable
{
  private readonly ILogger<DocumentSegmentRepository> _logger;
  private readonly DocumentSegmentRepository _repository;
  private readonly ISqliteSessionFactory _sessionFactory;
  private readonly SessionContext _testSessionContext;

  public DocumentSegmentRepositoryTests()
  {
    _logger = new LoggerFactory().CreateLogger<DocumentSegmentRepository>();
    _repository = new DocumentSegmentRepository(_logger);
    _sessionFactory = new TestSqliteSessionFactory(new LoggerFactory());
    
    _testSessionContext = new SessionContext
    {
      UserId = "test-user-123",
      AgentId = "test-agent-456",
      RunId = "test-run-789"
    };
  }

  [Fact]
  public async Task StoreSegmentsAsync_WithValidSegments_ReturnsSegmentIds()
  {
    // Arrange
    var segments = CreateTestSegments(3);
    var parentDocumentId = 1;

    await using var session = await _sessionFactory.CreateSessionAsync();

    // Act
    var result = await _repository.StoreSegmentsAsync(
      session, segments, parentDocumentId, _testSessionContext);

    // Assert
    result.Should().HaveCount(3);
    result.Should().AllSatisfy(id => id.Should().BeGreaterThan(0));
  }

  [Fact]
  public async Task GetDocumentSegmentsAsync_AfterStoringSegments_ReturnsStoredSegments()
  {
    // Arrange
    var originalSegments = CreateTestSegments(2);
    var parentDocumentId = 1;

    await using var session = await _sessionFactory.CreateSessionAsync();

    // Store segments first
    await _repository.StoreSegmentsAsync(
      session, originalSegments, parentDocumentId, _testSessionContext);

    // Act
    var retrievedSegments = await _repository.GetDocumentSegmentsAsync(
      session, parentDocumentId, _testSessionContext);

    // Assert
    retrievedSegments.Should().HaveCount(2);
    retrievedSegments.Should().BeInAscendingOrder(s => s.SequenceNumber);
    
    for (int i = 0; i < originalSegments.Count; i++)
    {
      var original = originalSegments[i];
      var retrieved = retrievedSegments[i];
      
      retrieved.Id.Should().Be(original.Id);
      retrieved.Content.Should().Be(original.Content);
      retrieved.Title.Should().Be(original.Title);
      retrieved.Summary.Should().Be(original.Summary);
      retrieved.SequenceNumber.Should().Be(original.SequenceNumber);
    }
  }

  [Fact]
  public async Task StoreSegmentRelationshipsAsync_WithValidRelationships_ReturnsCount()
  {
    // Arrange
    var segments = CreateTestSegments(2);
    var relationships = CreateTestRelationships(segments);
    var parentDocumentId = 1;

    await using var session = await _sessionFactory.CreateSessionAsync();

    // Store segments first
    await _repository.StoreSegmentsAsync(
      session, segments, parentDocumentId, _testSessionContext);

    // Act
    var result = await _repository.StoreSegmentRelationshipsAsync(
      session, relationships, _testSessionContext);

    // Assert
    result.Should().Be(1);
  }

  [Fact]
  public async Task GetSegmentRelationshipsAsync_AfterStoringRelationships_ReturnsStoredRelationships()
  {
    // Arrange
    var segments = CreateTestSegments(2);
    var originalRelationships = CreateTestRelationships(segments);
    var parentDocumentId = 1;

    await using var session = await _sessionFactory.CreateSessionAsync();

    // Store segments and relationships
    await _repository.StoreSegmentsAsync(
      session, segments, parentDocumentId, _testSessionContext);
    await _repository.StoreSegmentRelationshipsAsync(
      session, originalRelationships, _testSessionContext);

    // Act
    var retrievedRelationships = await _repository.GetSegmentRelationshipsAsync(
      session, parentDocumentId, _testSessionContext);

    // Assert
    retrievedRelationships.Should().HaveCount(1);
    var retrieved = retrievedRelationships[0];
    var original = originalRelationships[0];

    retrieved.SourceSegmentId.Should().Be(original.SourceSegmentId);
    retrieved.TargetSegmentId.Should().Be(original.TargetSegmentId);
    retrieved.RelationshipType.Should().Be(original.RelationshipType);
    retrieved.Strength.Should().Be(original.Strength);
  }

  [Fact]
  public async Task DeleteDocumentSegmentsAsync_RemovesSegmentsAndRelationships()
  {
    // Arrange
    var segments = CreateTestSegments(2);
    var relationships = CreateTestRelationships(segments);
    var parentDocumentId = 1;

    await using var session = await _sessionFactory.CreateSessionAsync();

    // Store segments and relationships
    await _repository.StoreSegmentsAsync(
      session, segments, parentDocumentId, _testSessionContext);
    await _repository.StoreSegmentRelationshipsAsync(
      session, relationships, _testSessionContext);

    // Act
    var deletedCount = await _repository.DeleteDocumentSegmentsAsync(
      session, parentDocumentId, _testSessionContext);

    // Assert
    deletedCount.Should().Be(2);

    // Verify segments are deleted
    var remainingSegments = await _repository.GetDocumentSegmentsAsync(
      session, parentDocumentId, _testSessionContext);
    remainingSegments.Should().BeEmpty();

    // Verify relationships are deleted
    var remainingRelationships = await _repository.GetSegmentRelationshipsAsync(
      session, parentDocumentId, _testSessionContext);
    remainingRelationships.Should().BeEmpty();
  }

  [Fact]
  public async Task SessionIsolation_DifferentSessions_DontSeeEachOthersData()
  {
    // Arrange
    var segments1 = CreateTestSegments(1);
    segments1[0].Id = $"seg-session1-{Guid.NewGuid():N}"; // Unique ID for session 1
    segments1[0].Content = "Content for session 1";
    
    var segments2 = CreateTestSegments(1);
    segments2[0].Id = $"seg-session2-{Guid.NewGuid():N}"; // Unique ID for session 2
    segments2[0].Content = "Content for session 2";

    var session1Context = new SessionContext
    {
      UserId = "user1",
      AgentId = "agent1",
      RunId = "run1"
    };

    var session2Context = new SessionContext
    {
      UserId = "user2",
      AgentId = "agent2", 
      RunId = "run2"
    };

    var parentDocumentId = 1;

    await using var session = await _sessionFactory.CreateSessionAsync();

    // Act
    await _repository.StoreSegmentsAsync(session, segments1, parentDocumentId, session1Context);
    await _repository.StoreSegmentsAsync(session, segments2, parentDocumentId, session2Context);

    var session1Segments = await _repository.GetDocumentSegmentsAsync(session, parentDocumentId, session1Context);
    var session2Segments = await _repository.GetDocumentSegmentsAsync(session, parentDocumentId, session2Context);

    // Assert
    session1Segments.Should().HaveCount(1);
    session1Segments[0].Content.Should().Be("Content for session 1");

    session2Segments.Should().HaveCount(1);
    session2Segments[0].Content.Should().Be("Content for session 2");
  }

  [Fact]
  public async Task SearchSegmentsAsync_WithMatchingQuery_ReturnsRelevantSegments()
  {
    // Arrange
    var segments = new List<DocumentSegment>
    {
      CreateTestSegment("ai-seg", 1, "This document discusses artificial intelligence and machine learning"),
      CreateTestSegment("weather-seg", 2, "The weather today is sunny and pleasant"),
      CreateTestSegment("tech-seg", 3, "AI technologies are revolutionizing the industry")
    };

    var parentDocumentId = 1;
    await using var session = await _sessionFactory.CreateSessionAsync();

    await _repository.StoreSegmentsAsync(session, segments, parentDocumentId, _testSessionContext);

    // Act - Use simple text query that will work with fallback LIKE search
    var results = await _repository.SearchSegmentsAsync(session, "artificial", _testSessionContext);

    // Assert
    results.Should().NotBeEmpty();
    results.Should().Contain(s => s.Content.Contains("artificial"));
    results.All(s => s.Metadata.ContainsKey("search_rank") || s.Metadata.ContainsKey("rank")).Should().BeTrue();
  }

  private List<DocumentSegment> CreateTestSegments(int count)
  {
    var segments = new List<DocumentSegment>();
    
    for (int i = 1; i <= count; i++)
    {
      segments.Add(CreateTestSegment($"test-segment-{i}", i, $"Test content for segment {i}"));
    }

    return segments;
  }

  private DocumentSegment CreateTestSegment(string baseId, int sequenceNumber, string content)
  {
    // Create unique ID to avoid collisions across tests
    var uniqueId = $"{baseId}-{sequenceNumber}-{Guid.NewGuid():N}";
    
    return new DocumentSegment
    {
      Id = uniqueId,
      SequenceNumber = sequenceNumber,
      Content = content,
      Title = $"Test Segment {sequenceNumber}",
      Summary = $"Summary for segment {sequenceNumber}",
      Quality = new SegmentQuality
      {
        CoherenceScore = 0.85,
        IndependenceScore = 0.75,
        TopicConsistencyScore = 0.80,
        PassesQualityThreshold = true
      },
      Metadata = new Dictionary<string, object>
      {
        ["test"] = true,
        ["created_by"] = "unit_test",
        ["unique_id"] = uniqueId
      }
    };
  }

  private List<SegmentRelationship> CreateTestRelationships(List<DocumentSegment> segments)
  {
    if (segments.Count < 2) return new List<SegmentRelationship>();

    return new List<SegmentRelationship>
    {
      new()
      {
        Id = Guid.NewGuid().ToString(),
        SourceSegmentId = segments[0].Id,
        TargetSegmentId = segments[1].Id,
        RelationshipType = SegmentRelationshipType.Sequential,
        Strength = 0.9,
        Metadata = new Dictionary<string, object>
        {
          ["test"] = true
        }
      }
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
