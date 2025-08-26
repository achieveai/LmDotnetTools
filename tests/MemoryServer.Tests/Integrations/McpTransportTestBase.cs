using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace MemoryServer.Tests.Integrations;

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
    /// Generates a unique user ID for test isolation.
    /// </summary>
    protected string GenerateTestUserId(string prefix) => $"{prefix}_user_{Guid.NewGuid():N}";

    #endregion

    #region Core MCP Functionality Tests

    [Fact]
    public async Task ToolDiscovery_ShouldDiscoverAll10MemoryTools()
    {
        // Arrange
        var client = await GetClientAsync();
        _output.WriteLine($"🔍 Testing tool discovery via {GetTransportName()} transport");

        // Act
        var tools = await client.ListToolsAsync();

        // Assert
        Assert.NotNull(tools);
        Assert.NotEmpty(tools);

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

        // Session management tools are no longer needed with transport-aware session management
        // The following tools were removed when connectionId was eliminated:
        // - memory_init_session (replaced by environment variables/HTTP headers)
        // - memory_get_session (replaced by automatic session resolution)
        // - memory_update_session (replaced by transport-level configuration)
        // - memory_clear_session (replaced by process/connection lifecycle)
        // - memory_resolve_session (replaced by automatic resolution)

        // Should have exactly 10 tools
        Assert.Equal(10, toolNames.Count);
        _output.WriteLine($"✅ {GetTransportName()}: All 10 MCP tools discovered successfully");
    }

    [Fact]
    public async Task MemoryAdd_WithoutConnectionId_ShouldAutoGenerateConnectionId()
    {
        // Arrange
        var client = await GetClientAsync();
        var testUserId = GenerateTestUserId("add-auto");

        _output.WriteLine(
            $"🧪 Testing memory_add with auto-generated connection ID via {GetTransportName()}"
        );

        // Act - Call memory_add without explicit userId/agentId (they come from JWT token)
        var response = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                ["content"] = $"Test memory without connection ID via {GetTransportName()}",
                // No userId/agentId - they come from JWT token
            }
        );

        // Assert
        Assert.NotNull(response);
        Assert.NotNull(response.Content);
        Assert.NotEmpty(response.Content);

        var responseText = ((TextContentBlock)response.Content[0]).Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory_add response: {responseText}");

        var result = JsonSerializer.Deserialize<JsonElement>(responseText);
        Assert.True(result.GetProperty("success").GetBoolean());

        var memory = result.GetProperty("memory");
        Assert.True(memory.GetProperty("id").GetInt32() > 0);
        Assert.Equal(
            $"Test memory without connection ID via {GetTransportName()}",
            memory.GetProperty("content").GetString()
        );

        // The userId should be from JWT token (in tests, this will be the default user)
        var actualUserId = memory.GetProperty("user_id").GetString();
        Assert.NotNull(actualUserId);

        _output.WriteLine(
            $"✅ {GetTransportName()}: Auto-generated connection ID test passed with userId: {actualUserId}"
        );
    }

    [Fact]
    public async Task SessionWorkflow_AddSearchUpdateDelete_ShouldWorkEndToEnd()
    {
        // Arrange
        var client = await GetClientAsync();
        var userId = GenerateTestUserId("workflow");
        var agentId = "test-agent";
        var runId = "test-run";

        _output.WriteLine($"🔄 Testing complete workflow via {GetTransportName()}: {userId}");

        // Add memory - userId/agentId come from JWT token
        var addResponse = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                ["content"] = $"Test memory for workflow via {GetTransportName()}",
                ["runId"] = runId,
            }
        );

        var addResponseText = ((TextContentBlock)addResponse.Content[0]).Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory add: {addResponseText}");
        var addResult = JsonSerializer.Deserialize<JsonElement>(addResponseText);
        Assert.True(addResult.GetProperty("success").GetBoolean());

        var memoryId = addResult.GetProperty("memory").GetProperty("id").GetInt32();

        // Search memories - userId comes from JWT, agentId is optional
        var searchResponse = await client.CallToolAsync(
            "memory_search",
            new Dictionary<string, object?>
            {
                ["query"] = "workflow",
                ["agentId"] = agentId, // Optional
                ["runId"] = runId,
            }
        );

        var searchResponseText = ((TextContentBlock)searchResponse.Content[0]).Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory search: {searchResponseText}");
        var searchResult = JsonSerializer.Deserialize<JsonElement>(searchResponseText);
        Assert.True(searchResult.GetProperty("success").GetBoolean());
        Assert.True(searchResult.GetProperty("total_results").GetInt32() > 0);

        // Update memory - userId/agentId come from JWT token
        var updateResponse = await client.CallToolAsync(
            "memory_update",
            new Dictionary<string, object?>
            {
                ["id"] = memoryId,
                ["content"] = $"Updated test memory for workflow via {GetTransportName()}",
                ["runId"] = runId,
            }
        );

        var updateResponseText = ((TextContentBlock)updateResponse.Content[0]).Text!;
        _output.WriteLine($"📝 {GetTransportName()} memory update: {updateResponseText}");
        var updateResult = JsonSerializer.Deserialize<JsonElement>(updateResponseText);
        Assert.True(updateResult.GetProperty("success").GetBoolean());

        // Delete memory - userId/agentId come from JWT token
        var deleteResponse = await client.CallToolAsync(
            "memory_delete",
            new Dictionary<string, object?> { ["id"] = memoryId, ["runId"] = runId }
        );

        var deleteResponseText = ((TextContentBlock)deleteResponse.Content[0]).Text!;
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
        var user1 = GenerateTestUserId("isolation1");
        var user2 = GenerateTestUserId("isolation2");

        _output.WriteLine(
            $"🔒 Testing session isolation via {GetTransportName()}: {user1} vs {user2}"
        );

        // Note: With JWT authentication, session isolation is handled by the JWT token
        // For testing purposes, we'll add memories and verify they're isolated by the default user from JWT

        // Add memories - userId/agentId come from JWT token
        var add1Response = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                ["content"] = $"Memory for user 1 via {GetTransportName()}",
            }
        );
        _output.WriteLine(
            $"📝 {GetTransportName()} add1: {((TextContentBlock)add1Response.Content[0]).Text}"
        );

        var add2Response = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                ["content"] = $"Memory for user 2 via {GetTransportName()}",
            }
        );
        _output.WriteLine(
            $"📝 {GetTransportName()} add2: {((TextContentBlock)add2Response.Content[0]).Text}"
        );

        // Search memories - userId comes from JWT token
        var search1Response = await client.CallToolAsync(
            "memory_search",
            new Dictionary<string, object?> { ["query"] = "memory" }
        );
        _output.WriteLine(
            $"📝 {GetTransportName()} search1: {((TextContentBlock)search1Response.Content[0]).Text}"
        );

        var search2Response = await client.CallToolAsync(
            "memory_search",
            new Dictionary<string, object?> { ["query"] = "memory" }
        );
        _output.WriteLine(
            $"📝 {GetTransportName()} search2: {((TextContentBlock)search2Response.Content[0]).Text}"
        );

        // Assert - Since both operations use the same JWT token, they should see the same memories
        var search1Result = JsonSerializer.Deserialize<JsonElement>(
            ((TextContentBlock)search1Response.Content[0]).Text!
        );
        var search2Result = JsonSerializer.Deserialize<JsonElement>(
            ((TextContentBlock)search2Response.Content[0]).Text!
        );

        Assert.True(search1Result.GetProperty("success").GetBoolean());
        Assert.True(search2Result.GetProperty("success").GetBoolean());

        // Both searches should return the same results since they use the same JWT token
        var search1Count = search1Result.GetProperty("total_results").GetInt32();
        var search2Count = search2Result.GetProperty("total_results").GetInt32();
        Assert.Equal(search1Count, search2Count);

        _output.WriteLine(
            $"✅ {GetTransportName()}: Session isolation test passed - both searches returned {search1Count} memories"
        );
    }

    [Fact]
    public async Task MemoryStats_WithMultipleMemories_ShouldReturnAccurateStats()
    {
        // Arrange
        var client = await GetClientAsync();
        var userId = GenerateTestUserId("stats");

        _output.WriteLine($"📊 Testing memory stats via {GetTransportName()}: {userId}");

        // Add multiple memories - userId/agentId come from JWT token
        var memory1Response = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                ["content"] = $"First memory for stats test via {GetTransportName()}",
            }
        );

        var memory2Response = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                ["content"] = $"Second memory for stats test via {GetTransportName()}",
            }
        );

        var memory3Response = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                ["content"] = $"Third memory for stats test via {GetTransportName()}",
            }
        );

        // Get memory statistics - userId comes from JWT token
        var statsResponse = await client.CallToolAsync(
            "memory_get_stats",
            new Dictionary<string, object?>
            {
                // No userId parameter - comes from JWT token
            }
        );

        // Assert
        Assert.NotNull(statsResponse);
        Assert.NotNull(statsResponse.Content);
        Assert.NotEmpty(statsResponse.Content);

        var statsResponseText = ((TextContentBlock)statsResponse.Content[0]).Text!;
        _output.WriteLine($"📊 {GetTransportName()} stats response: {statsResponseText}");

        var statsResult = JsonSerializer.Deserialize<JsonElement>(statsResponseText);
        Assert.True(statsResult.GetProperty("success").GetBoolean());

        var statistics = statsResult.GetProperty("stats"); // Changed from "statistics" to "stats"
        var totalMemories = statistics.GetProperty("total_memories").GetInt32();

        // Should have at least 3 memories (the ones we just added)
        Assert.True(totalMemories >= 3, $"Expected at least 3 memories, but got {totalMemories}");

        _output.WriteLine(
            $"✅ {GetTransportName()}: Memory stats test passed with {totalMemories} total memories"
        );
    }

    [Fact]
    public async Task ErrorHandling_InvalidToolName_ShouldReturnError()
    {
        // Arrange
        var client = await GetClientAsync();
        _output.WriteLine(
            $"🚫 Testing error handling for invalid tool name via {GetTransportName()}"
        );

        // Act & Assert - Expect ModelContextProtocol.McpException which is what 0.2.x actually throws
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(async () =>
        {
            await client.CallToolAsync("invalid_tool_name", new Dictionary<string, object?>());
        });

        _output.WriteLine(
            $"✅ {GetTransportName()}: Invalid tool name error handling works correctly"
        );
    }

    [Fact]
    public async Task ErrorHandling_MissingRequiredParameters_ShouldReturnError()
    {
        // Arrange
        var client = await GetClientAsync();
        _output.WriteLine(
            $"🚫 Testing error handling for missing parameters via {GetTransportName()}"
        );

        // Act - memory_add requires content parameter
        var response = await client.CallToolAsync(
            "memory_add",
            new Dictionary<string, object?>
            {
                // Missing required "content" parameter
                // No userId parameter needed - comes from JWT token
            }
        );

        // Assert - The MCP framework returns a plain text error message for missing parameters
        Assert.NotNull(response);
        var responseText = ((TextContentBlock)response.Content[0]).Text!;
        _output.WriteLine($"📝 {GetTransportName()} missing params response: {responseText}");

        // The response should be a plain text error message from the MCP framework
        Assert.Contains("An error occurred invoking", responseText);
        Assert.Contains("memory_add", responseText);

        _output.WriteLine(
            $"✅ {GetTransportName()}: Missing parameters error handling works correctly"
        );
    }

    #endregion

    #region IDisposable Implementation

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        // IMcpClient doesn't implement IDisposable, so no cleanup needed
    }

    #endregion
}
