using MemoryServer.Models;
using MemoryServer.Tests.TestUtilities;

namespace MemoryServer.Tests.Models;

/// <summary>
/// Unit tests for the Memory model.
/// Tests core business logic methods without external dependencies.
/// </summary>
public class MemoryTests
{
    [Theory]
    [MemberData(nameof(GetSessionContextTestData))]
    public void GetSessionContext_WithVariousSessionData_ReturnsCorrectSessionContext(
        string userId, string? agentId, string? runId, string description)
    {
        // Arrange
        Debug.WriteLine($"Testing GetSessionContext: {description}");
        Debug.WriteLine($"Input - UserId: {userId}, AgentId: {agentId}, RunId: {runId}");
        
        var memory = MemoryTestDataFactory.CreateTestMemory(
            userId: userId,
            agentId: agentId,
            runId: runId
        );

        // Act
        var sessionContext = memory.GetSessionContext();
        Debug.WriteLine($"Result - SessionContext: {sessionContext}");

        // Assert
        Assert.Equal(userId, sessionContext.UserId);
        Assert.Equal(agentId, sessionContext.AgentId);
        Assert.Equal(runId, sessionContext.RunId);
        
        Debug.WriteLine("✅ GetSessionContext test passed");
    }

    [Theory]
    [MemberData(nameof(GetScoreTestData))]
    public void WithScore_WithVariousScores_ReturnsMemoryWithCorrectScore(
        float score, string description)
    {
        // Arrange
        Debug.WriteLine($"Testing WithScore: {description}");
        Debug.WriteLine($"Input score: {score}");
        
        var originalMemory = MemoryTestDataFactory.CreateTestMemory(id: 1, content: "Test content");
        Debug.WriteLine($"Original memory ID: {originalMemory.Id}, Score: {originalMemory.Score}");

        // Act
        var memoryWithScore = originalMemory.WithScore(score);
        Debug.WriteLine($"Result memory ID: {memoryWithScore.Id}, Score: {memoryWithScore.Score}");

        // Assert
        Assert.Equal(score, memoryWithScore.Score);
        Assert.Equal(originalMemory.Id, memoryWithScore.Id);
        Assert.Equal(originalMemory.Content, memoryWithScore.Content);
        Assert.Equal(originalMemory.UserId, memoryWithScore.UserId);
        Assert.Equal(originalMemory.AgentId, memoryWithScore.AgentId);
        Assert.Equal(originalMemory.RunId, memoryWithScore.RunId);
        Assert.Equal(originalMemory.CreatedAt, memoryWithScore.CreatedAt);
        Assert.Equal(originalMemory.UpdatedAt, memoryWithScore.UpdatedAt);
        Assert.Equal(originalMemory.Version, memoryWithScore.Version);
        
        // Original memory should be unchanged
        Assert.Null(originalMemory.Score);
        
        Debug.WriteLine("✅ WithScore test passed");
    }

    [Fact]
    public void WithUpdatedTimestamp_Always_ReturnsMemoryWithUpdatedTimestampAndVersion()
    {
        // Arrange
        Debug.WriteLine("Testing WithUpdatedTimestamp");
        var originalMemory = MemoryTestDataFactory.CreateTestMemory(id: 1, content: "Test content");
        var originalUpdatedAt = originalMemory.UpdatedAt;
        var originalVersion = originalMemory.Version;
        
        Debug.WriteLine($"Original - UpdatedAt: {originalUpdatedAt}, Version: {originalVersion}");
        
        // Small delay to ensure timestamp difference
        Thread.Sleep(10);

        // Act
        var updatedMemory = originalMemory.WithUpdatedTimestamp();
        Debug.WriteLine($"Updated - UpdatedAt: {updatedMemory.UpdatedAt}, Version: {updatedMemory.Version}");

        // Assert
        Assert.True(updatedMemory.UpdatedAt > originalUpdatedAt);
        Assert.Equal(originalVersion + 1, updatedMemory.Version);
        
        // Other properties should be copied correctly
        Assert.Equal(originalMemory.Id, updatedMemory.Id);
        Assert.Equal(originalMemory.Content, updatedMemory.Content);
        Assert.Equal(originalMemory.UserId, updatedMemory.UserId);
        Assert.Equal(originalMemory.AgentId, updatedMemory.AgentId);
        Assert.Equal(originalMemory.RunId, updatedMemory.RunId);
        Assert.Equal(originalMemory.CreatedAt, updatedMemory.CreatedAt);
        
        // Metadata should be deep copied
        if (originalMemory.Metadata != null)
        {
            Assert.NotSame(originalMemory.Metadata, updatedMemory.Metadata);
            Assert.Equal(originalMemory.Metadata.Count, updatedMemory.Metadata?.Count);
        }
        
        // Original memory should be unchanged
        Assert.Equal(originalUpdatedAt, originalMemory.UpdatedAt);
        Assert.Equal(originalVersion, originalMemory.Version);
        
        Debug.WriteLine("✅ WithUpdatedTimestamp test passed");
    }

