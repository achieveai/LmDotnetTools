using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.Examples;

/// <summary>
/// Example demonstrating how to use FunctionRegistry with MCP clients
/// </summary>
public class FunctionRegistryMcpExample
{
    /// <summary>
    /// Shows how to add MCP clients to FunctionRegistry using the new extension methods
    /// </summary>
    public static async Task<FunctionCallMiddleware> CreateMiddlewareWithMcpClientsAsync(
        Dictionary<string, IMcpClient> mcpClients,
        ILogger? logger = null
    )
    {
        // Create function registry
        var registry = new FunctionRegistry();

        // Add MCP clients using the new extension method
        await registry.AddMcpClientsAsync(mcpClients, "MyMcpProvider", logger as ILogger<McpClientFunctionProvider>);

        // You can also add individual clients
        // await registry.AddMcpClientAsync(someClient, "client1", "Provider1", logger);

        // Build the middleware with conflict resolution
        return registry
            .WithConflictResolution(ConflictResolution.PreferMcp)
            .BuildMiddleware("ExampleMiddleware", logger as ILogger<FunctionCallMiddleware>);
    }

    /// <summary>
    /// Example of mixing MCP clients with other function providers
    /// </summary>
    public static async Task<FunctionCallMiddleware> CreateMixedMiddlewareAsync(
        Dictionary<string, IMcpClient> mcpClients,
        IFunctionProvider otherProvider,
        ILogger? logger = null
    )
    {
        var registry = new FunctionRegistry();

        // Add MCP clients
        await registry.AddMcpClientsAsync(mcpClients, "McpProvider", logger as ILogger<McpClientFunctionProvider>);

        // Add other function providers
        registry.AddProvider(otherProvider);

        // Build with custom conflict resolution
        return registry
            .WithConflictHandler(
                (functionName, candidates) =>
                {
                    // Custom logic to resolve conflicts
                    // For example, prefer MCP functions over others
                    return candidates.FirstOrDefault(c => c.ProviderName.StartsWith("Mcp")) ?? candidates.First();
                }
            )
            .BuildMiddleware("MixedMiddleware", logger as ILogger<FunctionCallMiddleware>);
    }
}
