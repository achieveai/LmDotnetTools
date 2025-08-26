using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace MemoryServer.Tests.Services;

/// <summary>
/// Comprehensive tests for GraphDecisionEngine including conflict resolution, decision logic, and confidence calculations.
/// Uses data-driven testing approach with mocked dependencies for isolated testing.
/// </summary>
public class GraphDecisionEngineTests
{
    private readonly Mock<IGraphRepository> _mockRepository;
    private readonly Mock<ILogger<GraphDecisionEngine>> _mockLogger;
    private readonly GraphDecisionEngine _decisionEngine;

    public GraphDecisionEngineTests()
    {
        _mockRepository = new Mock<IGraphRepository>();
        _mockLogger = new Mock<ILogger<GraphDecisionEngine>>();
        _decisionEngine = new GraphDecisionEngine(_mockRepository.Object, _mockLogger.Object);

        Debug.WriteLine("✅ GraphDecisionEngine test setup complete");
    }

    #region Graph Update Analysis Tests

    [Theory]
    [MemberData(nameof(GraphUpdateAnalysisTestCases))]
    public async Task AnalyzeGraphUpdatesAsync_WithExtractedData_ShouldReturnCorrectInstructions(
        string testName,
        List<Entity> extractedEntities,
        List<Relationship> extractedRelationships,
        List<Entity> existingEntities,
        List<Relationship> existingRelationships,
        SessionContext sessionContext,
        int expectedInstructionCount,
        List<GraphDecisionOperation> expectedOperations
    )
    {
        // Arrange
        Debug.WriteLine($"Testing graph update analysis: {testName}");
        Debug.WriteLine(
            $"Extracted entities: {extractedEntities.Count}, relationships: {extractedRelationships.Count}"
        );

        SetupRepositoryMocks(existingEntities, existingRelationships, sessionContext);

        // Act
        var instructions = await _decisionEngine.AnalyzeGraphUpdatesAsync(
            extractedEntities,
            extractedRelationships,
            sessionContext
        );

        // Assert
        Assert.Equal(expectedInstructionCount, instructions.Count);

        for (int i = 0; i < expectedOperations.Count && i < instructions.Count; i++)
        {
            Assert.Equal(expectedOperations[i], instructions[i].Operation);
        }

        Debug.WriteLine($"✅ Generated {instructions.Count} instructions");
        foreach (var instruction in instructions)
        {
            Debug.WriteLine($"   - {instruction.Operation}: {instruction.Reasoning}");
        }
    }

    #endregion

    #region Entity Conflict Resolution Tests

