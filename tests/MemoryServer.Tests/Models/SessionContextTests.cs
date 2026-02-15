using MemoryServer.Models;
using MemoryServer.Tests.TestUtilities;

namespace MemoryServer.Tests.Models;

/// <summary>
///     Unit tests for the SessionContext model.
///     Tests session matching logic and factory methods.
/// </summary>
public class SessionContextTests
{
    [Theory]
    [MemberData(nameof(MemoryTestDataFactory.GetSessionMatchingTestCases), MemberType = typeof(MemoryTestDataFactory))]
    public void Matches_WithVariousSessionContexts_ReturnsExpectedResult(
        SessionContext context1,
        SessionContext context2,
        bool expectedMatch,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing session matching: {description}");
        Debug.WriteLine($"Context1: {context1}");
        Debug.WriteLine($"Context2: {context2}");
        Debug.WriteLine($"Expected match: {expectedMatch}");

        // Act
        var actualMatch = context1.Matches(context2);
        Debug.WriteLine($"Actual match: {actualMatch}");

        // Assert
        Assert.Equal(expectedMatch, actualMatch);

        // Test symmetry - matching should be symmetric
        var reverseMatch = context2.Matches(context1);
        Assert.Equal(expectedMatch, reverseMatch);
        Debug.WriteLine($"Reverse match (symmetry test): {reverseMatch}");

        Debug.WriteLine("✅ Session matching test passed");
    }

    [Fact]
    public void Matches_WithNullContext_ReturnsFalse()
    {
        // Arrange
        Debug.WriteLine("Testing Matches with null context");
        var context = SessionContext.ForUser("user1");
        Debug.WriteLine($"Context: {context}");

        // Act
        var result = context.Matches(null!);
        Debug.WriteLine($"Result: {result}");

        // Assert
        Assert.False(result);
        Debug.WriteLine("✅ Null context test passed");
    }

    [Theory]
    [MemberData(nameof(GetFactoryMethodTestData))]
    public void FactoryMethods_WithVariousInputs_CreateCorrectSessionContext(
        string factoryMethod,
        string userId,
        string? agentId,
        string? runId,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing factory method: {description}");
        Debug.WriteLine($"Method: {factoryMethod}, UserId: {userId}, AgentId: {agentId}, RunId: {runId}");

        // Act
        var context = factoryMethod switch
        {
            "ForUser" => SessionContext.ForUser(userId),
            "ForAgent" => SessionContext.ForAgent(userId, agentId!),
            "ForRun" => SessionContext.ForRun(userId, agentId!, runId!),
            _ => throw new ArgumentException($"Unknown factory method: {factoryMethod}"),
        };

        Debug.WriteLine($"Created context: {context}");

        // Assert
        Assert.Equal(userId, context.UserId);
        Assert.Equal(agentId, context.AgentId);
        Assert.Equal(runId, context.RunId);

        Debug.WriteLine("✅ Factory method test passed");
    }

    [Theory]
    [MemberData(nameof(GetSessionScopeTestData))]
    public void GetScope_WithVariousSessionContexts_ReturnsCorrectScope(
        SessionContext context,
        SessionScope expectedScope,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing GetScope: {description}");
        Debug.WriteLine($"Context: {context}");
        Debug.WriteLine($"Expected scope: {expectedScope}");

        // Act
        var actualScope = context.GetScope();
        Debug.WriteLine($"Actual scope: {actualScope}");

        // Assert
        Assert.Equal(expectedScope, actualScope);
        Debug.WriteLine("✅ GetScope test passed");
    }

    [Theory]
    [MemberData(nameof(GetToStringTestData))]
    public void ToString_WithVariousSessionContexts_ReturnsExpectedFormat(
        SessionContext context,
        string expectedFormat,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing ToString: {description}");
        Debug.WriteLine($"Context: UserId={context.UserId}, AgentId={context.AgentId}, RunId={context.RunId}");

        // Act
        var result = context.ToString();
        Debug.WriteLine($"ToString result: {result}");
        Debug.WriteLine($"Expected format: {expectedFormat}");

        // Assert
        Assert.Equal(expectedFormat, result);
        Debug.WriteLine("✅ ToString test passed");
    }

    [Theory]
    [MemberData(nameof(GetSessionIsolationTestData))]
    public void SessionIsolation_WithDifferentUsers_DoesNotMatch(string user1, string user2, string description)
    {
        // Arrange
        Debug.WriteLine($"Testing session isolation: {description}");
        Debug.WriteLine($"User1: {user1}, User2: {user2}");

        var context1 = SessionContext.ForUser(user1);
        var context2 = SessionContext.ForUser(user2);

        // Act
        var matches = context1.Matches(context2);
        Debug.WriteLine($"Contexts match: {matches}");

        // Assert
        Assert.False(matches);
        Debug.WriteLine("✅ Session isolation test passed");
    }

