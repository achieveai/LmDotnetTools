using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
/// Integration tests for the Memory MCP Server using actual MCP protocol communication
/// </summary>
public class MemoryMcpServerIntegrationTests : IDisposable
{
    private readonly string _serverLocation;
    private IMcpClient? _client;
    private readonly string _testConnectionId;

    public MemoryMcpServerIntegrationTests()
    {
        // Path to the Memory MCP Server executable - using same pattern as working MCP tests
        var assemblyLocation = Path.GetDirectoryName(typeof(MemoryMcpServerIntegrationTests).Assembly.Location)!;
        _serverLocation = Path.Combine(assemblyLocation, "MemoryServer.exe");
        
        // If not found in test output, try the actual build location
        if (!File.Exists(_serverLocation))
        {
            _serverLocation = Path.Combine(
                assemblyLocation,
                "..", "..", "..", "..", "McpServers", "Memory", "MemoryServer", "bin", "Debug", "net9.0", "MemoryServer.exe");
        }
        
        _testConnectionId = $"test-conn-{Guid.NewGuid():N}";
    }

    private async Task<IMcpClient> GetClientAsync()
    {
        if (_client == null)
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "memory-server",
                Command = _serverLocation,
                Arguments = Array.Empty<string>()
            });

            _client = await McpClientFactory.CreateAsync(transport);
        }
        return _client;
    }

    [Fact]
    public async Task ToolDiscovery_ShouldDiscoverAll13MemoryTools()
    {
        // Arrange
        var client = await GetClientAsync();

        // Act
        var tools = await client.ListToolsAsync();

        // Assert
        Assert.NotNull(tools);
        
        // Verify we have all 13 expected tools
        var toolNames = tools.Select(t => t.Name).ToList();
        
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
    }

    [Fact]
    public async Task MemoryAdd_WithoutConnectionId_ShouldAutoGenerateConnectionId()
    {
        // Arrange
        var client = await GetClientAsync();
        var arguments = new Dictionary<string, object?>
        {
            ["content"] = "Test memory without connection ID",
            ["userId"] = "test_user_auto"
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
        
        // Debug output
        Console.WriteLine($"DEBUG: memory_add response: {responseText}");
        
        var result = JsonSerializer.Deserialize<JsonElement>(responseText);
        Assert.True(result.GetProperty("success").GetBoolean());
        
        var memory = result.GetProperty("memory");
        Assert.Equal("Test memory without connection ID", memory.GetProperty("content").GetString());
        Assert.Equal("test_user_auto", memory.GetProperty("user_id").GetString());
        Assert.True(memory.GetProperty("id").GetInt32() > 0);
    }

    [Fact]
    public async Task SessionWorkflow_InitAddSearchUpdateDelete_ShouldWorkEndToEnd()
    {
        // Arrange
        var client = await GetClientAsync();
        var userId = $"workflow_user_{Guid.NewGuid():N}";
        var agentId = $"workflow_agent_{Guid.NewGuid():N}";
        var runId = $"workflow_run_{Guid.NewGuid():N}";

        // Step 1: Initialize session
        var initArgs = new Dictionary<string, object?>
        {
            ["connectionId"] = _testConnectionId,
            ["userId"] = userId,
            ["agentId"] = agentId,
            ["runId"] = runId
        };
        
        var initResponse = await client.CallToolAsync("memory_init_session", initArgs);
        Assert.NotNull(initResponse);
        
        var initResponseText = initResponse.Content[0].Text!;
        Console.WriteLine($"DEBUG: memory_init_session response: {initResponseText}");
        
        var initResult = JsonSerializer.Deserialize<JsonElement>(initResponseText);
        Assert.True(initResult.GetProperty("success").GetBoolean());

        // Step 2: Add memory using session defaults
        var addArgs = new Dictionary<string, object?>
        {
            ["connectionId"] = _testConnectionId,
            ["content"] = "Workflow test memory content"
        };
        
        var addResponse = await client.CallToolAsync("memory_add", addArgs);
        Assert.NotNull(addResponse);
        
        var addResponseText = addResponse.Content[0].Text!;
        Console.WriteLine($"DEBUG: memory_add response: {addResponseText}");
        
        var addResult = JsonSerializer.Deserialize<JsonElement>(addResponseText);
        Assert.True(addResult.GetProperty("success").GetBoolean());
        
        var memoryId = addResult.GetProperty("memory").GetProperty("id").GetInt32();
        Assert.True(memoryId > 0);

        // Step 3: Search for the memory
        var searchArgs = new Dictionary<string, object?>
        {
            ["connectionId"] = _testConnectionId,
            ["query"] = "workflow test"
        };
        
        var searchResponse = await client.CallToolAsync("memory_search", searchArgs);
        Assert.NotNull(searchResponse);
        
        var searchResponseText = searchResponse.Content[0].Text!;
        Console.WriteLine($"DEBUG: memory_search response: {searchResponseText}");
        
        var searchResult = JsonSerializer.Deserialize<JsonElement>(searchResponseText);
        Assert.True(searchResult.GetProperty("success").GetBoolean());
        Assert.True(searchResult.GetProperty("total_results").GetInt32() > 0);

        // Step 4: Update the memory
        var updateArgs = new Dictionary<string, object?>
        {
            ["connectionId"] = _testConnectionId,
            ["id"] = memoryId,
            ["content"] = "Updated workflow test memory content"
        };
        
        var updateResponse = await client.CallToolAsync("memory_update", updateArgs);
        Assert.NotNull(updateResponse);
        
        var updateResponseText = updateResponse.Content[0].Text!;
        Console.WriteLine($"DEBUG: memory_update response: {updateResponseText}");
        
        var updateResult = JsonSerializer.Deserialize<JsonElement>(updateResponseText);
        Assert.True(updateResult.GetProperty("success").GetBoolean());
        Assert.Equal("Updated workflow test memory content", 
            updateResult.GetProperty("memory").GetProperty("content").GetString());

        // Step 5: Delete the memory
        var deleteArgs = new Dictionary<string, object?>
        {
            ["connectionId"] = _testConnectionId,
            ["id"] = memoryId
        };
        
        var deleteResponse = await client.CallToolAsync("memory_delete", deleteArgs);
        Assert.NotNull(deleteResponse);
        
        var deleteResponseText = deleteResponse.Content[0].Text!;
        Console.WriteLine($"DEBUG: memory_delete response: {deleteResponseText}");
        
        var deleteResult = JsonSerializer.Deserialize<JsonElement>(deleteResponseText);
        Assert.True(deleteResult.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task SessionIsolation_DifferentConnections_ShouldNotInterfere()
    {
        // Arrange
        var client = await GetClientAsync();
        var connection1 = $"conn1_{Guid.NewGuid():N}";
        var connection2 = $"conn2_{Guid.NewGuid():N}";
        var user1 = $"user1_{Guid.NewGuid():N}";
        var user2 = $"user2_{Guid.NewGuid():N}";

        // Initialize two separate sessions
        var init1Response = await client.CallToolAsync("memory_init_session", new Dictionary<string, object?>
        {
            ["connectionId"] = connection1,
            ["userId"] = user1
        });
        Console.WriteLine($"DEBUG: init1 response: {init1Response.Content[0].Text}");
        
        var init2Response = await client.CallToolAsync("memory_init_session", new Dictionary<string, object?>
        {
            ["connectionId"] = connection2,
            ["userId"] = user2
        });
        Console.WriteLine($"DEBUG: init2 response: {init2Response.Content[0].Text}");

        // Add memories to each session
        var add1Response = await client.CallToolAsync("memory_add", new Dictionary<string, object?>
        {
            ["connectionId"] = connection1,
            ["content"] = "Memory for user 1"
        });
        Console.WriteLine($"DEBUG: add1 response: {add1Response.Content[0].Text}");
        
        var add2Response = await client.CallToolAsync("memory_add", new Dictionary<string, object?>
        {
            ["connectionId"] = connection2,
            ["content"] = "Memory for user 2"
        });
        Console.WriteLine($"DEBUG: add2 response: {add2Response.Content[0].Text}");

        // Search in each session - should only find their own memories
        var search1Response = await client.CallToolAsync("memory_search", new Dictionary<string, object?>
        {
            ["connectionId"] = connection1,
            ["query"] = "memory"
        });
        Console.WriteLine($"DEBUG: search1 response: {search1Response.Content[0].Text}");
        
        var search2Response = await client.CallToolAsync("memory_search", new Dictionary<string, object?>
        {
            ["connectionId"] = connection2,
            ["query"] = "memory"
        });
        Console.WriteLine($"DEBUG: search2 response: {search2Response.Content[0].Text}");

        // Assert isolation
        var search1Result = JsonSerializer.Deserialize<JsonElement>(search1Response.Content[0].Text!);
        var search2Result = JsonSerializer.Deserialize<JsonElement>(search2Response.Content[0].Text!);
        
        Assert.True(search1Result.GetProperty("success").GetBoolean());
        Assert.True(search2Result.GetProperty("success").GetBoolean());
        
        // Each should find their own memory but not the other's
        var results1 = search1Result.GetProperty("results").EnumerateArray().ToList();
        var results2 = search2Result.GetProperty("results").EnumerateArray().ToList();
        
        Assert.NotEmpty(results1);
        Assert.NotEmpty(results2);
        
        // Verify content isolation
        var content1 = results1.First().GetProperty("content").GetString();
        var content2 = results2.First().GetProperty("content").GetString();
        
        Assert.Contains("user 1", content1);
        Assert.Contains("user 2", content2);
        Assert.DoesNotContain("user 2", content1);
        Assert.DoesNotContain("user 1", content2);
    }

    [Fact]
    public async Task ErrorHandling_InvalidToolName_ShouldReturnError()
    {
        // Arrange
        var client = await GetClientAsync();

        // Act & Assert - Expect ModelContextProtocol.McpException which is what 0.2.x actually throws
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(async () =>
        {
            await client.CallToolAsync("invalid_tool_name", new Dictionary<string, object?>());
        });
    }

    [Fact]
    public async Task ErrorHandling_MissingRequiredParameters_ShouldReturnError()
    {
        // Arrange
        var client = await GetClientAsync();

        // Act - memory_add requires content parameter
        var response = await client.CallToolAsync("memory_add", new Dictionary<string, object?>
        {
            ["userId"] = "test_user"
            // Missing required "content" parameter
        });

        // Assert - The MCP framework returns a plain text error message for missing parameters
        Assert.NotNull(response);
        var responseText = response.Content[0].Text!;
        Console.WriteLine($"DEBUG: missing params response: {responseText}");
        
        // The response should be a plain text error message from the MCP framework
        Assert.Contains("An error occurred invoking", responseText);
        Assert.Contains("memory_add", responseText);
    }

    [Fact]
    public async Task MemoryStats_AfterAddingMemories_ShouldReturnCorrectStats()
    {
        // Arrange
        var client = await GetClientAsync();
        var connectionId = $"stats_conn_{Guid.NewGuid():N}";
        var userId = $"stats_user_{Guid.NewGuid():N}";

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
                ["content"] = $"Test memory {i} for stats"
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
        Console.WriteLine($"DEBUG: stats response: {statsResponseText}");
        
        var statsResult = JsonSerializer.Deserialize<JsonElement>(statsResponseText);
        Assert.True(statsResult.GetProperty("success").GetBoolean());
        
        var stats = statsResult.GetProperty("statistics");
        Assert.True(stats.GetProperty("total_memories").GetInt32() >= 3);
        Assert.True(stats.GetProperty("total_content_size").GetInt32() > 0);
    }

    public void Dispose()
    {
        _client?.DisposeAsync().AsTask().Wait();
    }
} 