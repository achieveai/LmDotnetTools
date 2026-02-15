using System.Text.Json;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tests.Mocks;
using MemoryServer.Tests.TestUtilities;
using MemoryServer.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace MemoryServer.Tests.Tools;

public class MemoryMcpToolsTests
{
    private static readonly float[] value = [0.1f, 0.2f, 0.3f];
    private readonly MemoryMcpTools _mcpTools;
    private readonly MemoryService _memoryService;
    private readonly Mock<IEmbeddingManager> _mockEmbeddingManager;
    private readonly Mock<ILogger<MemoryMcpTools>> _mockLogger;
    private readonly MockMemoryRepository _mockRepository;
    private readonly Mock<ISessionContextResolver> _mockSessionResolver;

    public MemoryMcpToolsTests()
    {
        _mockRepository = new MockMemoryRepository();
        _mockSessionResolver = new Mock<ISessionContextResolver>();
        _mockLogger = new Mock<ILogger<MemoryMcpTools>>();
        _mockEmbeddingManager = new Mock<IEmbeddingManager>();

        // Setup memory service with mocked dependencies
        var memoryOptions = new MemoryServerOptions
        {
            Memory = new MemoryOptions { MaxMemoryLength = 10000, DefaultSearchLimit = 10 },
            Embedding = new EmbeddingOptions
            {
                EnableVectorStorage = false, // Disable for unit tests by default
                AutoGenerateEmbeddings = false,
            },
        };

        var optionsMock = new Mock<IOptions<MemoryServerOptions>>();
        _ = optionsMock.Setup(x => x.Value).Returns(memoryOptions);

        var mockGraphMemoryService = new Mock<IGraphMemoryService>();
        var memoryServiceLogger = new Mock<ILogger<MemoryService>>();

        // Setup mock embedding manager
        _ = _mockEmbeddingManager
            .Setup(x => x.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(value); // Mock embedding
        _ = _mockEmbeddingManager.Setup(x => x.ModelName).Returns("mock-model");

        _memoryService = new MemoryService(
            _mockRepository,
            mockGraphMemoryService.Object,
            _mockEmbeddingManager.Object,
            memoryServiceLogger.Object,
            optionsMock.Object
        );

        // Setup session resolver to return predictable session contexts
        // When userId is null (from JWT), return "test_user"
        // When agentId is null (from JWT), return "test_agent"
        _ = _mockSessionResolver
            .Setup(x =>
                x.ResolveSessionContextAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<string?, string?, string?, CancellationToken>(
                (userId, agentId, runId, ct) =>
                    Task.FromResult(
                        new SessionContext
                        {
                            UserId = userId ?? "test_user", // Default user from JWT
                            AgentId = agentId ?? "test_agent", // Default agent from JWT when not explicitly provided
                            RunId = runId,
                        }
                    )
            );

        _mcpTools = new MemoryMcpTools(_memoryService, _mockSessionResolver.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task AddMemoryAsync_WithValidContent_ReturnsSuccess()
    {
        Debug.WriteLine("Testing MCP memory_add tool");

        // Arrange
        _mockRepository.Reset();

        // Act
        var result = await _mcpTools.AddMemoryAsync("Test memory content for MCP", "test_run");

        Debug.WriteLine($"Result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);

        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.True(resultObj.GetProperty("memory").GetProperty("id").GetInt32() > 0);
        Assert.Equal("Test memory content for MCP", resultObj.GetProperty("memory").GetProperty("content").GetString());

        // Verify the session resolver was called correctly (userId and agentId come from JWT)
        _mockSessionResolver.Verify(
            x =>
                x.ResolveSessionContextAsync(
                    null, // userId from JWT
                    null, // agentId from JWT
                    "test_run",
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        Debug.WriteLine("✅ MCP memory_add tool test passed");
    }

    [Fact]
    public async Task SearchMemoriesAsync_WithValidQuery_ReturnsResults()
    {
        Debug.WriteLine("Testing MCP memory_search tool");

        // Arrange
        _mockRepository.Reset();

        // Add a test memory to the mock repository using the same user that will be returned by the session resolver
        var testMemory = MemoryTestDataFactory.CreateTestMemory(
            1,
            "Searchable test content for MCP search",
            "test_user",
            "search_agent",
            "search_run"
        );
        testMemory.Score = 0.95f; // Set a high relevance score
        _mockRepository.AddTestMemory(testMemory);

        // Act
        var result = await _mcpTools.SearchMemoriesAsync(
            "searchable test",
            "search_agent", // Optional - can be provided to filter by agent
            "search_run",
            10
        );

        Debug.WriteLine($"Search result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);

        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.Equal("searchable test", resultObj.GetProperty("query").GetString());
        Assert.True(resultObj.GetProperty("total_results").GetInt32() > 0);

        var results = resultObj.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(results);
        Assert.Contains("Searchable test content for MCP search", results.First().GetProperty("content").GetString());

        Debug.WriteLine("✅ MCP memory_search tool test passed");
    }

    [Fact]
    public async Task GetAllMemoriesAsync_WithValidSession_ReturnsMemories()
    {
        Debug.WriteLine("Testing MCP memory_get_all tool");

        // Arrange
        _mockRepository.Reset();

        // Add test memories to the mock repository using the same user that will be returned by the session resolver
        var memory1 = MemoryTestDataFactory.CreateTestMemory(
            1,
            "First memory for get_all test",
            "test_user",
            "getall_agent",
            "getall_run"
        );
        var memory2 = MemoryTestDataFactory.CreateTestMemory(
            2,
            "Second memory for get_all test",
            "test_user",
            "getall_agent",
            "getall_run"
        );
        _mockRepository.AddTestMemory(memory1);
        _mockRepository.AddTestMemory(memory2);

        // Act
        var result = await _mcpTools.GetAllMemoriesAsync(
            "getall_agent", // Optional - can be provided to filter by agent
            "getall_run",
            100
        );

        Debug.WriteLine($"Get all result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);

        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.True(resultObj.GetProperty("total_count").GetInt32() >= 2);

        var memories = resultObj.GetProperty("memories").EnumerateArray().ToList();
        Assert.True(memories.Count >= 2);

        Debug.WriteLine("✅ MCP memory_get_all tool test passed");
    }

    [Fact]
    public async Task UpdateMemoryAsync_WithValidId_UpdatesMemory()
    {
        Debug.WriteLine("Testing MCP memory_update tool");

        // Arrange
        _mockRepository.Reset();

        // Add a test memory first using the same user that will be returned by the session resolver
        var originalMemory = MemoryTestDataFactory.CreateTestMemory(
            1,
            "Original content for update test",
            "test_user",
            "test_agent",
            "update_run"
        );
        _mockRepository.AddTestMemory(originalMemory);

        // Act
        var result = await _mcpTools.UpdateMemoryAsync(1, "Updated content for update test", "update_run"); // userId and agentId come from JWT

        Debug.WriteLine($"Update result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);

        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.Equal(1, resultObj.GetProperty("memory").GetProperty("id").GetInt32());
        Assert.Equal(
            "Updated content for update test",
            resultObj.GetProperty("memory").GetProperty("content").GetString()
        );

        Debug.WriteLine("✅ MCP memory_update tool test passed");
    }

    [Fact]
    public async Task DeleteMemoryAsync_WithValidId_DeletesMemory()
    {
        Debug.WriteLine("Testing MCP memory_delete tool");

        // Arrange
        _mockRepository.Reset();

        // Add a test memory first using the same user that will be returned by the session resolver
        var testMemory = MemoryTestDataFactory.CreateTestMemory(
            1,
            "Content to be deleted",
            "test_user",
            "test_agent",
            "delete_run"
        );
        _mockRepository.AddTestMemory(testMemory);

        // Act
        var result = await _mcpTools.DeleteMemoryAsync(1, "delete_run"); // userId and agentId come from JWT

        Debug.WriteLine($"Delete result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);

        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.Contains("Memory 1 deleted successfully", resultObj.GetProperty("message").GetString());

        Debug.WriteLine("✅ MCP memory_delete tool test passed");
    }

    [Fact]
    public async Task GetMemoryStatsAsync_WithValidSession_ReturnsStats()
    {
        Debug.WriteLine("Testing MCP memory_get_stats tool");

        // Arrange
        _mockRepository.Reset();

        // Add test memories using the same user that will be returned by the session resolver
        var memory1 = MemoryTestDataFactory.CreateTestMemory(
            1,
            "Stats test memory 1",
            "test_user",
            "stats_agent",
            "stats_run"
        );
        var memory2 = MemoryTestDataFactory.CreateTestMemory(
            2,
            "Stats test memory 2",
            "test_user",
            "stats_agent",
            "stats_run"
        );
        _mockRepository.AddTestMemory(memory1);
        _mockRepository.AddTestMemory(memory2);

        // Act
        var result = await _mcpTools.GetMemoryStatsAsync(
            "stats_agent", // Optional - can be provided to filter by agent
            "stats_run"
        );

        Debug.WriteLine($"Stats result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);

        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.True(resultObj.GetProperty("stats").GetProperty("total_memories").GetInt32() >= 2);

        Debug.WriteLine("✅ MCP memory_get_stats tool test passed");
    }

    [Fact]
    public async Task AddMemoryAsync_WithoutConnectionId_AutoGeneratesConnectionId()
    {
        Debug.WriteLine("Testing MCP memory_add tool without explicit connection ID");

        // Arrange
        _mockRepository.Reset();

        // Act
        var result = await _mcpTools.AddMemoryAsync("Test memory without connection ID"); // No userId, agentId - they come from JWT

        Debug.WriteLine($"Result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);

        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.True(resultObj.GetProperty("memory").GetProperty("id").GetInt32() > 0);
        Assert.Equal(
            "Test memory without connection ID",
            resultObj.GetProperty("memory").GetProperty("content").GetString()
        );

        Debug.WriteLine("✅ MCP memory_add tool test without connection ID passed");
    }
}
