using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Types;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Factory for creating MCP middleware
/// </summary>
public class McpMiddlewareFactory
{
    private readonly ILogger<McpMiddlewareFactory>? _logger;

    /// <summary>
    /// Creates a new instance of the McpMiddlewareFactory
    /// </summary>
    /// <param name="logger">Optional logger</param>
    public McpMiddlewareFactory(ILogger<McpMiddlewareFactory>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a new MCP middleware from a configuration file asynchronously
    /// </summary>
    /// <param name="configFilePath">Path to the configuration file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created middleware</returns>
    public async Task<IStreamingMiddleware> CreateFromConfigFileAsync(string configFilePath, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Creating MCP middleware from config file asynchronously: {ConfigFilePath}", configFilePath);
        
        // Read and parse the configuration file
        var configJson = await File.ReadAllTextAsync(configFilePath, cancellationToken);
        var config = JsonSerializer.Deserialize<McpMiddlewareConfiguration>(configJson) 
            ?? throw new InvalidOperationException($"Failed to deserialize config file: {configFilePath}");
        
        return await CreateFromConfigAsync(config, cancellationToken);
    }

    /// <summary>
    /// Creates a new MCP middleware from a configuration object asynchronously
    /// </summary>
    /// <param name="config">The configuration object</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created middleware</returns>
    public async Task<IStreamingMiddleware> CreateFromConfigAsync(
        McpMiddlewareConfiguration config,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Creating MCP middleware from config with {ClientCount} clients", config.Clients.Count);
        
        // Create MCP clients from the configuration
        var mcpClients = new Dictionary<string, IMcpClient>();
        
        foreach (var clientConfig in config.Clients)
        {
            try
            {
                var clientId = clientConfig.Key;
                var clientSettings = clientConfig.Value;
                
                _logger?.LogInformation("Creating MCP client: {ClientId}", clientId);
                
                // Create transport options
                var transportOptions = new Dictionary<string, object?>();
                if (clientSettings.TransportOptions != null)
                {
                    foreach (var option in clientSettings.TransportOptions)
                    {
                        transportOptions[option.Key] = option.Value;
                    }
                }
                
                // Create the client
                var client = await McpClientFactory.CreateAsync(clientConfig.Value);
                
                mcpClients[clientId] = client;
                
                _logger?.LogInformation("Successfully created MCP client: {ClientId}", clientId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to create MCP client: {ClientId}", clientConfig.Key);
                throw;
            }
        }
        
        // Create the middleware using the async factory pattern
        return await McpMiddleware.CreateAsync(mcpClients, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates a new MCP middleware from a collection of MCP clients asynchronously
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created middleware</returns>
    public async Task<IStreamingMiddleware> CreateFromClientsAsync(
        Dictionary<string, IMcpClient> mcpClients,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Creating MCP middleware from {ClientCount} clients asynchronously", mcpClients.Count);
        
        // Use the async factory pattern from McpMiddleware
        // This will automatically extract function contracts from the clients
        return await McpMiddleware.CreateAsync(mcpClients, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Converts an MCP client tool to a function contract
    /// </summary>
    /// <param name="tool">The MCP client tool</param>
    /// <returns>The function contract</returns>





}
