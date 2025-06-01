using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
/// Abstract base class for testing MCP functionality across different transports.
/// This class contains all the core MCP test logic that should work identically
/// regardless of the transport (STDIO, SSE, WebSocket, etc.).
/// 
/// Concrete implementations provide transport-specific client creation and server setup.
/// 
/// NOTE: Currently only STDIO transport is fully supported. SSE transport implementation
/// is pending SDK updates with proper SSE client support. When SSE client support is
/// available, create SseMcpTransportTests.cs that inherits from this base class.
/// </summary>
public abstract class McpTransportTestBase : IDisposable
{
    protected readonly ITestOutputHelper _output;
    private IMcpClient? _client;
    private bool _disposed = false;

    protected McpTransportTestBase(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    #region Abstract Methods - Transport-Specific Implementation

    /// <summary>
    /// Creates and returns an MCP client for the specific transport.
    /// This is where transport-specific setup happens (STDIO process, SSE HTTP client, etc.).
    /// </summary>
    protected abstract Task<IMcpClient> CreateClientAsync();

    /// <summary>
    /// Gets the name of the transport being tested (for logging/debugging).
    /// </summary>
    protected abstract string GetTransportName();

    /// <summary>
    /// Performs any transport-specific server setup if needed.
    /// For STDIO: might start a server process
    /// For SSE: might start an HTTP server
    /// </summary>
    protected virtual Task SetupServerAsync() => Task.CompletedTask;

    /// <summary>
    /// Performs any transport-specific server teardown if needed.
    /// </summary>
    protected virtual Task TeardownServerAsync() => Task.CompletedTask;

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets or creates the MCP client for this test session.
    /// </summary>
    protected async Task<IMcpClient> GetClientAsync()
    {
        if (_client == null)
        {
            await SetupServerAsync();
            _client = await CreateClientAsync();
            _output.WriteLine($"✅ {GetTransportName()} MCP client created successfully");
        }
        return _client;
    }

    /// <summary>
    /// Generates a unique connection ID for test isolation.
    /// </summary>
    protected string GenerateTestConnectionId(string testName = "")
    {
        var suffix = string.IsNullOrEmpty(testName) ? "" : $"-{testName}";
        return $"test-{GetTransportName().ToLower()}-{Guid.NewGuid():N}{suffix}";
    }

    /// <summary>
    /// Generates a unique user ID for test isolation.
    /// </summary>
    protected string GenerateTestUserId(string testName = "")
    {
        var suffix = string.IsNullOrEmpty(testName) ? "" : $"-{testName}";
        return $"user-{GetTransportName().ToLower()}-{Guid.NewGuid():N}{suffix}";
    }

    #endregion

    #region Core MCP Functionality Tests

    [Fact]
    public async Task ToolDiscovery_ShouldDiscoverAll13MemoryTools()
    {
        // Arrange
        var client = await GetClientAsync();
        _output.WriteLine($"🔍 Testing tool discovery via {GetTransportName()} transport");

        // Act
        var tools = await client.ListToolsAsync();

        // Assert
        Assert.NotNull(tools);
        
        var toolNames = tools.Select(t => t.Name).ToList();
        _output.WriteLine($"📋 Discovered {toolNames.Count} tools: {string.Join(", ", toolNames)}");
        
        // Memory operation tools (8)
        Assert.Contains("memory_add", toolNames);
        Assert.Contains("memory_search", toolNames);
        Assert.Contains("memory_get_all", toolNames);
        Assert.Contains("memory_update", toolNames);
        Assert.Contains("memory_delete", toolNames);
        Assert.Contains("memory_delete_all", toolNames);
        Assert.Contains("memory_get_history", toolNames);
        Assert.Contains("memory_get_stats", toolNames);
        
        // Session management tools (5)
        Assert.Contains("memory_init_session", toolNames);
        Assert.Contains("memory_get_session", toolNames);
        Assert.Contains("memory_update_session", toolNames);
        Assert.Contains("memory_clear_session", toolNames);
        Assert.Contains("memory_resolve_session", toolNames);
        
        // Should have exactly 13 tools
        Assert.Equal(13, toolNames.Count);
        _output.WriteLine($"✅ {GetTransportName()}: All 13 MCP tools discovered successfully");
    }

    [Fact]
    public async Task MemoryAdd_WithoutConnectionId_ShouldAutoGenerateConnectionId()
    {
        // Arrange
        var client = await GetClientAsync();
        var userId = GenerateTestUserId("add-auto");
        _output.WriteLine($"🧪 Testing memory_add with auto-generated connection ID via {GetTransportName()}");

        var arguments = new Dictionary<string, object?>
        {
            ["content"] = $"Test memory without connection ID via {GetTransportName()}",
            ["userId"] = userId
        };

        // Act
        var response = await client.CallToolAsync("memory_add", arguments);

        // Assert
        Assert.NotNull(response);
        Assert.NotNull(response.Content);
        Assert.NotEmpty(response.Content);
        Assert.Equal("text", response.Content[0].Type);
        
        var responseText = response.Content[0].Text;
        Assert.NotNull(responseText);
        
        _output.WriteLine($"📝 {GetTransportName()} memory_add response: {responseText}");
        
        var result = JsonSerializer.Deserialize<JsonElement>(responseText);
        Assert.True(result.GetProperty("success").GetBoolean());
        
        var memory = result.GetProperty("memory");
        Assert.Equal($"Test memory without connection ID via {GetTransportName()}", memory.GetProperty("content").GetString());
        Assert.Equal(userId, memory.GetProperty("user_id").GetString());
        Assert.True(memory.GetProperty("id").GetInt32() > 0);
        
        _output.WriteLine($"✅ {GetTransportName()}: memory_add with auto-generated connection ID works");
    }

    [Fact]
    public async Task SessionWorkflow_InitAddSearchUpdateDelete_ShouldWorkEndToEnd()
    {
        // Arrange
        var client = await GetClientAsync();
        var connectionId = GenerateTestConnectionId("workflow");
        var userId = GenerateTestUserId("workflow");
        var agentId = $"workflow_agent_{Guid.NewGuid():N}";
        var runId = $"workflow_run_{Guid.NewGuid():N}";
        
        _output.WriteLine($"🔄 Testing complete workflow via {GetTransportName()}: {connectionId}");

        // Act & Assert - Initialize session
        var initResponse = await client.CallToolAsync("memory_init_session", new Dictionary<string, object?>
        {
            ["connectionId"] = connectionId,
            ["userId"] = userId,
            ["agentId"] = agentId,
            ["runId"] = runId
        });
        
        var initResponseText = initResponse.Content[0].Text!;
        _output.WriteLine($"📝 {GetTransportName()} session init: {initResponseText}");
        var initResult = JsonSerializer.Deserialize<JsonElement>(initResponseText);
        Assert.True(initResult.GetProperty("success").GetBoolean());

        // Add memory
        var addResponse = await client.CallToolAsync("memory_add", new Dictionary<string, object?>
        {
            ["connectionId"] = connectionId,
            ["content"] = $"Workflow test memory content via {GetTransportName()}"
        });
        
        var addResponseText = addResponse.Content[0].Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory add: {addResponseText}");
        var addResult = JsonSerializer.Deserialize<JsonElement>(addResponseText);
        Assert.True(addResult.GetProperty("success").GetBoolean());
        var memoryId = addResult.GetProperty("memory").GetProperty("id").GetInt32();

        // Search memory
        var searchResponse = await client.CallToolAsync("memory_search", new Dictionary<string, object?>
        {
            ["connectionId"] = connectionId,
            ["query"] = "workflow test"
        });
        
        var searchResponseText = searchResponse.Content[0].Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory search: {searchResponseText}");
        var searchResult = JsonSerializer.Deserialize<JsonElement>(searchResponseText);
        Assert.True(searchResult.GetProperty("success").GetBoolean());
        Assert.True(searchResult.GetProperty("total_results").GetInt32() > 0);

        // Update memory
        var updateResponse = await client.CallToolAsync("memory_update", new Dictionary<string, object?>
        {
            ["connectionId"] = connectionId,
            ["id"] = memoryId,
            ["content"] = $"Updated workflow test memory content via {GetTransportName()}"
        });
        
        var updateResponseText = updateResponse.Content[0].Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory update: {updateResponseText}");
        var updateResult = JsonSerializer.Deserialize<JsonElement>(updateResponseText);
        Assert.True(updateResult.GetProperty("success").GetBoolean());

        // Delete memory
        var deleteResponse = await client.CallToolAsync("memory_delete", new Dictionary<string, object?>
        {
            ["connectionId"] = connectionId,
            ["id"] = memoryId
        });
        
        var deleteResponseText = deleteResponse.Content[0].Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory delete: {deleteResponseText}");
        var deleteResult = JsonSerializer.Deserialize<JsonElement>(deleteResponseText);
        Assert.True(deleteResult.GetProperty("success").GetBoolean());
        
        _output.WriteLine($"✅ {GetTransportName()}: Complete workflow test passed");
    }

    [Fact]
    public async Task SessionIsolation_DifferentConnections_ShouldNotInterfere()
    {
        // Arrange
        var client = await GetClientAsync();
        var connection1 = GenerateTestConnectionId("isolation1");
        var connection2 = GenerateTestConnectionId("isolation2");
        var user1 = GenerateTestUserId("isolation1");
        var user2 = GenerateTestUserId("isolation2");
        
        _output.WriteLine($"🔒 Testing session isolation via {GetTransportName()}: {connection1} vs {connection2}");

        // Initialize two separate sessions
        var init1Response = await client.CallToolAsync("memory_init_session", new Dictionary<string, object?>
        {
            ["connectionId"] = connection1,
            ["userId"] = user1
        });
        _output.WriteLine($"📝 {GetTransportName()} init1: {init1Response.Content[0].Text}");
        
        var init2Response = await client.CallToolAsync("memory_init_session", new Dictionary<string, object?>
        {
            ["connectionId"] = connection2,
            ["userId"] = user2
        });
        _output.WriteLine($"📝 {GetTransportName()} init2: {init2Response.Content[0].Text}");

        // Add memories to each session
        var add1Response = await client.CallToolAsync("memory_add", new Dictionary<string, object?>
        {
            ["connectionId"] = connection1,
            ["content"] = $"Memory for user 1 via {GetTransportName()}"
        });
        _output.WriteLine($"📝 {GetTransportName()} add1: {add1Response.Content[0].Text}");
        
        var add2Response = await client.CallToolAsync("memory_add", new Dictionary<string, object?>
        {
            ["connectionId"] = connection2,
            ["content"] = $"Memory for user 2 via {GetTransportName()}"
        });
        _output.WriteLine($"📝 {GetTransportName()} add2: {add2Response.Content[0].Text}");

        // Search in each session - should only find their own memories
        var search1Response = await client.CallToolAsync("memory_search", new Dictionary<string, object?>
        {
            ["connectionId"] = connection1,
            ["query"] = "memory"
        });
        _output.WriteLine($"📝 {GetTransportName()} search1: {search1Response.Content[0].Text}");
        
        var search2Response = await client.CallToolAsync("memory_search", new Dictionary<string, object?>
        {
            ["connectionId"] = connection2,
            ["query"] = "memory"
        });
        _output.WriteLine($"📝 {GetTransportName()} search2: {search2Response.Content[0].Text}");

        // Assert - Each session should only see its own memories
        var search1Result = JsonSerializer.Deserialize<JsonElement>(search1Response.Content[0].Text!);
        var search2Result = JsonSerializer.Deserialize<JsonElement>(search2Response.Content[0].Text!);
        
        Assert.True(search1Result.GetProperty("success").GetBoolean());
        Assert.True(search2Result.GetProperty("success").GetBoolean());
        
        var results1 = search1Result.GetProperty("results").EnumerateArray().ToList();
        var results2 = search2Result.GetProperty("results").EnumerateArray().ToList();
        
        // Each session should have exactly 1 result (their own memory)
        Assert.Single(results1);
        Assert.Single(results2);
        
        // Verify content isolation
        Assert.Contains($"user 1 via {GetTransportName()}", results1[0].GetProperty("content").GetString()!);
        Assert.Contains($"user 2 via {GetTransportName()}", results2[0].GetProperty("content").GetString()!);
        
        _output.WriteLine($"✅ {GetTransportName()}: Session isolation works correctly");
    }

    [Fact]
    public async Task MemoryStats_WithMultipleMemories_ShouldReturnAccurateStats()
    {
        // Arrange
        var client = await GetClientAsync();
        var connectionId = GenerateTestConnectionId("stats");
        var userId = GenerateTestUserId("stats");
        
        _output.WriteLine($"📊 Testing memory stats via {GetTransportName()}: {connectionId}");

        // Initialize session
        await client.CallToolAsync("memory_init_session", new Dictionary<string, object?>
        {
            ["connectionId"] = connectionId,
            ["userId"] = userId
        });

        // Add multiple memories
        for (int i = 1; i <= 3; i++)
        {
            await client.CallToolAsync("memory_add", new Dictionary<string, object?>
            {
                ["connectionId"] = connectionId,
                ["content"] = $"Test memory {i} for stats via {GetTransportName()}"
            });
        }

        // Act
        var statsResponse = await client.CallToolAsync("memory_get_stats", new Dictionary<string, object?>
        {
            ["connectionId"] = connectionId
        });

        // Assert
        Assert.NotNull(statsResponse);
        var statsResponseText = statsResponse.Content[0].Text!;
        _output.WriteLine($"📝 {GetTransportName()} stats: {statsResponseText}");
        
        var statsResult = JsonSerializer.Deserialize<JsonElement>(statsResponseText);
        Assert.True(statsResult.GetProperty("success").GetBoolean());
        
        var stats = statsResult.GetProperty("statistics");
        Assert.True(stats.GetProperty("total_memories").GetInt32() >= 3);
        Assert.True(stats.GetProperty("total_content_size").GetInt32() > 0);
        
        _output.WriteLine($"✅ {GetTransportName()}: Memory stats work correctly");
    }

    [Fact]
    public async Task ErrorHandling_InvalidToolName_ShouldReturnError()
    {
        // Arrange
        var client = await GetClientAsync();
        _output.WriteLine($"🚫 Testing error handling for invalid tool name via {GetTransportName()}");

        // Act & Assert - Expect ModelContextProtocol.McpException which is what 0.2.x actually throws
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(async () =>
        {
            await client.CallToolAsync("invalid_tool_name", new Dictionary<string, object?>());
        });
        
        _output.WriteLine($"✅ {GetTransportName()}: Invalid tool name error handling works correctly");
    }

    [Fact]
    public async Task ErrorHandling_MissingRequiredParameters_ShouldReturnError()
    {
        // Arrange
        var client = await GetClientAsync();
        _output.WriteLine($"🚫 Testing error handling for missing parameters via {GetTransportName()}");

        // Act - memory_add requires content parameter
        var response = await client.CallToolAsync("memory_add", new Dictionary<string, object?>
        {
            ["userId"] = GenerateTestUserId("missing-params")
            // Missing required "content" parameter
        });

        // Assert - The MCP framework returns a plain text error message for missing parameters
        Assert.NotNull(response);
        var responseText = response.Content[0].Text!;
        _output.WriteLine($"📝 {GetTransportName()} missing params response: {responseText}");
        
        // The response should be a plain text error message from the MCP framework
        Assert.Contains("An error occurred invoking", responseText);
        Assert.Contains("memory_add", responseText);
        
        _output.WriteLine($"✅ {GetTransportName()}: Missing parameters error handling works correctly");
    }

    #endregion

    #region Disposal

    public virtual void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                _client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                TeardownServerAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _output.WriteLine($"⚠️ Error during {GetTransportName()} cleanup: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    #endregion
} 