    [Theory]
    [MemberData(nameof(MemoryTestDataFactory.GetMetadataTestCases), MemberType = typeof(MemoryTestDataFactory))]
    public void WithUpdatedTimestamp_WithVariousMetadata_PreservesMetadataCorrectly(
        Dictionary<string, object>? metadata, string description)
    {
        // Arrange
        Debug.WriteLine($"Testing WithUpdatedTimestamp with metadata: {description}");
        Debug.WriteLine($"Metadata: {(metadata == null ? "null" : $"{metadata.Count} entries")}");
        
        var originalMemory = MemoryTestDataFactory.CreateTestMemory(metadata: metadata);

        // Act
        var updatedMemory = originalMemory.WithUpdatedTimestamp();

        // Assert
        if (metadata == null)
        {
            Assert.Null(updatedMemory.Metadata);
            Debug.WriteLine("Metadata correctly preserved as null");
        }
        else
        {
            Assert.NotNull(updatedMemory.Metadata);
            Assert.NotSame(originalMemory.Metadata, updatedMemory.Metadata);
            Assert.Equal(metadata.Count, updatedMemory.Metadata.Count);
            
            foreach (var kvp in metadata)
            {
                Assert.True(updatedMemory.Metadata.ContainsKey(kvp.Key));
                Assert.Equal(kvp.Value, updatedMemory.Metadata[kvp.Key]);
            }
            Debug.WriteLine($"Metadata correctly deep copied with {metadata.Count} entries");
        }
        
        Debug.WriteLine("✅ WithUpdatedTimestamp metadata test passed");
    }

    [Theory]
    [MemberData(nameof(GetEmbeddingTestData))]
    public void WithScore_WithEmbedding_PreservesEmbeddingCorrectly(
        float[]? embedding, string description)
    {
        // Arrange
        Debug.WriteLine($"Testing WithScore with embedding: {description}");
        Debug.WriteLine($"Embedding: {(embedding == null ? "null" : $"array of {embedding.Length} elements")}");
        
        var originalMemory = MemoryTestDataFactory.CreateTestMemory();
        originalMemory.Embedding = embedding;

        // Act
        var memoryWithScore = originalMemory.WithScore(0.85f);

        // Assert
        if (embedding == null)
        {
            Assert.Null(memoryWithScore.Embedding);
            Debug.WriteLine("Embedding correctly preserved as null");
        }
        else
        {
            Assert.NotNull(memoryWithScore.Embedding);
            Assert.Equal(embedding.Length, memoryWithScore.Embedding.Length);
            Assert.Equal(embedding, memoryWithScore.Embedding);
            Debug.WriteLine($"Embedding correctly preserved with {embedding.Length} elements");
        }
        
        Debug.WriteLine("✅ WithScore embedding test passed");
    }

    public static IEnumerable<object?[]> GetSessionContextTestData()
    {
        return MemoryTestDataFactory.GetSessionContextTestCases();
    }

    public static IEnumerable<object[]> GetScoreTestData()
    {
        yield return new object[] { 0.0f, "Zero score" };
        yield return new object[] { 0.5f, "Medium score" };
        yield return new object[] { 1.0f, "Perfect score" };
        yield return new object[] { 0.123456f, "Precise decimal score" };
        yield return new object[] { -0.1f, "Negative score" };
        yield return new object[] { 1.5f, "Score above 1.0" };
    }

    public static IEnumerable<object?[]> GetEmbeddingTestData()
    {
        yield return new object?[] { null, "Null embedding" };
        yield return new object?[] { new float[0], "Empty embedding array" };
        yield return new object?[] { new float[] { 0.1f }, "Single element embedding" };
        yield return new object?[] { new float[] { 0.1f, 0.2f, 0.3f }, "Small embedding" };
        yield return new object?[] { 
            Enumerable.Range(0, 100).Select(i => (float)i / 100).ToArray(), 
            "Large embedding (100 dimensions)" 
        };
    }
} 