    [Theory]
    [MemberData(nameof(GetHierarchicalMatchingTestData))]
    public void HierarchicalMatching_WithDifferentLevels_MatchesCorrectly(
        SessionContext broader,
        SessionContext narrower,
        bool shouldMatch,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing hierarchical matching: {description}");
        Debug.WriteLine($"Broader context: {broader}");
        Debug.WriteLine($"Narrower context: {narrower}");
        Debug.WriteLine($"Should match: {shouldMatch}");

        // Act
        var matches = broader.Matches(narrower);
        Debug.WriteLine($"Actual match: {matches}");

        // Assert
        Assert.Equal(shouldMatch, matches);
        Debug.WriteLine("✅ Hierarchical matching test passed");
    }

    public static IEnumerable<object[]> GetFactoryMethodTestData()
    {
        yield return new object[] { "ForUser", "user1", null!, null!, "ForUser factory method" };
        yield return new object[] { "ForAgent", "user1", "agent1", null!, "ForAgent factory method" };
        yield return new object[] { "ForRun", "user1", "agent1", "run1", "ForRun factory method" };
        yield return new object[]
        {
            "ForUser",
            "user-with-special-chars!@#",
            null!,
            null!,
            "ForUser with special characters",
        };
        yield return new object[] { "ForAgent", "user1", "agent-with-dashes", null!, "ForAgent with dashes" };
        yield return new object[] { "ForRun", "user1", "agent1", "run-123-abc", "ForRun with complex run ID" };
    }

    public static IEnumerable<object[]> GetSessionScopeTestData()
    {
        yield return new object[]
        {
            SessionContext.ForUser("user1"),
            SessionScope.User,
            "User-only context should have User scope",
        };
        yield return new object[]
        {
            SessionContext.ForAgent("user1", "agent1"),
            SessionScope.Agent,
            "User-agent context should have Agent scope",
        };
        yield return new object[]
        {
            SessionContext.ForRun("user1", "agent1", "run1"),
            SessionScope.Run,
            "Full context should have Run scope",
        };

        // Edge case: user-run without agent (unusual but possible)
        var userRunContext = new SessionContext { UserId = "user1", RunId = "run1" };
        yield return new object[]
        {
            userRunContext,
            SessionScope.Run,
            "User-run context (no agent) should have Run scope",
        };
    }

    public static IEnumerable<object[]> GetToStringTestData()
    {
        yield return new object[] { SessionContext.ForUser("user1"), "user1", "User-only context toString" };
        yield return new object[]
        {
            SessionContext.ForAgent("user1", "agent1"),
            "user1/agent1",
            "User-agent context toString",
        };
        yield return new object[]
        {
            SessionContext.ForRun("user1", "agent1", "run1"),
            "user1/agent1/run1",
            "Full context toString",
        };

        var userRunContext = new SessionContext { UserId = "user1", RunId = "run1" };
        yield return new object[] { userRunContext, "user1//run1", "User-run context (no agent) toString" };
    }

    public static IEnumerable<object[]> GetSessionIsolationTestData()
    {
        yield return new object[] { "user1", "user2", "Different users should not match" };
        yield return new object[] { "alice", "bob", "Different user names should not match" };
        yield return new object[] { "user-1", "user-2", "Similar user names should not match" };
        yield return new object[] { "admin", "user", "Admin and user should not match" };
        yield return new object[] { "", "user1", "Empty user ID should not match non-empty" };
    }

    public static IEnumerable<object[]> GetHierarchicalMatchingTestData()
    {
        // The actual implementation uses strict matching, not hierarchical matching
        // User level should NOT match agent level (strict matching)
        yield return new object[]
        {
            SessionContext.ForUser("user1"),
            SessionContext.ForAgent("user1", "agent1"),
            false,
            "User context should not match user-agent context (strict matching)",
        };

        // User level should NOT match run level (strict matching)
        yield return new object[]
        {
            SessionContext.ForUser("user1"),
            SessionContext.ForRun("user1", "agent1", "run1"),
            false,
            "User context should not match full context (strict matching)",
        };

        // Agent level should NOT match run level (strict matching)
        yield return new object[]
        {
            SessionContext.ForAgent("user1", "agent1"),
            SessionContext.ForRun("user1", "agent1", "run1"),
            false,
            "User-agent context should not match full context (strict matching)",
        };

        // Different agents should not match
        yield return new object[]
        {
            SessionContext.ForAgent("user1", "agent1"),
            SessionContext.ForAgent("user1", "agent2"),
            false,
            "Different agents should not match",
        };

        // Different runs should not match
        yield return new object[]
        {
            SessionContext.ForRun("user1", "agent1", "run1"),
            SessionContext.ForRun("user1", "agent1", "run2"),
            false,
            "Different runs should not match",
        };

        // Same contexts should match
        yield return new object[]
        {
            SessionContext.ForAgent("user1", "agent1"),
            SessionContext.ForAgent("user1", "agent1"),
            true,
            "Same agent contexts should match",
        };
    }
}
