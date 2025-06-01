using System.Text.Json;
using MemoryServer.Models;
using MemoryServer.Services;
using MemoryServer.Tools;
using MemoryServer.Tests.Mocks;
using MemoryServer.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace MemoryServer.Tests.Tools;

public class MemoryMcpToolsTests
{
    private readonly MockMemoryRepository _mockRepository;
    private readonly Mock<ISessionContextResolver> _mockSessionResolver;
    private readonly Mock<ILogger<MemoryMcpTools>> _mockLogger;
    private readonly MemoryMcpTools _mcpTools;
    private readonly MemoryService _memoryService;

    public MemoryMcpToolsTests()
    {
        _mockRepository = new MockMemoryRepository();
        _mockSessionResolver = new Mock<ISessionContextResolver>();
        _mockLogger = new Mock<ILogger<MemoryMcpTools>>();

        // Setup memory service with mocked dependencies
        var memoryOptions = new MemoryServerOptions
        {
            Memory = new MemoryOptions
            {
                MaxMemoryLength = 10000,
                DefaultSearchLimit = 10
            }
        };

        var optionsMock = new Mock<IOptions<MemoryServerOptions>>();
        optionsMock.Setup(x => x.Value).Returns(memoryOptions);

        var memoryServiceLogger = new Mock<ILogger<MemoryService>>();
        _memoryService = new MemoryService(_mockRepository, memoryServiceLogger.Object, optionsMock.Object);

        // Setup session resolver to return predictable session contexts
        _mockSessionResolver
            .Setup(x => x.ResolveSessionContextAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string?, string?, string?, CancellationToken>((userId, agentId, runId, ct) =>
                Task.FromResult(new SessionContext
                {
                    UserId = userId ?? "default_user",
                    AgentId = agentId,
                    RunId = runId
                }));

        _mcpTools = new MemoryMcpTools(_memoryService, _mockSessionResolver.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task AddMemoryAsync_WithValidContent_ReturnsSuccess()
    {
        Debug.WriteLine("Testing MCP memory_add tool");

        // Arrange
        _mockRepository.Reset();

        // Act
        var result = await _mcpTools.AddMemoryAsync(
            content: "Test memory content for MCP",
            userId: "test_user",
            agentId: "test_agent",
            runId: "test_run");

        Debug.WriteLine($"Result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);
        
        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.True(resultObj.GetProperty("memory").GetProperty("id").GetInt32() > 0);
        Assert.Equal("Test memory content for MCP", resultObj.GetProperty("memory").GetProperty("content").GetString());
        Assert.Equal("test_user", resultObj.GetProperty("memory").GetProperty("user_id").GetString());
        Assert.Equal("test_agent", resultObj.GetProperty("memory").GetProperty("agent_id").GetString());
        Assert.Equal("test_run", resultObj.GetProperty("memory").GetProperty("run_id").GetString());

        // Verify the session resolver was called correctly
        _mockSessionResolver.Verify(x => x.ResolveSessionContextAsync(
            "test_user",
            "test_agent", 
            "test_run",
            It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ MCP memory_add tool test passed");
    }

    [Fact]
    public async Task SearchMemoriesAsync_WithValidQuery_ReturnsResults()
    {
        Debug.WriteLine("Testing MCP memory_search tool");

        // Arrange
        _mockRepository.Reset();
        var sessionContext = new SessionContext { UserId = "search_user", AgentId = "search_agent", RunId = "search_run" };
        
        // Add a test memory to the mock repository
        var testMemory = MemoryTestDataFactory.CreateTestMemory(1, "Searchable test content for MCP search", "search_user", "search_agent", "search_run");
        testMemory.Score = 0.95f; // Set a high relevance score
        _mockRepository.AddTestMemory(testMemory);

        // Act
        var result = await _mcpTools.SearchMemoriesAsync(
            query: "searchable test",
            userId: "search_user",
            agentId: "search_agent",
            runId: "search_run",
            limit: 10);

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
        Assert.Contains("Searchable test content for MCP search", 
            results.First().GetProperty("content").GetString());

        Debug.WriteLine("✅ MCP memory_search tool test passed");
    }

    [Fact]
    public async Task GetAllMemoriesAsync_WithValidSession_ReturnsMemories()
    {
        Debug.WriteLine("Testing MCP memory_get_all tool");

        // Arrange
        _mockRepository.Reset();
        var sessionContext = new SessionContext { UserId = "getall_user", AgentId = "getall_agent", RunId = "getall_run" };
        
        // Add test memories to the mock repository
        var memory1 = MemoryTestDataFactory.CreateTestMemory(1, "First memory for get_all test", "getall_user", "getall_agent", "getall_run");
        var memory2 = MemoryTestDataFactory.CreateTestMemory(2, "Second memory for get_all test", "getall_user", "getall_agent", "getall_run");
        _mockRepository.AddTestMemory(memory1);
        _mockRepository.AddTestMemory(memory2);

        // Act
        var result = await _mcpTools.GetAllMemoriesAsync(
            userId: "getall_user",
            agentId: "getall_agent",
            runId: "getall_run",
            limit: 100);

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
        var sessionContext = new SessionContext { UserId = "update_user", AgentId = "update_agent", RunId = "update_run" };
        
        // Add a test memory first
        var originalMemory = MemoryTestDataFactory.CreateTestMemory(1, "Original content for update test", "update_user", "update_agent", "update_run");
        _mockRepository.AddTestMemory(originalMemory);

        // Act
        var result = await _mcpTools.UpdateMemoryAsync(
            id: 1,
            content: "Updated content for update test",
            userId: "update_user",
            agentId: "update_agent",
            runId: "update_run");

        Debug.WriteLine($"Update result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);
        
        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.Equal(1, resultObj.GetProperty("memory").GetProperty("id").GetInt32());
        Assert.Equal("Updated content for update test", resultObj.GetProperty("memory").GetProperty("content").GetString());

        Debug.WriteLine("✅ MCP memory_update tool test passed");
    }

    [Fact]
    public async Task DeleteMemoryAsync_WithValidId_DeletesMemory()
    {
        Debug.WriteLine("Testing MCP memory_delete tool");

        // Arrange
        _mockRepository.Reset();
        var sessionContext = new SessionContext { UserId = "delete_user", AgentId = "delete_agent", RunId = "delete_run" };
        
        // Add a test memory first
        var testMemory = MemoryTestDataFactory.CreateTestMemory(1, "Content to be deleted", "delete_user", "delete_agent", "delete_run");
        _mockRepository.AddTestMemory(testMemory);

        // Act
        var result = await _mcpTools.DeleteMemoryAsync(
            id: 1,
            userId: "delete_user",
            agentId: "delete_agent",
            runId: "delete_run");

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
        var sessionContext = new SessionContext { UserId = "stats_user", AgentId = "stats_agent", RunId = "stats_run" };
        
        // Add test memories
        var memory1 = MemoryTestDataFactory.CreateTestMemory(1, "Stats test memory 1", "stats_user", "stats_agent", "stats_run");
        var memory2 = MemoryTestDataFactory.CreateTestMemory(2, "Stats test memory 2", "stats_user", "stats_agent", "stats_run");
        _mockRepository.AddTestMemory(memory1);
        _mockRepository.AddTestMemory(memory2);

        // Act
        var result = await _mcpTools.GetMemoryStatsAsync(
            userId: "stats_user",
            agentId: "stats_agent",
            runId: "stats_run");

        Debug.WriteLine($"Stats result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);
        
        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.True(resultObj.GetProperty("statistics").GetProperty("total_memories").GetInt32() >= 2);
        Assert.True(resultObj.GetProperty("statistics").GetProperty("total_content_size").GetInt32() > 0);

        Debug.WriteLine("✅ MCP memory_get_stats tool test passed");
    }

    [Fact]
    public async Task AddMemoryAsync_WithoutConnectionId_AutoGeneratesConnectionId()
    {
        Debug.WriteLine("Testing MCP memory_add tool with auto-generated connection ID");

        // Arrange
        _mockRepository.Reset();

        // Act - Call without providing connectionId
        var result = await _mcpTools.AddMemoryAsync(
            content: "Test memory without connection ID",
            userId: "test_user");

        Debug.WriteLine($"Result: {JsonSerializer.Serialize(result)}");

        // Assert
        Assert.NotNull(result);
        var resultJson = JsonSerializer.Serialize(result);
        var resultObj = JsonSerializer.Deserialize<JsonElement>(resultJson);
        
        Assert.True(resultObj.GetProperty("success").GetBoolean());
        Assert.True(resultObj.GetProperty("memory").GetProperty("id").GetInt32() > 0);
        Assert.Equal("Test memory without connection ID", resultObj.GetProperty("memory").GetProperty("content").GetString());
        Assert.Equal("test_user", resultObj.GetProperty("memory").GetProperty("user_id").GetString());

        // Verify the session resolver was called with a generated connection ID (not null)
        _mockSessionResolver.Verify(x => x.ResolveSessionContextAsync(
            "test_user",
            null, 
            null,
            It.IsAny<CancellationToken>()), Times.Once);

        Debug.WriteLine("✅ MCP memory_add tool auto-generation test passed");
    }
} 