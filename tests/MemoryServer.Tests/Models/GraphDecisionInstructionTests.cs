using System.Text.Json;
using MemoryServer.Models;

namespace MemoryServer.Tests.Models;

/// <summary>
/// Comprehensive tests for GraphDecisionInstruction model including validation, serialization, and operation logic.
/// Uses data-driven testing approach for maximum coverage with minimal test methods.
/// </summary>
public class GraphDecisionInstructionTests
{
    #region Instruction Creation and Validation Tests

    [Theory]
    [MemberData(nameof(ValidInstructionTestCases))]
    public void CreateInstruction_WithValidData_ShouldSucceed(
        string testName,
        GraphDecisionOperation operation,
        Entity? entityData,
        Relationship? relationshipData,
        float confidence,
        string reasoning,
        SessionContext sessionContext
    )
    {
        // Arrange
        Debug.WriteLine($"Testing instruction creation: {testName}");
        Debug.WriteLine($"Operation: {operation}, Confidence: {confidence}");

        // Act
        var instruction = new GraphDecisionInstruction
        {
            Operation = operation,
            EntityData = entityData,
            RelationshipData = relationshipData,
            Confidence = confidence,
            Reasoning = reasoning,
            SessionContext = sessionContext,
            CreatedAt = DateTime.UtcNow,
        };

        // Assert
        Assert.Equal(operation, instruction.Operation);
        Assert.Equal(entityData, instruction.EntityData);
        Assert.Equal(relationshipData, instruction.RelationshipData);
        Assert.Equal(confidence, instruction.Confidence);
        Assert.Equal(reasoning, instruction.Reasoning);
        Assert.Equal(sessionContext, instruction.SessionContext);

        Debug.WriteLine($"✅ Instruction created successfully");
        Debug.WriteLine($"   Reasoning: {reasoning}");
    }

    [Theory]
    [MemberData(nameof(InvalidInstructionTestCases))]
    public void CreateInstruction_WithInvalidData_ShouldHandleGracefully(
        string testName,
        GraphDecisionOperation operation,
        float confidence,
        string reasoning,
        string expectedIssue
    )
    {
        // Arrange
        Debug.WriteLine($"Testing invalid instruction creation: {testName}");
        Debug.WriteLine($"Expected issue: {expectedIssue}");

        // Act
        var instruction = new GraphDecisionInstruction
        {
            Operation = operation,
            Confidence = confidence,
            Reasoning = reasoning,
            SessionContext = new SessionContext { UserId = "user123" },
            CreatedAt = DateTime.UtcNow,
        };

        // Assert - Instruction creation doesn't throw, but we can validate the data
        if (confidence < 0 || confidence > 1)
        {
            Assert.True(
                instruction.Confidence < 0 || instruction.Confidence > 1,
                "Confidence should be out of valid range"
            );
        }
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            Assert.True(string.IsNullOrWhiteSpace(instruction.Reasoning), "Reasoning should be empty or whitespace");
        }

