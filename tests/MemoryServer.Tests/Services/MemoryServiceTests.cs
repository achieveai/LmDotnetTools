using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tests.Mocks;
using MemoryServer.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace MemoryServer.Tests.Services;

/// <summary>
/// Unit tests for the MemoryService class.
/// Tests business logic with mocked dependencies.
/// </summary>
public class MemoryServiceTests
{
    private readonly MockMemoryRepository _mockRepository;
    private readonly Mock<ILogger<MemoryService>> _mockLogger;
    private readonly Mock<IEmbeddingManager> _mockEmbeddingManager;
    private readonly MemoryService _memoryService;
    private readonly MemoryServerOptions _options;

    public MemoryServiceTests()
    {
        _mockRepository = new MockMemoryRepository();
        _mockLogger = new Mock<ILogger<MemoryService>>();
        _mockEmbeddingManager = new Mock<IEmbeddingManager>();

        _options = new MemoryServerOptions
        {
            Memory = new MemoryOptions { MaxMemoryLength = 10000, DefaultSearchLimit = 10 },
            Embedding = new EmbeddingOptions
            {
                EnableVectorStorage = false, // Disable for unit tests by default
                AutoGenerateEmbeddings = false,
            },
        };

        var optionsMock = new Mock<IOptions<MemoryServerOptions>>();
        optionsMock.Setup(x => x.Value).Returns(_options);

        var mockGraphMemoryService = new Mock<IGraphMemoryService>();

        // Setup mock embedding manager
        _mockEmbeddingManager
            .Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f, 0.3f }); // Mock embedding
        _mockEmbeddingManager.Setup(x => x.ModelName).Returns("mock-model");

        _memoryService = new MemoryService(
            _mockRepository,
            mockGraphMemoryService.Object,
            _mockEmbeddingManager.Object,
            _mockLogger.Object,
            optionsMock.Object
        );
    }

    [Theory]
    [MemberData(nameof(MemoryTestDataFactory.GetMemoryContentTestCases), MemberType = typeof(MemoryTestDataFactory))]
    public async Task AddMemoryAsync_WithVariousContent_ValidatesCorrectly(
        string content,
        bool shouldSucceed,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing AddMemoryAsync: {description}");
        Debug.WriteLine($"Content length: {content.Length}, Should succeed: {shouldSucceed}");

        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await _memoryService.AddMemoryAsync(content, sessionContext);

            Debug.WriteLine($"Successfully added memory with ID: {result.Id}");
            Assert.NotNull(result);
            Assert.Equal(content.Trim(), result.Content);
            Assert.Equal(sessionContext.UserId, result.UserId);
            Assert.Contains(nameof(_mockRepository.AddAsync), _mockRepository.MethodCalls);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                _memoryService.AddMemoryAsync(content, sessionContext)
            );

            Debug.WriteLine($"Expected exception thrown: {exception.Message}");
            Assert.DoesNotContain(nameof(_mockRepository.AddAsync), _mockRepository.MethodCalls);
        }

        Debug.WriteLine("✅ AddMemoryAsync validation test passed");
    }

    [Theory]
    [MemberData(nameof(MemoryTestDataFactory.GetMetadataTestCases), MemberType = typeof(MemoryTestDataFactory))]
    public async Task AddMemoryAsync_WithVariousMetadata_PreservesMetadataCorrectly(
        Dictionary<string, object>? metadata,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing AddMemoryAsync with metadata: {description}");
        Debug.WriteLine($"Metadata: {(metadata == null ? "null" : $"{metadata.Count} entries")}");

        var content = "Test memory content";
        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        // Act
        var result = await _memoryService.AddMemoryAsync(content, sessionContext, metadata);
        Debug.WriteLine($"Added memory with ID: {result.Id}");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(metadata, result.Metadata);
        Assert.Equal(metadata, _mockRepository.LastCallParameters["metadata"]);

        Debug.WriteLine("✅ AddMemoryAsync metadata test passed");
    }

    [Theory]
    [MemberData(nameof(MemoryTestDataFactory.GetSearchQueryTestCases), MemberType = typeof(MemoryTestDataFactory))]
    public async Task SearchMemoriesAsync_WithVariousQueries_HandlesCorrectly(
        string query,
        int limit,
        float scoreThreshold,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing SearchMemoriesAsync: {description}");
        Debug.WriteLine($"Query: '{query}', Limit: {limit}, Threshold: {scoreThreshold}");

        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        // Add some test memories
        var testMemories = MemoryTestDataFactory.CreateTestMemories(5, sessionContext);
        foreach (var memory in testMemories)
        {
            _mockRepository.AddTestMemory(memory);
        }

        // Act
        var results = await _memoryService.SearchMemoriesAsync(query, sessionContext, limit, scoreThreshold);
        Debug.WriteLine($"Search returned {results.Count} results");

        // Assert
        Assert.NotNull(results);

        if (string.IsNullOrWhiteSpace(query))
        {
            Assert.Empty(results);
            Debug.WriteLine("Empty query correctly returned no results");
        }
        else
        {
            Assert.Contains(nameof(_mockRepository.SearchAsync), _mockRepository.MethodCalls);

            // Verify limit is applied correctly (respecting service limits)
            var expectedLimit = Math.Min(limit, _options.Memory.DefaultSearchLimit * 2);
            Assert.Equal(expectedLimit, _mockRepository.LastCallParameters["limit"]);
            Debug.WriteLine($"Limit correctly applied: {expectedLimit}");
        }

        Debug.WriteLine("✅ SearchMemoriesAsync test passed");
    }

    [Theory]
    [MemberData(nameof(GetSessionContextTestData))]
    public async Task GetAllMemoriesAsync_WithDifferentSessions_RespectsSessionIsolation(
        SessionContext sessionContext,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing GetAllMemoriesAsync session isolation: {description}");
        Debug.WriteLine($"Session context: {sessionContext}");

        _mockRepository.Reset();

        // Add memories for different sessions
        var user1Context = SessionContext.ForUser("user1");
        var user2Context = SessionContext.ForUser("user2");

        _mockRepository.AddTestMemory(MemoryTestDataFactory.CreateTestMemory(1, "User1 memory", "user1"));
        _mockRepository.AddTestMemory(MemoryTestDataFactory.CreateTestMemory(2, "User2 memory", "user2"));

        // Act
        var results = await _memoryService.GetAllMemoriesAsync(sessionContext, 100, 0);
        Debug.WriteLine($"Retrieved {results.Count} memories for session");

        // Assert
        Assert.NotNull(results);
        Assert.Contains(nameof(_mockRepository.GetAllAsync), _mockRepository.MethodCalls);

        // All returned memories should match the session context
        foreach (var memory in results)
        {
            Assert.True(memory.GetSessionContext().Matches(sessionContext));
            Debug.WriteLine($"Memory {memory.Id} correctly matches session context");
        }

        Debug.WriteLine("✅ Session isolation test passed");
    }

    [Theory]
    [MemberData(nameof(GetUpdateTestData))]
    public async Task UpdateMemoryAsync_WithVariousScenarios_HandlesCorrectly(
        string newContent,
        bool shouldSucceed,
        string description
    )
    {
        // Arrange
        Debug.WriteLine($"Testing UpdateMemoryAsync: {description}");
        Debug.WriteLine($"New content length: {newContent.Length}, Should succeed: {shouldSucceed}");

        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        // Add a test memory
        var originalMemory = MemoryTestDataFactory.CreateTestMemory(1, "Original content", "test-user");
        _mockRepository.AddTestMemory(originalMemory);

        // Act & Assert
        if (shouldSucceed)
        {
            var result = await _memoryService.UpdateMemoryAsync(1, newContent, sessionContext);

            Debug.WriteLine($"Successfully updated memory: {result?.Content}");
            Assert.NotNull(result);
            Assert.Equal(newContent.Trim(), result.Content);
            Assert.Contains(nameof(_mockRepository.UpdateAsync), _mockRepository.MethodCalls);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                _memoryService.UpdateMemoryAsync(1, newContent, sessionContext)
            );

            Debug.WriteLine($"Expected exception thrown: {exception.Message}");
            Assert.DoesNotContain(nameof(_mockRepository.UpdateAsync), _mockRepository.MethodCalls);
        }

        Debug.WriteLine("✅ UpdateMemoryAsync test passed");
    }

    [Fact]
    public async Task DeleteMemoryAsync_WithValidId_DeletesSuccessfully()
    {
        // Arrange
        Debug.WriteLine("Testing DeleteMemoryAsync with valid ID");
        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        var testMemory = MemoryTestDataFactory.CreateTestMemory(1, "Test content", "test-user");
        _mockRepository.AddTestMemory(testMemory);
        Debug.WriteLine($"Added test memory with ID: {testMemory.Id}");

        // Act
        var result = await _memoryService.DeleteMemoryAsync(1, sessionContext);
        Debug.WriteLine($"Delete result: {result}");

        // Assert
        Assert.True(result);
        Assert.Contains(nameof(_mockRepository.DeleteAsync), _mockRepository.MethodCalls);
        Assert.Equal(1, _mockRepository.LastCallParameters["id"]);
        Assert.Equal(sessionContext, _mockRepository.LastCallParameters["sessionContext"]);

        Debug.WriteLine("✅ DeleteMemoryAsync test passed");
    }

    [Fact]
    public async Task DeleteAllMemoriesAsync_WithValidSession_DeletesAllSessionMemories()
    {
        // Arrange
        Debug.WriteLine("Testing DeleteAllMemoriesAsync");
        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        // Add multiple test memories
        for (int i = 1; i <= 3; i++)
        {
            var memory = MemoryTestDataFactory.CreateTestMemory(i, $"Test content {i}", "test-user");
            _mockRepository.AddTestMemory(memory);
        }
        Debug.WriteLine("Added 3 test memories");

        // Act
        var deletedCount = await _memoryService.DeleteAllMemoriesAsync(sessionContext);
        Debug.WriteLine($"Deleted {deletedCount} memories");

        // Assert
        Assert.Equal(3, deletedCount);
        Assert.Contains(nameof(_mockRepository.DeleteAllAsync), _mockRepository.MethodCalls);
        Assert.Equal(sessionContext, _mockRepository.LastCallParameters["sessionContext"]);

        Debug.WriteLine("✅ DeleteAllMemoriesAsync test passed");
    }

    [Fact]
    public async Task GetMemoryStatsAsync_WithMemories_ReturnsCorrectStats()
    {
        // Arrange
        Debug.WriteLine("Testing GetMemoryStatsAsync");
        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        // Add test memories with known content lengths
        var memories = new[]
        {
            MemoryTestDataFactory.CreateTestMemory(1, "Short", "test-user"), // 5 chars
            MemoryTestDataFactory.CreateTestMemory(2, "Medium length content", "test-user"), // 21 chars
            MemoryTestDataFactory.CreateTestMemory(3, "This is a longer piece of content for testing", "test-user"), // 45 chars
        };

        foreach (var memory in memories)
        {
            _mockRepository.AddTestMemory(memory);
        }
        Debug.WriteLine($"Added {memories.Length} test memories");

        // Act
        var stats = await _memoryService.GetMemoryStatsAsync(sessionContext);
        Debug.WriteLine(
            $"Stats - Total: {stats.TotalMemories}, Size: {stats.TotalContentSize}, Avg: {stats.AverageContentLength}"
        );

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(3, stats.TotalMemories);
        Assert.Equal(71, stats.TotalContentSize); // 5 + 21 + 45
        Assert.Equal(71.0 / 3.0, stats.AverageContentLength); // 71 / 3
        Assert.Contains(nameof(_mockRepository.GetStatsAsync), _mockRepository.MethodCalls);

        Debug.WriteLine("✅ GetMemoryStatsAsync test passed");
    }

    [Fact]
    public async Task GetMemoryHistoryAsync_WithValidId_ReturnsHistory()
    {
        // Arrange
        Debug.WriteLine("Testing GetMemoryHistoryAsync");
        var sessionContext = SessionContext.ForUser("test-user");
        _mockRepository.Reset();

        var testMemory = MemoryTestDataFactory.CreateTestMemory(1, "Test content", "test-user");
        _mockRepository.AddTestMemory(testMemory);

        // Act
        var history = await _memoryService.GetMemoryHistoryAsync(1, sessionContext);
        Debug.WriteLine($"Retrieved {history.Count} history entries");

        // Assert
        Assert.NotNull(history);
        Assert.Contains(nameof(_mockRepository.GetHistoryAsync), _mockRepository.MethodCalls);
        Assert.Equal(1, _mockRepository.LastCallParameters["id"]);
        Assert.Equal(sessionContext, _mockRepository.LastCallParameters["sessionContext"]);

        Debug.WriteLine("✅ GetMemoryHistoryAsync test passed");
    }

    public static IEnumerable<object[]> GetSessionContextTestData()
    {
        yield return new object[] { SessionContext.ForUser("user1"), "User-only session" };
        yield return new object[] { SessionContext.ForAgent("user1", "agent1"), "User-agent session" };
        yield return new object[] { SessionContext.ForRun("user1", "agent1", "run1"), "Full session context" };
    }

    public static IEnumerable<object[]> GetUpdateTestData()
    {
        yield return new object[] { "Valid updated content", true, "Valid content update" };
        yield return new object[] { new string('A', 1000), true, "Long but valid content update" };
        yield return new object[] { new string('A', 10000), true, "Maximum length content update" };
        yield return new object[] { "", false, "Empty content should fail" };
        yield return new object[] { "   ", false, "Whitespace-only content should fail" };
        yield return new object[] { new string('A', 10001), false, "Over-length content should fail" };
    }
}
