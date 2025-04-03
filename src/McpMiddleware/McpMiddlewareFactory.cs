using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
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
    /// Creates a new MCP middleware from a configuration file
    /// </summary>
    /// <param name="configFilePath">Path to the configuration file</param>
    /// <returns>The created middleware</returns>
    public IStreamingMiddleware CreateFromConfigFile(string configFilePath)
    {
        _logger?.LogInformation("Creating MCP middleware from config file: {ConfigFilePath}", configFilePath);
        
        // Read and parse the configuration file
        var configJson = File.ReadAllText(configFilePath);
        var config = JsonSerializer.Deserialize<McpMiddlewareConfiguration>(configJson) 
            ?? throw new InvalidOperationException($"Failed to deserialize config file: {configFilePath}");
        
        return CreateFromConfig(config);
    }

    /// <summary>
    /// Creates a new MCP middleware from a configuration object
    /// </summary>
    /// <param name="config">The configuration object</param>
    /// <returns>The created middleware</returns>
    public IStreamingMiddleware CreateFromConfig(McpMiddlewareConfiguration config)
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
                var client = McpClientFactory.Create(new McpClientOptions
                {
                    Id = clientSettings.Id,
                    Name = clientSettings.Name,
                    TransportType = clientSettings.TransportType,
                    TransportOptions = transportOptions
                });
                
                mcpClients[clientId] = client;
                
                _logger?.LogInformation("Successfully created MCP client: {ClientId}", clientId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to create MCP client: {ClientId}", clientConfig.Key);
                throw;
            }
        }
        
        // Create the middleware
        return new McpMiddleware(mcpClients);
    }

    /// <summary>
    /// Creates a new MCP middleware from a collection of MCP clients
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <returns>The created middleware</returns>
    public IStreamingMiddleware CreateFromClients(Dictionary<string, IMcpClient> mcpClients)
    {
        _logger?.LogInformation("Creating MCP middleware from {ClientCount} clients", mcpClients.Count);
        return new McpMiddleware(mcpClients);
    }
}