    [Theory]
    [MemberData(nameof(EntityConflictTestCases))]
    public async Task ResolveEntityConflictAsync_WithConflictingEntities_ShouldReturnCorrectInstruction(
        string testName,
        Entity existingEntity,
        Entity newEntity,
        SessionContext sessionContext,
        GraphDecisionOperation expectedOperation,
        string expectedReasoningContains
    )
    {
        // Arrange
        Debug.WriteLine($"Testing entity conflict resolution: {testName}");
        Debug.WriteLine($"Existing: {existingEntity.Name} (conf: {existingEntity.Confidence})");
        Debug.WriteLine($"New: {newEntity.Name} (conf: {newEntity.Confidence})");

        // Act
        var instruction = await _decisionEngine.ResolveEntityConflictAsync(
            existingEntity,
            newEntity,
            sessionContext
        );

        // Assert
        Assert.Equal(expectedOperation, instruction.Operation);
        Assert.Contains(
            expectedReasoningContains,
            instruction.Reasoning,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(sessionContext.UserId, instruction.SessionContext.UserId);

        Debug.WriteLine($"✅ Conflict resolved with operation: {instruction.Operation}");
        Debug.WriteLine($"   Reasoning: {instruction.Reasoning}");
    }

    #endregion

    #region Relationship Conflict Resolution Tests

    [Theory]
    [MemberData(nameof(RelationshipConflictTestCases))]
    public async Task ResolveRelationshipConflictAsync_WithConflictingRelationships_ShouldReturnCorrectInstruction(
        string testName,
        Relationship existingRelationship,
        Relationship newRelationship,
        SessionContext sessionContext,
        GraphDecisionOperation expectedOperation,
        string expectedReasoningContains
    )
    {
        // Arrange
        Debug.WriteLine($"Testing relationship conflict resolution: {testName}");
        Debug.WriteLine(
            $"Existing: {existingRelationship.Source} --[{existingRelationship.RelationshipType}]--> {existingRelationship.Target} (conf: {existingRelationship.Confidence})"
        );
        Debug.WriteLine(
            $"New: {newRelationship.Source} --[{newRelationship.RelationshipType}]--> {newRelationship.Target} (conf: {newRelationship.Confidence})"
        );

        // Act
        var instruction = await _decisionEngine.ResolveRelationshipConflictAsync(
            existingRelationship,
            newRelationship,
            sessionContext
        );

        // Assert
        Assert.Equal(expectedOperation, instruction.Operation);
        Assert.Contains(
            expectedReasoningContains,
            instruction.Reasoning,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(sessionContext.UserId, instruction.SessionContext.UserId);

        Debug.WriteLine($"✅ Conflict resolved with operation: {instruction.Operation}");
        Debug.WriteLine($"   Reasoning: {instruction.Reasoning}");
    }

    #endregion

    #region Confidence Calculation Tests

    [Theory]
    [MemberData(nameof(ConfidenceCalculationTestCases))]
    public async Task ConfidenceCalculation_WithVariousScenarios_ShouldCalculateCorrectly(
        string testName,
        Entity existingEntity,
        Entity newEntity,
        float expectedMinConfidence,
        float expectedMaxConfidence
    )
    {
        // Arrange
        Debug.WriteLine($"Testing confidence calculation: {testName}");
        Debug.WriteLine(
            $"Existing confidence: {existingEntity.Confidence}, New confidence: {newEntity.Confidence}"
        );

        var sessionContext = new SessionContext { UserId = "user123" };

        // Act
        var instruction = await _decisionEngine.ResolveEntityConflictAsync(
            existingEntity,
            newEntity,
            sessionContext
        );

        // Assert
        Assert.True(
            instruction.Confidence >= expectedMinConfidence,
            $"Confidence {instruction.Confidence} should be >= {expectedMinConfidence}"
        );
        Assert.True(
            instruction.Confidence <= expectedMaxConfidence,
            $"Confidence {instruction.Confidence} should be <= {expectedMaxConfidence}"
        );

        Debug.WriteLine($"✅ Calculated confidence: {instruction.Confidence}");
    }

    #endregion

    #region Helper Methods

    private void SetupRepositoryMocks(
        List<Entity> existingEntities,
        List<Relationship> existingRelationships,
        SessionContext sessionContext
    )
    {
        // Setup entity lookups
        foreach (var entity in existingEntities)
        {
            _mockRepository
                .Setup(r =>
                    r.GetEntityByNameAsync(
                        entity.Name,
                        sessionContext,
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(entity);
        }

        // Setup relationship lookups
        _mockRepository
            .Setup(r =>
                r.GetRelationshipsAsync(
                    sessionContext,
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(existingRelationships);

        // Setup for entities not found
        var existingEntityNames = existingEntities.Select(e => e.Name).ToHashSet();
        _mockRepository
            .Setup(r =>
                r.GetEntityByNameAsync(
                    It.Is<string>(name => !existingEntityNames.Contains(name)),
                    sessionContext,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entity?)null);
    }

    #endregion

    #region Test Data

    public static IEnumerable<object[]> GraphUpdateAnalysisTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "New entities and relationships",
                new List<Entity>
                {
                    new Entity
                    {
                        Name = "John",
                        Type = "person",
                        UserId = "user123",
                        Confidence = 0.8f,
                    },
                    new Entity
                    {
                        Name = "Pizza",
                        Type = "food",
                        UserId = "user123",
                        Confidence = 0.9f,
                    },
                },
                new List<Relationship>
                {
                    new Relationship
                    {
                        Source = "John",
                        RelationshipType = "likes",
                        Target = "Pizza",
                        UserId = "user123",
                        Confidence = 0.8f,
                    },
                },
                new List<Entity>(), // No existing entities
                new List<Relationship>(), // No existing relationships
                new SessionContext { UserId = "user123" },
                3, // 2 ADD entities + 1 ADD relationship
                new List<GraphDecisionOperation>
                {
                    GraphDecisionOperation.ADD,
                    GraphDecisionOperation.ADD,
                    GraphDecisionOperation.ADD,
                },
            },
            new object[]
            {
                "Mixed new and existing entities",
                new List<Entity>
                {
                    new Entity
                    {
                        Name = "John",
                        Type = "person",
                        UserId = "user123",
                        Confidence = 0.9f,
                    }, // Higher confidence
                    new Entity
                    {
                        Name = "Alice",
                        Type = "person",
                        UserId = "user123",
                        Confidence = 0.8f,
                    }, // New entity
                },
                new List<Relationship>(),
                new List<Entity>
                {
                    new Entity
                    {
                        Id = 1,
                        Name = "John",
                        Type = "person",
                        UserId = "user123",
                        Confidence = 0.7f,
                    }, // Lower confidence
                },
                new List<Relationship>(),
                new SessionContext { UserId = "user123" },
                2, // 1 UPDATE existing + 1 ADD new
                new List<GraphDecisionOperation>
                {
                    GraphDecisionOperation.UPDATE,
                    GraphDecisionOperation.ADD,
                },
            },
        };

    public static IEnumerable<object[]> EntityConflictTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "Higher confidence new entity",
                new Entity
                {
                    Id = 1,
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.6f,
                },
                new Entity
                {
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.9f,
                },
                new SessionContext { UserId = "user123" },
                GraphDecisionOperation.UPDATE,
                "higher confidence",
            },
            new object[]
            {
                "Lower confidence new entity",
                new Entity
                {
                    Id = 1,
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.9f,
                },
                new Entity
                {
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.6f,
                },
                new SessionContext { UserId = "user123" },
                GraphDecisionOperation.NONE,
                "lower confidence",
            },
            new object[]
            {
                "Same confidence with type refinement",
                new Entity
                {
                    Id = 1,
                    Name = "Entity",
                    Type = "unknown",
                    UserId = "user123",
                    Confidence = 0.8f,
                },
                new Entity
                {
                    Name = "Entity",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.8f,
                },
                new SessionContext { UserId = "user123" },
                GraphDecisionOperation.UPDATE,
                "type refinement",
            },
            new object[]
            {
                "Very low confidence new entity",
                new Entity
                {
                    Id = 1,
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.8f,
                },
                new Entity
                {
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.2f,
                },
                new SessionContext { UserId = "user123" },
                GraphDecisionOperation.NONE,
                "lower confidence",
            },
        };

    public static IEnumerable<object[]> RelationshipConflictTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "Higher confidence new relationship",
                new Relationship
                {
                    Id = 1,
                    Source = "John",
                    RelationshipType = "likes",
                    Target = "Pizza",
                    UserId = "user123",
                    Confidence = 0.6f,
                },
                new Relationship
                {
                    Source = "John",
                    RelationshipType = "likes",
                    Target = "Pizza",
                    UserId = "user123",
                    Confidence = 0.9f,
                },
                new SessionContext { UserId = "user123" },
                GraphDecisionOperation.UPDATE,
                "higher confidence",
            },
            new object[]
            {
                "Lower confidence new relationship",
                new Relationship
                {
                    Id = 1,
                    Source = "John",
                    RelationshipType = "likes",
                    Target = "Pizza",
                    UserId = "user123",
                    Confidence = 0.9f,
                },
                new Relationship
                {
                    Source = "John",
                    RelationshipType = "likes",
                    Target = "Pizza",
                    UserId = "user123",
                    Confidence = 0.6f,
                },
                new SessionContext { UserId = "user123" },
                GraphDecisionOperation.NONE,
                "lower confidence",
            },
            new object[]
            {
                "Temporal context update",
                new Relationship
                {
                    Id = 1,
                    Source = "Alice",
                    RelationshipType = "works_at",
                    Target = "Google",
                    UserId = "user123",
                    Confidence = 0.8f,
                    TemporalContext = null,
                },
                new Relationship
                {
                    Source = "Alice",
                    RelationshipType = "works_at",
                    Target = "Google",
                    UserId = "user123",
                    Confidence = 0.8f,
                    TemporalContext = "since 2023",
                },
                new SessionContext { UserId = "user123" },
                GraphDecisionOperation.UPDATE,
                "temporal context",
            },
        };

    public static IEnumerable<object[]> ConfidenceCalculationTestCases =>
        new List<object[]>
        {
            new object[]
            {
                "High confidence update",
                new Entity
                {
                    Id = 1,
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.7f,
                },
                new Entity
                {
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.9f,
                },
                0.8f, // Expected minimum confidence (average-ish)
                1.0f, // Expected maximum confidence
            },
            new object[]
            {
                "Low confidence scenario",
                new Entity
                {
                    Id = 1,
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.3f,
                },
                new Entity
                {
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.4f,
                },
                0.0f, // Expected minimum confidence
                0.5f, // Expected maximum confidence
            },
            new object[]
            {
                "Maximum confidence scenario",
                new Entity
                {
                    Id = 1,
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 0.9f,
                },
                new Entity
                {
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                    Confidence = 1.0f,
                },
                0.9f, // Expected minimum confidence
                1.0f, // Expected maximum confidence
            },
        };

    #endregion
}
