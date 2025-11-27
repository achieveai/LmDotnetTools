using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpMiddleware.Extensions;

/// <summary>
///     Extension methods for FunctionRegistry to support MCP clients
/// </summary>
public static class FunctionRegistryExtensions
{
    /// <summary>
    ///     Adds functions from an MCP client to the registry
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="mcpClient">The MCP client</param>
    /// <param name="clientId">Client identifier</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function registry for chaining</returns>
    public static async Task<FunctionRegistry> AddMcpClientAsync(
        this FunctionRegistry registry,
        IMcpClient mcpClient,
        string clientId,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        var provider = await McpClientFunctionProvider.CreateAsync(
            mcpClient,
            clientId,
            providerName,
            logger,
            cancellationToken
        );

        ArgumentNullException.ThrowIfNull(registry);
        _ = registry.AddProvider(provider);
        return registry;
    }

    /// <summary>
    ///     Adds functions from multiple MCP clients to the registry
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function registry for chaining</returns>
    public static async Task<FunctionRegistry> AddMcpClientsAsync(
        this FunctionRegistry registry,
        Dictionary<string, IMcpClient> mcpClients,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(mcpClients);
        var provider = await McpClientFunctionProvider.CreateAsync(mcpClients, providerName, logger, cancellationToken);

        _ = registry.AddProvider(provider);
        return registry;
    }

    /// <summary>
    ///     Adds functions from multiple MCP clients to the registry with configuration
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="toolFilterConfig">Tool filtering configuration</param>
    /// <param name="serverConfigs">Per-server configurations</param>
    /// <param name="providerName">Optional provider name</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function registry for chaining</returns>
    public static async Task<FunctionRegistry> AddMcpClientsAsync(
        this FunctionRegistry registry,
        Dictionary<string, IMcpClient> mcpClients,
        FunctionFilterConfig? toolFilterConfig,
        Dictionary<string, ProviderFilterConfig>? serverConfigs,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(mcpClients);
        var provider = await McpClientFunctionProvider.CreateAsync(
            mcpClients,
            toolFilterConfig,
            serverConfigs,
            providerName,
            logger,
            cancellationToken
        );

        _ = registry.AddProvider(provider);
        return registry;
    }

    /// <summary>
    ///     Adds functions from an MCP client to the registry (convenience method with automatic client ID)
    /// </summary>
    /// <param name="registry">The function registry</param>
    /// <param name="mcpClient">The MCP client</param>
    /// <param name="providerName">Optional provider name (defaults to "McpClient-{GUID}")</param>
    /// <param name="logger">Optional logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function registry for chaining</returns>
    public static async Task<FunctionRegistry> AddMcpClientAsync(
        this FunctionRegistry registry,
        IMcpClient mcpClient,
        string? providerName = null,
        ILogger<McpClientFunctionProvider>? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        var clientId = providerName ?? $"McpClient-{Guid.NewGuid():N}";
        return await registry.AddMcpClientAsync(mcpClient, clientId, providerName, logger, cancellationToken);
    }
}
