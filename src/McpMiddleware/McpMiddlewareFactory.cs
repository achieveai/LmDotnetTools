using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
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

                // Create transport from configuration
                var transport = CreateTransportFromConfig(clientId, clientSettings);
                var client = await McpClientFactory.CreateAsync(transport);
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
    /// Creates a transport object from configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="clientSettings">The client configuration settings</param>
    /// <returns>The created transport</returns>
    private IClientTransport CreateTransportFromConfig(string clientId, object clientSettings)
    {
        // Handle JsonElement case (when deserialized from JSON)
        if (clientSettings is JsonElement jsonElement)
        {
            return CreateTransportFromJsonElement(clientId, jsonElement);
        }

        // Handle direct object case - try to extract command and arguments
        if (clientSettings is Dictionary<string, object> configDict)
        {
            return CreateTransportFromDictionary(clientId, configDict);
        }

        throw new InvalidOperationException($"Unsupported client configuration type for client '{clientId}': {clientSettings.GetType()}");
    }

    /// <summary>
    /// Creates a transport from a JsonElement configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="jsonElement">The JSON configuration</param>
    /// <returns>The created transport</returns>
    private IClientTransport CreateTransportFromJsonElement(string clientId, JsonElement jsonElement)
    {
        string? command = null;
        string[]? arguments = null;

        // Try to extract command from various possible property names
        if (jsonElement.TryGetProperty("command", out var commandElement))
        {
            command = commandElement.GetString();
        }
        else if (jsonElement.TryGetProperty("Command", out commandElement))
        {
            command = commandElement.GetString();
        }

        // Try to extract arguments
        if (jsonElement.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            arguments = argsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        }
        else if (jsonElement.TryGetProperty("Arguments", out argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            arguments = argsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        }
        else if (jsonElement.TryGetProperty("args", out argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            arguments = argsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        }

        // If no command found, try to parse from a single command string that might include arguments
        if (string.IsNullOrEmpty(command))
        {
            // Look for a single command property that might contain the full command line
            foreach (var property in jsonElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var fullCommand = property.Value.GetString();
                    if (!string.IsNullOrEmpty(fullCommand))
                    {
                        var parts = fullCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            command = parts[0];
                            arguments = parts.Skip(1).ToArray();
                            break;
                        }
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(command))
        {
            throw new InvalidOperationException($"No command found in configuration for client '{clientId}'");
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = clientId,
            Command = command,
            Arguments = arguments ?? Array.Empty<string>()
        });
    }

    /// <summary>
    /// Creates a transport from a dictionary configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="configDict">The configuration dictionary</param>
    /// <returns>The created transport</returns>
    private IClientTransport CreateTransportFromDictionary(string clientId, Dictionary<string, object> configDict)
    {
        string? command = null;
        string[]? arguments = null;

        // Try to extract command
        if (configDict.TryGetValue("command", out var commandObj) && commandObj is string commandStr)
        {
            command = commandStr;
        }
        else if (configDict.TryGetValue("Command", out commandObj) && commandObj is string commandStr2)
        {
            command = commandStr2;
        }

        // Try to extract arguments
        if (configDict.TryGetValue("arguments", out var argsObj))
        {
            arguments = ExtractArgumentsFromObject(argsObj);
        }
        else if (configDict.TryGetValue("Arguments", out argsObj))
        {
            arguments = ExtractArgumentsFromObject(argsObj);
        }
        else if (configDict.TryGetValue("args", out argsObj))
        {
            arguments = ExtractArgumentsFromObject(argsObj);
        }

        if (string.IsNullOrEmpty(command))
        {
            throw new InvalidOperationException($"No command found in configuration for client '{clientId}'");
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = clientId,
            Command = command,
            Arguments = arguments ?? Array.Empty<string>()
        });
    }

    /// <summary>
    /// Extracts arguments from various object types
    /// </summary>
    /// <param name="argsObj">The arguments object</param>
    /// <returns>Array of argument strings</returns>
    private string[] ExtractArgumentsFromObject(object argsObj)
    {
        return argsObj switch
        {
            string[] stringArray => stringArray,
            IEnumerable<string> stringEnumerable => stringEnumerable.ToArray(),
            IEnumerable<object> objectEnumerable => objectEnumerable.Select(o => o?.ToString() ?? string.Empty).ToArray(),
            string singleArg => singleArg.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            _ => Array.Empty<string>()
        };
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
}
