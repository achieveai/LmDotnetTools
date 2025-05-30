using MemoryServer.Models;

namespace MemoryServer.Tests.TestUtilities;

/// <summary>
/// Factory for generating test data for memory-related tests.
/// Supports data-driven testing with varied scenarios.
/// </summary>
public static class MemoryTestDataFactory
{
    /// <summary>
    /// Generates test data for memory content validation scenarios.
    /// </summary>
    public static IEnumerable<object[]> GetMemoryContentTestCases()
    {
        yield return new object[] { "Valid short content", true, "Normal content should be valid" };
        yield return new object[] { "Valid content with special chars: !@#$%^&*()", true, "Special characters should be allowed" };
        yield return new object[] { new string('A', 100), true, "100 character content should be valid" };
        yield return new object[] { new string('A', 1000), true, "1000 character content should be valid" };
        yield return new object[] { new string('A', 10000), true, "10000 character content should be at limit" };
        yield return new object[] { "", false, "Empty content should be invalid" };
        yield return new object[] { "   ", false, "Whitespace-only content should be invalid" };
        yield return new object[] { new string('A', 10001), false, "Content over 10000 chars should be invalid" };
        yield return new object[] { new string('A', 50000), false, "Very long content should be invalid" };
    }

    /// <summary>
    /// Generates test data for session context scenarios.
    /// </summary>
    public static IEnumerable<object[]> GetSessionContextTestCases()
    {
        yield return new object[] { "user1", null!, null!, "User-only session" };
        yield return new object[] { "user1", "agent1", null!, "User-agent session" };
        yield return new object[] { "user1", "agent1", "run1", "Full session context" };
        yield return new object[] { "user2", "agent2", "run2", "Different full session" };
        yield return new object[] { "user1", null!, "run1", "User-run session (no agent)" };
    }

    /// <summary>
    /// Generates test data for session context matching scenarios.
    /// </summary>
    public static IEnumerable<object[]> GetSessionMatchingTestCases()
    {
        // Format: context1, context2, shouldMatch, description
        yield return new object[] { 
            SessionContext.ForUser("user1"), 
            SessionContext.ForUser("user1"), 
            true, 
            "Same user contexts should match" 
        };
        
        yield return new object[] { 
            SessionContext.ForUser("user1"), 
            SessionContext.ForUser("user2"), 
            false, 
            "Different user contexts should not match" 
        };
        
        yield return new object[] { 
            SessionContext.ForAgent("user1", "agent1"), 
            SessionContext.ForAgent("user1", "agent1"), 
            true, 
            "Same user-agent contexts should match" 
        };
        
        yield return new object[] { 
            SessionContext.ForAgent("user1", "agent1"), 
            SessionContext.ForAgent("user1", "agent2"), 
            false, 
            "Different agent contexts should not match" 
        };
        
        yield return new object[] { 
            SessionContext.ForUser("user1"), 
            SessionContext.ForAgent("user1", "agent1"), 
            false, 
            "User context should not match user-agent context (strict matching)" 
        };
        
        yield return new object[] { 
            SessionContext.ForRun("user1", "agent1", "run1"), 
            SessionContext.ForRun("user1", "agent1", "run1"), 
            true, 
            "Same full contexts should match" 
        };
        
        yield return new object[] { 
            SessionContext.ForRun("user1", "agent1", "run1"), 
            SessionContext.ForRun("user1", "agent1", "run2"), 
            false, 
            "Different run contexts should not match" 
        };
    }

    /// <summary>
    /// Generates test data for search query scenarios.
    /// </summary>
    public static IEnumerable<object[]> GetSearchQueryTestCases()
    {
        yield return new object[] { "test", 10, 0.7f, "Normal search query" };
        yield return new object[] { "test query", 5, 0.8f, "Multi-word search query" };
        yield return new object[] { "special!@#", 20, 0.5f, "Special characters in query" };
        yield return new object[] { "", 10, 0.7f, "Empty query should return empty results" };
        yield return new object[] { "   ", 10, 0.7f, "Whitespace query should return empty results" };
        yield return new object[] { "test", 1, 0.9f, "Single result limit" };
        yield return new object[] { "test", 100, 0.1f, "High limit, low threshold" };
    }

    /// <summary>
    /// Generates test data for memory metadata scenarios.
    /// </summary>
    public static IEnumerable<object[]> GetMetadataTestCases()
    {
        yield return new object?[] { 
            null, 
            "Null metadata" 
        };
        
        yield return new object[] { 
            new Dictionary<string, object>(), 
            "Empty metadata dictionary" 
        };
        
        yield return new object[] { 
            new Dictionary<string, object> { { "key1", "value1" } }, 
            "Single metadata entry" 
        };
        
        yield return new object[] { 
            new Dictionary<string, object> 
            { 
                { "key1", "value1" }, 
                { "key2", 42 }, 
                { "key3", true } 
            }, 
            "Multiple metadata entries with different types" 
        };
        
        yield return new object[] { 
            new Dictionary<string, object> 
            { 
                { "source", "api" }, 
                { "priority", 5 }, 
                { "tags", new[] { "important", "user-generated" } } 
            }, 
            "Complex metadata with arrays" 
        };
    }

    /// <summary>
    /// Creates a test memory with specified parameters.
    /// </summary>
    public static Memory CreateTestMemory(
        int id = 1, 
        string content = "Test memory content", 
        string userId = "test-user", 
        string? agentId = null, 
        string? runId = null,
        Dictionary<string, object>? metadata = null)
    {
        return new Memory
        {
            Id = id,
            Content = content,
            UserId = userId,
            AgentId = agentId,
            RunId = runId,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
            Version = 1
        };
    }

    /// <summary>
    /// Creates a list of test memories for bulk operations.
    /// </summary>
    public static List<Memory> CreateTestMemories(int count, SessionContext sessionContext)
    {
        var memories = new List<Memory>();
        for (int i = 1; i <= count; i++)
        {
            memories.Add(CreateTestMemory(
                id: i,
                content: $"Test memory content {i}",
                userId: sessionContext.UserId,
                agentId: sessionContext.AgentId,
                runId: sessionContext.RunId
            ));
        }
        return memories;
    }
} 