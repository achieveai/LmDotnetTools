using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Models;

/// <summary>
///     Pins down the JSON contract for <see cref="McpConfiguration"/>. The
///     <c>mcpServers</c> property name is the integration point with the Claude CLI's
///     <c>.mcp.json</c> file format and must remain case-sensitive and exact.
/// </summary>
public class McpConfigurationTests
{
    [Fact]
    public void Deserialize_McpServersJson_PopulatesDictionary()
    {
        const string json = """{"mcpServers":{"server1":{"command":"node","args":["a.js"]}}}""";

        var config = JsonSerializer.Deserialize<McpConfiguration>(json);

        Assert.NotNull(config);
        Assert.Single(config!.McpServers);
        Assert.True(config.McpServers.ContainsKey("server1"));
        Assert.Equal("node", config.McpServers["server1"].Command);
        Assert.NotNull(config.McpServers["server1"].Args);
        Assert.Equal(["a.js"], config.McpServers["server1"].Args!);
    }

    [Fact]
    public void Serialize_McpConfiguration_PreservesMcpServersPropertyName()
    {
        var config = new McpConfiguration
        {
            McpServers =
            {
                ["server1"] = new LmCore.AgentRuntime.McpServerConfig
                {
                    Type = "stdio",
                    Command = "node",
                    Args = ["a.js"],
                },
            },
        };

        var json = JsonSerializer.Serialize(config);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Case-sensitive: the property must be `mcpServers`, not `McpServers` or `mcpservers`.
        Assert.True(root.TryGetProperty("mcpServers", out var serversElement));
        Assert.False(root.TryGetProperty("McpServers", out _));
        Assert.Equal(JsonValueKind.Object, serversElement.ValueKind);
        Assert.True(serversElement.TryGetProperty("server1", out var server1));
        Assert.Equal("node", server1.GetProperty("command").GetString());
    }

    [Fact]
    public void RoundTrip_PreservesContents()
    {
        const string json = """{"mcpServers":{"server1":{"command":"node","args":["a.js"]}}}""";

        var roundTripped = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<McpConfiguration>(json));
        using var doc = JsonDocument.Parse(roundTripped);
        var server1 = doc.RootElement.GetProperty("mcpServers").GetProperty("server1");

        Assert.Equal("node", server1.GetProperty("command").GetString());
        var args = server1.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Equal(["a.js"], args);
    }
}
