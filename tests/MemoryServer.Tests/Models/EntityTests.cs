using System.Text.Json;
using MemoryServer.Models;

namespace MemoryServer.Tests.Models;

/// <summary>
/// Comprehensive tests for Entity model including validation, serialization, and session context.
/// Uses data-driven testing approach for maximum coverage with minimal test methods.
/// </summary>
public class EntityTests
{
    #region Entity Creation and Validation Tests

    [Theory]
    [MemberData(nameof(ValidEntityTestCases))]
    public void CreateEntity_WithValidData_ShouldSucceed(
        string testName,
        string name,
        string? type,
        List<string>? aliases,
        string userId,
        string? agentId,
        string? runId,
        float confidence,
        List<int>? sourceMemoryIds,
        Dictionary<string, object>? metadata
    )
    {
        // Arrange
        Debug.WriteLine($"Testing entity creation: {testName}");
        Debug.WriteLine($"Input - Name: {name}, Type: {type}, UserId: {userId}");

        // Act
        var entity = new Entity
        {
            Name = name,
            Type = type,
            Aliases = aliases,
            UserId = userId,
            AgentId = agentId,
            RunId = runId,
            Confidence = confidence,
            SourceMemoryIds = sourceMemoryIds,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Assert
        Assert.Equal(name, entity.Name);
        Assert.Equal(type, entity.Type);
        Assert.Equal(aliases, entity.Aliases);
        Assert.Equal(userId, entity.UserId);
        Assert.Equal(agentId, entity.AgentId);
        Assert.Equal(runId, entity.RunId);
        Assert.Equal(confidence, entity.Confidence);
        Assert.Equal(sourceMemoryIds, entity.SourceMemoryIds);
        Assert.Equal(metadata, entity.Metadata);

        Debug.WriteLine($"✅ Entity created successfully with ID: {entity.Id}");
        Debug.WriteLine($"   Confidence: {entity.Confidence}, Aliases: {aliases?.Count ?? 0}");
    }

    [Theory]
    [MemberData(nameof(InvalidEntityTestCases))]
    public void CreateEntity_WithInvalidData_ShouldHandleGracefully(
        string testName,
        string name,
        string userId,
        float confidence,
        string expectedIssue
    )
    {
        // Arrange
        Debug.WriteLine($"Testing invalid entity creation: {testName}");
        Debug.WriteLine($"Expected issue: {expectedIssue}");

        // Act
        var entity = new Entity
        {
            Name = name,
            UserId = userId,
            Confidence = confidence,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Assert - Entity creation doesn't throw, but we can validate the data
        if (string.IsNullOrWhiteSpace(name))
        {
            Assert.True(string.IsNullOrWhiteSpace(entity.Name), "Name should be empty or whitespace");
        }
        if (string.IsNullOrWhiteSpace(userId))
        {
            Assert.True(string.IsNullOrWhiteSpace(entity.UserId), "UserId should be empty or whitespace");
        }
        if (confidence is < 0 or > 1)
        {
            Assert.True(entity.Confidence is < 0 or > 1, "Confidence should be out of valid range");
        }

        Debug.WriteLine($"⚠️ Invalid entity handled: {expectedIssue}");
    }

    #endregion

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

        var entity = new Entity
        {
            Name = "TestEntity",
            UserId = userId,
            AgentId = agentId,
            RunId = runId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Act
        var sessionContext = entity.GetSessionContext();

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
    public void JsonSerialization_WithComplexData_ShouldPreserveAllFields(string testName, Entity originalEntity)
    {
        // Arrange
        Debug.WriteLine($"Testing JSON serialization: {testName}");
        Debug.WriteLine(
            $"Original entity - Name: {originalEntity.Name}, Aliases: {originalEntity.Aliases?.Count ?? 0}"
        );

        // Act
        var json = JsonSerializer.Serialize(originalEntity);
        Debug.WriteLine($"Serialized JSON length: {json.Length} characters");

        var deserializedEntity = JsonSerializer.Deserialize<Entity>(json);

        // Assert
        Assert.NotNull(deserializedEntity);
        Assert.Equal(originalEntity.Name, deserializedEntity.Name);
        Assert.Equal(originalEntity.Type, deserializedEntity.Type);
        Assert.Equal(originalEntity.UserId, deserializedEntity.UserId);
        Assert.Equal(originalEntity.AgentId, deserializedEntity.AgentId);
        Assert.Equal(originalEntity.RunId, deserializedEntity.RunId);
        Assert.Equal(originalEntity.Confidence, deserializedEntity.Confidence);

        // Compare aliases
        if (originalEntity.Aliases == null)
        {
            Assert.Null(deserializedEntity.Aliases);
        }
        else
        {
            Assert.NotNull(deserializedEntity.Aliases);
            Assert.Equal(originalEntity.Aliases.Count, deserializedEntity.Aliases.Count);
            Assert.True(originalEntity.Aliases.SequenceEqual(deserializedEntity.Aliases));
        }

        // Compare source memory IDs
        if (originalEntity.SourceMemoryIds == null)
        {
            Assert.Null(deserializedEntity.SourceMemoryIds);
        }
        else
        {
            Assert.NotNull(deserializedEntity.SourceMemoryIds);
            Assert.Equal(originalEntity.SourceMemoryIds.Count, deserializedEntity.SourceMemoryIds.Count);
            Assert.True(originalEntity.SourceMemoryIds.SequenceEqual(deserializedEntity.SourceMemoryIds));
        }

        Debug.WriteLine($"✅ Serialization successful - all fields preserved");
    }

    #endregion

    #region Test Data

    public static IEnumerable<object?[]> ValidEntityTestCases =>
        [
            // Format: testName, name, type, aliases, userId, agentId, runId, confidence, sourceMemoryIds, metadata
            [
                "Basic entity with minimal data",
                "John Doe",
                "person",
                null,
                "user123",
                null,
                null,
                0.8f,
                null,
                null,
            ],
            [
                "Entity with aliases",
                "New York City",
                "place",
                new List<string> { "NYC", "The Big Apple", "Manhattan" },
                "user456",
                "agent789",
                null,
                0.9f,
                new List<int> { 1, 2, 3 },
                null,
            ],
            [
                "Entity with full session context",
                "Machine Learning",
                "concept",
                new List<string> { "ML", "AI subset" },
                "user789",
                "agent123",
                "run456",
                0.95f,
                new List<int> { 10, 20 },
                new Dictionary<string, object> { { "domain", "technology" }, { "complexity", "high" } },
            ],
            [
                "Entity with maximum confidence",
                "Earth",
                "planet",
                new List<string> { "Terra", "World", "Blue Planet" },
                "user999",
                "agent999",
                "run999",
                1.0f,
                new List<int> { 100, 200, 300, 400 },
                new Dictionary<string, object>
                {
                    { "type", "celestial_body" },
                    { "habitable", true },
                    { "radius_km", 6371 },
                },
            ],
            [
                "Entity with minimum confidence",
                "Uncertain Entity",
                "unknown",
                null,
                "user000",
                null,
                null,
                0.0f,
                null,
                null,
            ],
        ];

    public static IEnumerable<object[]> InvalidEntityTestCases =>
        [
            // Format: testName, name, userId, confidence, expectedIssue
            ["Empty name", "", "user123", 0.8f, "Name is empty"],
            ["Whitespace name", "   ", "user123", 0.8f, "Name is whitespace"],
            ["Empty userId", "Valid Name", "", 0.8f, "UserId is empty"],
            ["Negative confidence", "Valid Name", "user123", -0.1f, "Confidence below 0"],
            ["Confidence above 1", "Valid Name", "user123", 1.1f, "Confidence above 1"],
            [
                "Extreme negative confidence",
                "Valid Name",
                "user123",
                -999.0f,
                "Extreme negative confidence",
            ],
            [
                "Extreme positive confidence",
                "Valid Name",
                "user123",
                999.0f,
                "Extreme positive confidence",
            ],
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
            // Format: testName, entity
            [
                "Simple entity",
                new Entity
                {
                    Id = 1,
                    Name = "Test Entity",
                    Type = "test",
                    UserId = "user123",
                    Confidence = 0.8f,
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
            [
                "Entity with aliases and metadata",
                new Entity
                {
                    Id = 2,
                    Name = "Complex Entity",
                    Type = "complex",
                    Aliases = ["alias1", "alias2"],
                    UserId = "user456",
                    AgentId = "agent789",
                    RunId = "run123",
                    Confidence = 0.95f,
                    SourceMemoryIds = [1, 2, 3],
                    Metadata = new Dictionary<string, object> { { "key1", "value1" }, { "key2", 42 } },
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
            [
                "Entity with null collections",
                new Entity
                {
                    Id = 3,
                    Name = "Null Collections Entity",
                    Type = null,
                    Aliases = null,
                    UserId = "user789",
                    AgentId = null,
                    RunId = null,
                    Confidence = 0.5f,
                    SourceMemoryIds = null,
                    Metadata = null,
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                    UpdatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            ],
        ];

    #endregion
}