        Debug.WriteLine($"⚠️ Invalid instruction handled: {expectedIssue}");
    }

    #endregion

    #region Operation Logic Tests

    [Theory]
    [MemberData(nameof(OperationValidationTestCases))]
    public void ValidateOperation_WithVariousData_ShouldDetectCorrectly(
        string testName,
        GraphDecisionOperation operation,
        Entity? entityData,
        Relationship? relationshipData,
        bool expectedValid,
        string expectedIssue
    )
    {
        // Arrange
        Debug.WriteLine($"Testing operation validation: {testName}");
        Debug.WriteLine($"Operation: {operation}, Expected valid: {expectedValid}");

        var instruction = new GraphDecisionInstruction
        {
            Operation = operation,
            EntityData = entityData,
            RelationshipData = relationshipData,
            Confidence = 0.8f,
            Reasoning = "Test reasoning",
            SessionContext = new SessionContext { UserId = "user123" },
        };

        // Act & Assert
        bool isValid = true;
        string actualIssue = "";

        // Validate based on operation type
        switch (operation)
        {
            case GraphDecisionOperation.ADD:
            case GraphDecisionOperation.UPDATE:
            case GraphDecisionOperation.DELETE:
                // Should have either entity or relationship data, but not both
                if (entityData != null && relationshipData != null)
                {
                    isValid = false;
                    actualIssue = "Cannot have both entity and relationship data";
                }
                else if (entityData == null && relationshipData == null)
                {
                    isValid = false;
                    actualIssue = "Must have either entity or relationship data";
                }
                break;
            case GraphDecisionOperation.NONE:
                // Should not have any data
                if (entityData != null || relationshipData != null)
                {
                    isValid = false;
                    actualIssue = "NONE operation should not have data";
                }
                break;
        }

        Assert.Equal(expectedValid, isValid);
        if (!expectedValid)
        {
            Assert.Contains(expectedIssue, actualIssue);
        }

        Debug.WriteLine($"✅ Validation result: {isValid}");
        if (!isValid)
        {
            Debug.WriteLine($"   Issue: {actualIssue}");
        }
    }

    #endregion

    #region Serialization Tests

    [Theory]
    [MemberData(nameof(SerializationTestCases))]
    public void JsonSerialization_WithComplexData_ShouldPreserveAllFields(
        string testName,
        GraphDecisionInstruction originalInstruction
    )
    {
        // Arrange
        Debug.WriteLine($"Testing JSON serialization: {testName}");
        Debug.WriteLine($"Original operation: {originalInstruction.Operation}");

        // Act
        var json = JsonSerializer.Serialize(originalInstruction);
        Debug.WriteLine($"Serialized JSON length: {json.Length} characters");

        var deserializedInstruction = JsonSerializer.Deserialize<GraphDecisionInstruction>(json);

        // Assert
        Assert.NotNull(deserializedInstruction);
        Assert.Equal(originalInstruction.Operation, deserializedInstruction.Operation);
        Assert.Equal(originalInstruction.Confidence, deserializedInstruction.Confidence);
        Assert.Equal(originalInstruction.Reasoning, deserializedInstruction.Reasoning);

        // Compare session context
        Assert.Equal(originalInstruction.SessionContext.UserId, deserializedInstruction.SessionContext.UserId);
        Assert.Equal(originalInstruction.SessionContext.AgentId, deserializedInstruction.SessionContext.AgentId);
        Assert.Equal(originalInstruction.SessionContext.RunId, deserializedInstruction.SessionContext.RunId);

        // Compare entity data
        if (originalInstruction.EntityData == null)
        {
            Assert.Null(deserializedInstruction.EntityData);
        }
        else
        {
            Assert.NotNull(deserializedInstruction.EntityData);
            Assert.Equal(originalInstruction.EntityData.Name, deserializedInstruction.EntityData.Name);
            Assert.Equal(originalInstruction.EntityData.Type, deserializedInstruction.EntityData.Type);
        }

        // Compare relationship data
        if (originalInstruction.RelationshipData == null)
        {
            Assert.Null(deserializedInstruction.RelationshipData);
        }
        else
        {
            Assert.NotNull(deserializedInstruction.RelationshipData);
            Assert.Equal(originalInstruction.RelationshipData.Source, deserializedInstruction.RelationshipData.Source);
            Assert.Equal(
                originalInstruction.RelationshipData.RelationshipType,
                deserializedInstruction.RelationshipData.RelationshipType
            );
            Assert.Equal(originalInstruction.RelationshipData.Target, deserializedInstruction.RelationshipData.Target);
        }

        Debug.WriteLine($"✅ Serialization successful - all fields preserved");
    }

    #endregion

    #region Test Data

    public static IEnumerable<object?[]> ValidInstructionTestCases =>
        new List<object?[]>
        {
            // Format: testName, operation, entityData, relationshipData, confidence, reasoning, sessionContext
            new object?[]
            {
                "Add entity instruction",
                GraphDecisionOperation.ADD,
                new Entity
                {
                    Name = "John",
                    Type = "person",
                    UserId = "user123",
                },
                null,
                0.8f,
                "New entity detected in conversation",
                new SessionContext { UserId = "user123" },
            },
            new object?[]
            {
                "Update relationship instruction",
                GraphDecisionOperation.UPDATE,
                null,
                new Relationship
                {
                    Source = "Alice",
                    RelationshipType = "works_at",
                    Target = "Google",
                    UserId = "user456",
                },
                0.9f,
                "Relationship confidence updated based on new information",
                new SessionContext { UserId = "user456", AgentId = "agent789" },
            },
            new object?[]
            {
                "Delete entity instruction",
                GraphDecisionOperation.DELETE,
                new Entity { Name = "Obsolete Entity", UserId = "user789" },
                null,
                0.7f,
                "Entity no longer relevant based on conversation context",
                new SessionContext
                {
                    UserId = "user789",
                    AgentId = "agent123",
                    RunId = "run456",
                },
            },
            new object?[]
            {
                "No operation instruction",
                GraphDecisionOperation.NONE,
                null,
                null,
                0.5f,
                "No changes needed to the graph",
                new SessionContext { UserId = "user000" },
            },
        };

    public static IEnumerable<object[]> InvalidInstructionTestCases =>
        new List<object[]>
        {
            // Format: testName, operation, confidence, reasoning, expectedIssue
            new object[]
            {
                "Negative confidence",
                GraphDecisionOperation.ADD,
                -0.1f,
                "Valid reasoning",
                "Confidence below 0",
            },
            new object[]
            {
                "Confidence above 1",
                GraphDecisionOperation.UPDATE,
                1.1f,
                "Valid reasoning",
                "Confidence above 1",
            },
            new object[] { "Empty reasoning", GraphDecisionOperation.DELETE, 0.8f, "", "Reasoning is empty" },
            new object[] { "Whitespace reasoning", GraphDecisionOperation.ADD, 0.8f, "   ", "Reasoning is whitespace" },
            new object[]
            {
                "Extreme negative confidence",
                GraphDecisionOperation.NONE,
                -999.0f,
                "Valid reasoning",
                "Extreme negative confidence",
            },
            new object[]
            {
                "Extreme positive confidence",
                GraphDecisionOperation.UPDATE,
                999.0f,
                "Valid reasoning",
                "Extreme positive confidence",
            },
        };

    public static IEnumerable<object?[]> OperationValidationTestCases =>
        new List<object?[]>
        {
            // Format: testName, operation, entityData, relationshipData, expectedValid, expectedIssue
            new object?[]
            {
                "Valid ADD entity",
                GraphDecisionOperation.ADD,
                new Entity { Name = "Test", UserId = "user123" },
                null,
                true,
                "",
            },
            new object?[]
            {
                "Valid UPDATE relationship",
                GraphDecisionOperation.UPDATE,
                null,
                new Relationship
                {
                    Source = "A",
                    RelationshipType = "rel",
                    Target = "B",
                    UserId = "user123",
                },
                true,
                "",
            },
            new object?[]
            {
                "Invalid - both entity and relationship",
                GraphDecisionOperation.ADD,
                new Entity { Name = "Test", UserId = "user123" },
                new Relationship
                {
                    Source = "A",
                    RelationshipType = "rel",
                    Target = "B",
                    UserId = "user123",
                },
                false,
                "both entity and relationship",
            },
            new object?[]
            {
                "Invalid - no data for ADD",
                GraphDecisionOperation.ADD,
                null,
                null,
                false,
                "either entity or relationship",
            },
            new object?[] { "Valid NONE operation", GraphDecisionOperation.NONE, null, null, true, "" },
            new object?[]
            {
                "Invalid NONE with data",
                GraphDecisionOperation.NONE,
                new Entity { Name = "Test", UserId = "user123" },
                null,
                false,
                "NONE operation should not have data",
            },
        };

    public static IEnumerable<object?[]> SerializationTestCases =>
        new List<object?[]>
        {
            // Format: testName, instruction
            new object?[]
            {
                "Entity instruction",
                new GraphDecisionInstruction
                {
                    Operation = GraphDecisionOperation.ADD,
                    EntityData = new Entity
                    {
                        Id = 1,
                        Name = "Test Entity",
                        Type = "test",
                        UserId = "user123",
                        Confidence = 0.8f,
                    },
                    RelationshipData = null,
                    Confidence = 0.9f,
                    Reasoning = "Test entity reasoning",
                    SessionContext = new SessionContext { UserId = "user123" },
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            },
            new object?[]
            {
                "Relationship instruction",
                new GraphDecisionInstruction
                {
                    Operation = GraphDecisionOperation.UPDATE,
                    EntityData = null,
                    RelationshipData = new Relationship
                    {
                        Id = 2,
                        Source = "Alice",
                        RelationshipType = "works_at",
                        Target = "Google",
                        UserId = "user456",
                        Confidence = 0.95f,
                    },
                    Confidence = 0.85f,
                    Reasoning = "Relationship update reasoning",
                    SessionContext = new SessionContext { UserId = "user456", AgentId = "agent789" },
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            },
            new object?[]
            {
                "NONE operation instruction",
                new GraphDecisionInstruction
                {
                    Operation = GraphDecisionOperation.NONE,
                    EntityData = null,
                    RelationshipData = null,
                    Confidence = 0.5f,
                    Reasoning = "No changes needed",
                    SessionContext = new SessionContext
                    {
                        UserId = "user789",
                        AgentId = "agent123",
                        RunId = "run456",
                    },
                    CreatedAt = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                },
            },
        };

    #endregion
}
