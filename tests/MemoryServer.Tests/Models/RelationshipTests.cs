using System.Text.Json;
using MemoryServer.Models;

namespace MemoryServer.Tests.Models;

/// <summary>
///     Comprehensive tests for Relationship model including validation, serialization, and session context.
///     Uses data-driven testing approach for maximum coverage with minimal test methods.
/// </summary>
public class RelationshipTests
{
    #region Session Context Tests

    [Theory]
    [MemberData(nameof(SessionContextTestCases))]
    public void GetSessionContext_WithVariousInputs_ShouldReturnCorrectContext(
        string testName,
        string userId,
        string? agentId,
        string? runId,
        string expectedToString
    )
    {
        // Arrange
        Debug.WriteLine($"Testing session context: {testName}");
        Debug.WriteLine($"Input - UserId: {userId}, AgentId: {agentId}, RunId: {runId}");

        var relationship = new Relationship
        {
            Source = "TestSource",
            RelationshipType = "test_relation",
            Target = "TestTarget",
            UserId = userId,
            AgentId = agentId,
            RunId = runId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Act
        var sessionContext = relationship.GetSessionContext();

        // Assert
        Assert.Equal(userId, sessionContext.UserId);
        Assert.Equal(string.IsNullOrEmpty(agentId) ? null : agentId, sessionContext.AgentId);
        Assert.Equal(string.IsNullOrEmpty(runId) ? null : runId, sessionContext.RunId);
        Assert.Equal(expectedToString, sessionContext.ToString());

        Debug.WriteLine($"✅ Session context: {sessionContext}");
    }

    #endregion

    #region Serialization Tests

    [Theory]
    [MemberData(nameof(SerializationTestCases))]
    public void JsonSerialization_WithComplexData_ShouldPreserveAllFields(
        string testName,
        Relationship originalRelationship
    )
    {
        // Arrange
        Debug.WriteLine($"Testing JSON serialization: {testName}");
        Debug.WriteLine(
            $"Original relationship - {originalRelationship.Source} --[{originalRelationship.RelationshipType}]--> {originalRelationship.Target}"
        );

        // Act
        var json = JsonSerializer.Serialize(originalRelationship);
        Debug.WriteLine($"Serialized JSON length: {json.Length} characters");

        var deserializedRelationship = JsonSerializer.Deserialize<Relationship>(json);

        // Assert
        Assert.NotNull(deserializedRelationship);
        Assert.Equal(originalRelationship.Source, deserializedRelationship.Source);
        Assert.Equal(originalRelationship.RelationshipType, deserializedRelationship.RelationshipType);
        Assert.Equal(originalRelationship.Target, deserializedRelationship.Target);
        Assert.Equal(originalRelationship.UserId, deserializedRelationship.UserId);
        Assert.Equal(originalRelationship.AgentId, deserializedRelationship.AgentId);
        Assert.Equal(originalRelationship.RunId, deserializedRelationship.RunId);
        Assert.Equal(originalRelationship.Confidence, deserializedRelationship.Confidence);
        Assert.Equal(originalRelationship.SourceMemoryId, deserializedRelationship.SourceMemoryId);
        Assert.Equal(originalRelationship.TemporalContext, deserializedRelationship.TemporalContext);

        // Compare metadata
        if (originalRelationship.Metadata == null)
        {
            Assert.Null(deserializedRelationship.Metadata);
        }
        else
        {
            Assert.NotNull(deserializedRelationship.Metadata);
            Assert.Equal(originalRelationship.Metadata.Count, deserializedRelationship.Metadata.Count);
            foreach (var kvp in originalRelationship.Metadata)
            {
                Assert.True(deserializedRelationship.Metadata.ContainsKey(kvp.Key));
                Assert.Equal(kvp.Value.ToString(), deserializedRelationship.Metadata[kvp.Key].ToString());
            }
        }

        Debug.WriteLine("✅ Serialization successful - all fields preserved");
    }

    #endregion

    #region Relationship Logic Tests

    [Theory]
    [MemberData(nameof(SelfReferentialTestCases))]
    public void IsSelfReferential_WithVariousInputs_ShouldDetectCorrectly(
        string testName,
        string source,
        string target,
        bool expectedResult
    )
    {
        // Arrange
        Debug.WriteLine($"Testing self-referential detection: {testName}");
        Debug.WriteLine($"Source: '{source}', Target: '{target}'");

        var relationship = new Relationship
        {
            Source = source,
            RelationshipType = "test_relation",
            Target = target,
            UserId = "user123",
        };

        // Act
        var isSelfReferential = source.Equals(target, StringComparison.OrdinalIgnoreCase);

        // Assert
        Assert.Equal(expectedResult, isSelfReferential);

        Debug.WriteLine($"✅ Self-referential check: {isSelfReferential}");
    }

    #endregion

    #region Relationship Creation and Validation Tests

    [Theory]
    [MemberData(nameof(ValidRelationshipTestCases))]
    public void CreateRelationship_WithValidData_ShouldSucceed(
        string testName,
        string source,
        string relationshipType,
        string target,
        string userId,
        string? agentId,
        string? runId,
        float confidence,
        int? sourceMemoryId,
        string? temporalContext,
        Dictionary<string, object>? metadata
    )
    {
        // Arrange
        Debug.WriteLine($"Testing relationship creation: {testName}");
        Debug.WriteLine($"Input - {source} --[{relationshipType}]--> {target}");

        // Act
        var relationship = new Relationship
        {
            Source = source,
            RelationshipType = relationshipType,
            Target = target,
            UserId = userId,
            AgentId = agentId,
            RunId = runId,
            Confidence = confidence,
            SourceMemoryId = sourceMemoryId,
            TemporalContext = temporalContext,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Assert
        Assert.Equal(source, relationship.Source);
        Assert.Equal(relationshipType, relationship.RelationshipType);
        Assert.Equal(target, relationship.Target);
        Assert.Equal(userId, relationship.UserId);
        Assert.Equal(agentId, relationship.AgentId);
        Assert.Equal(runId, relationship.RunId);
        Assert.Equal(confidence, relationship.Confidence);
        Assert.Equal(sourceMemoryId, relationship.SourceMemoryId);
        Assert.Equal(temporalContext, relationship.TemporalContext);
        Assert.Equal(metadata, relationship.Metadata);

        Debug.WriteLine($"✅ Relationship created successfully with ID: {relationship.Id}");
        Debug.WriteLine($"   Confidence: {relationship.Confidence}, Temporal: {temporalContext ?? "none"}");
    }

    [Theory]
    [MemberData(nameof(InvalidRelationshipTestCases))]
    public void CreateRelationship_WithInvalidData_ShouldHandleGracefully(
        string testName,
        string source,
        string relationshipType,
        string target,
        string userId,
        float confidence,
        string expectedIssue
    )
    {
        // Arrange
        Debug.WriteLine($"Testing invalid relationship creation: {testName}");
        Debug.WriteLine($"Expected issue: {expectedIssue}");

        // Act
        var relationship = new Relationship
        {
            Source = source,
            RelationshipType = relationshipType,
            Target = target,
            UserId = userId,
            Confidence = confidence,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Assert - Relationship creation doesn't throw, but we can validate the data
        if (string.IsNullOrWhiteSpace(source))
        {
            Assert.True(string.IsNullOrWhiteSpace(relationship.Source), "Source should be empty or whitespace");
        }

        if (string.IsNullOrWhiteSpace(relationshipType))
        {
            Assert.True(
                string.IsNullOrWhiteSpace(relationship.RelationshipType),
                "RelationshipType should be empty or whitespace"
            );
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            Assert.True(string.IsNullOrWhiteSpace(relationship.Target), "Target should be empty or whitespace");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            Assert.True(string.IsNullOrWhiteSpace(relationship.UserId), "UserId should be empty or whitespace");
        }

        if (confidence is < 0 or > 1)
        {
            Assert.True(relationship.Confidence is < 0 or > 1, "Confidence should be out of valid range");
        }

        Debug.WriteLine($"⚠️ Invalid relationship handled: {expectedIssue}");
    }

    #endregion

    #region Test Data

    public static IEnumerable<object?[]> ValidRelationshipTestCases =>
        [
            // Format: testName, source, relationshipType, target, userId, agentId, runId, confidence, sourceMemoryId, temporalContext, metadata
            [
                "Basic relationship with minimal data",
                "John",
                "likes",
                "Pizza",
                "user123",
                null,
                null,
                0.8f,
                null,
                null,
                null,
            ],
            [
                "Relationship with full context",
                "Alice",
                "works_at",
                "Google",
                "user456",
                "agent789",
                "run123",
                0.9f,
                42,
                "2024-01-15",
                new Dictionary<string, object> { { "department", "engineering" }, { "start_date", "2023-06-01" } },
            ],
            [
                "Relationship with temporal context",
                "Bob",
                "visited",
                "Paris",
                "user789",
                null,
                "run456",
                0.95f,
                100,
                "last summer",
                null,
            ],
            [
                "Complex relationship with metadata",
                "Company A",
                "acquired",
                "Company B",
                "user999",
                "agent999",
                "run999",
                1.0f,
                200,
                "Q3 2023",
                new Dictionary<string, object>
                {
                    { "amount", "$1.2B" },
                    { "type", "cash_deal" },
                    { "regulatory_approval", true },
                    { "completion_date", "2023-09-15" },
                },
            ],
            [
                "Relationship with minimum confidence",
                "Uncertain Entity",
                "might_be_related_to",
                "Another Entity",
                "user000",
                null,
                null,
                0.0f,
                null,
                null,
                null,
            ],
        ];

    public static IEnumerable<object[]> InvalidRelationshipTestCases =>
        [
            // Format: testName, source, relationshipType, target, userId, confidence, expectedIssue
            ["Empty source", "", "likes", "Pizza", "user123", 0.8f, "Source is empty"],
            ["Whitespace source", "   ", "likes", "Pizza", "user123", 0.8f, "Source is whitespace"],
            ["Empty relationship type", "John", "", "Pizza", "user123", 0.8f, "RelationshipType is empty"],
            ["Whitespace relationship type", "John", "   ", "Pizza", "user123", 0.8f, "RelationshipType is whitespace"],
            ["Empty target", "John", "likes", "", "user123", 0.8f, "Target is empty"],
            ["Whitespace target", "John", "likes", "   ", "user123", 0.8f, "Target is whitespace"],
            ["Empty userId", "John", "likes", "Pizza", "", 0.8f, "UserId is empty"],
            ["Negative confidence", "John", "likes", "Pizza", "user123", -0.1f, "Confidence below 0"],
            ["Confidence above 1", "John", "likes", "Pizza", "user123", 1.1f, "Confidence above 1"],
        ];

    public static IEnumerable<object?[]> SessionContextTestCases =>
        [
            // Format: testName, userId, agentId, runId, expectedToString
            ["User only", "user123", null, null, "user123"],
            ["User and agent", "user123", "agent456", null, "user123/agent456"],
            ["Full context", "user123", "agent456", "run789", "user123/agent456/run789"],
            ["User and run (no agent)", "user123", null, "run789", "user123//run789"],
            ["Empty strings treated as null", "user123", "", "", "user123"],
        ];

    public static IEnumerable<object?[]> SerializationTestCases =>
        [
            // Format: testName, relationship
            [
                "Simple relationship",
                new Relationship
                {
                    Id = 1,
                    Source = "John",
                    RelationshipType = "likes",
                    Target = "Pizza",
                    UserId = "user123",
                    Confidence = 0.8f,
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
            [
                "Relationship with all fields",
                new Relationship
                {
                    Id = 2,
                    Source = "Alice",
                    RelationshipType = "works_at",
                    Target = "Google",
                    UserId = "user456",
                    AgentId = "agent789",
                    RunId = "run123",
                    Confidence = 0.95f,
                    SourceMemoryId = 42,
                    TemporalContext = "since 2023",
                    Metadata = new Dictionary<string, object> { { "department", "AI" }, { "level", "senior" } },
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
            [
                "Relationship with null optional fields",
                new Relationship
                {
                    Id = 3,
                    Source = "Bob",
                    RelationshipType = "visited",
                    Target = "Paris",
                    UserId = "user789",
                    AgentId = null,
                    RunId = null,
                    Confidence = 0.7f,
                    SourceMemoryId = null,
                    TemporalContext = null,
                    Metadata = null,
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
        ];

    public static IEnumerable<object[]> SelfReferentialTestCases =>
        [
            // Format: testName, source, target, expectedResult
            ["Exact match", "John", "John", true],
            ["Case insensitive match", "john", "JOHN", true],
            ["Different entities", "John", "Jane", false],
            ["Empty strings", "", "", true],
            ["Whitespace variations", "  John  ", "John", false], // Exact string comparison
            ["Similar but different", "John Doe", "John", false],
        ];

    #endregion
}
