using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
///     Covers the <c>omitServerPrefix</c> option on <c>AddMcpClientsAsync</c>, which lets MCP
///     tools surface under their bare names (e.g. <c>Add</c>) instead of
///     <c>{serverName}-{toolName}</c>, falling back to per-server prefixing only on collision.
///     Drives the real <c>McpSampleServer</c> over stdio, mirroring <see cref="McpServerTests" />.
/// </summary>
public class McpClientFunctionProviderPrefixTests
{
    // Resolve the sample-server apphost cross-platform: the apphost has no extension on
    // Unix and a ".exe" suffix on Windows (McpServerTests.ServerLocation hardcodes ".exe").
    private static string ServerCommand =>
        Path.Combine(
            Path.GetDirectoryName(typeof(McpClientFunctionProviderPrefixTests).Assembly.Location)!,
            OperatingSystem.IsWindows()
                ? "AchieveAi.LmDotnetTools.McpSampleServer.exe"
                : "AchieveAi.LmDotnetTools.McpSampleServer"
        );

    private static async Task<McpClient> CreateSampleServerClientAsync()
    {
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "test-server",
                Command = ServerCommand,
                Arguments = Array.Empty<string>(),
            }
        );

        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task AddMcpClients_WithOmitServerPrefix_ExposesBareToolNamesAndDispatches()
    {
        var client = await CreateSampleServerClientAsync();
        try
        {
            var registry = new FunctionRegistry();
            _ = await registry.AddMcpClientsAsync(
                new Dictionary<string, McpClient> { ["sandbox"] = client },
                "sandbox",
                omitServerPrefix: true
            );

            var (contracts, handlers) = registry.Build();
            var names = contracts.Select(c => c.Name).ToList();

            // Bare tool name, no "sandbox-" prefix.
            Assert.Contains("Add", names);
            Assert.DoesNotContain(names, n => n.StartsWith("sandbox-", StringComparison.Ordinal));
            Assert.Contains("Add", handlers.Keys);

            // Dispatch still routes the bare name to the sandbox client's tool.
            var result = await handlers["Add"](
                JsonSerializer.Serialize(new { a = 5.0, b = 3.0 }),
                new ToolCallContext(),
                CancellationToken.None
            );
            Assert.Contains("8", result.ResultText); // 5 + 3 = 8
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddMcpClients_Default_KeepsServerPrefix()
    {
        var client = await CreateSampleServerClientAsync();
        try
        {
            var registry = new FunctionRegistry();
            _ = await registry.AddMcpClientsAsync(
                new Dictionary<string, McpClient> { ["sandbox"] = client },
                "sandbox"
            );

            var (contracts, _) = registry.Build();
            var names = contracts.Select(c => c.Name).ToList();

            // Historical behavior unchanged: every tool keeps the server prefix.
            Assert.Contains("sandbox-Add", names);
            Assert.DoesNotContain("Add", names);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task AddMcpClients_WithOmitServerPrefix_PrefixesOnlyCollidingTools()
    {
        var client = await CreateSampleServerClientAsync();
        try
        {
            var registry = new FunctionRegistry();
            // Registering the same client under two server ids makes every tool name collide;
            // the fallback must re-prefix the colliding names with their own server id.
            _ = await registry.AddMcpClientsAsync(
                new Dictionary<string, McpClient> { ["alpha"] = client, ["beta"] = client },
                "sandbox",
                omitServerPrefix: true
            );

            var (contracts, _) = registry.Build();
            var names = contracts.Select(c => c.Name).ToList();

            Assert.Contains("alpha-Add", names);
            Assert.Contains("beta-Add", names);
            Assert.DoesNotContain("Add", names); // bare name must not survive a collision
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
