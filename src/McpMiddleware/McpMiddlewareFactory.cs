using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Factory for creating MCP middleware
/// </summary>
public class McpMiddlewareFactory
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<McpMiddlewareFactory> _logger;

    /// <summary>
    /// Creates a new instance of the McpMiddlewareFactory
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory for creating typed loggers</param>
    public McpMiddlewareFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<McpMiddlewareFactory>() ?? NullLogger<McpMiddlewareFactory>.Instance;
    }

    /// <summary>
    /// Creates a new MCP middleware from a configuration file asynchronously
    /// </summary>
    /// <param name="configFilePath">Path to the configuration file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created middleware</returns>
    public async Task<IStreamingMiddleware> CreateFromConfigFileAsync(
        string configFilePath,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "Creating MCP middleware from config file asynchronously: {ConfigFilePath}",
            configFilePath
        );

        _logger.LogDebug("Factory initialization: Reading configuration file: {ConfigFilePath}", configFilePath);

        // Read and parse the configuration file
        var configJson = await File.ReadAllTextAsync(configFilePath, cancellationToken);

        _logger.LogDebug("Configuration file read: Size={ConfigSize} bytes", configJson.Length);

        var config =
            JsonSerializer.Deserialize<McpMiddlewareConfiguration>(configJson)
            ?? throw new InvalidOperationException($"Failed to deserialize config file: {configFilePath}");

        _logger.LogDebug("Configuration deserialized: ClientCount={ClientCount}", config.Clients.Count);

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
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Creating MCP middleware from config with {ClientCount} clients", config.Clients.Count);

        _logger.LogDebug(
            "MCP client configuration validation: ClientIds={ClientIds}",
            string.Join(", ", config.Clients.Keys)
        );

        // Create MCP clients from the configuration
        var mcpClients = new Dictionary<string, IMcpClient>();

        foreach (var clientConfig in config.Clients)
        {
            try
            {
                var clientId = clientConfig.Key;
                var clientSettings = clientConfig.Value;

                _logger.LogInformation("Creating MCP client: {ClientId}", clientId);
                _logger.LogDebug(
                    "Client validation: ClientId={ClientId}, ConfigType={ConfigType}",
                    clientId,
                    clientSettings?.GetType().Name ?? "null"
                );

                // Create transport from configuration
                if (clientSettings == null)
                {
                    throw new ArgumentNullException(
                        nameof(clientSettings),
                        $"Client settings for '{clientId}' cannot be null"
                    );
                }
                var transport = CreateTransportFromConfig(clientId, clientSettings);
                _logger.LogDebug(
                    "Transport created for client: ClientId={ClientId}, TransportType={TransportType}",
                    clientId,
                    transport.GetType().Name
                );

                var client = await McpClientFactory.CreateAsync(transport, cancellationToken: cancellationToken);
                mcpClients[clientId] = client;

                _logger.LogInformation("Successfully created MCP client: {ClientId}", clientId);
                _logger.LogDebug(
                    "Client creation completed: ClientId={ClientId}, ClientType={ClientType}",
                    clientId,
                    client.GetType().Name
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create MCP client: {ClientId}", clientConfig.Key);
                throw;
            }
        }

        _logger.LogDebug(
            "Middleware setup: Preparing to create middleware with {ClientCount} validated clients",
            mcpClients.Count
        );

        // Create loggers for middleware components
        var mcpMiddlewareLogger = _loggerFactory?.CreateLogger<McpMiddleware>();
        var functionCallMiddlewareLogger = _loggerFactory?.CreateLogger<FunctionCallMiddleware>();

        _logger.LogDebug(
            "Creating MCP middleware with logger propagation: HasMcpLogger={HasMcpLogger}, HasFunctionLogger={HasFunctionLogger}",
            mcpMiddlewareLogger != null,
            functionCallMiddlewareLogger != null
        );

        _logger.LogDebug(
            "Function contract processing: Starting middleware creation with automatic contract extraction"
        );

        // Create the middleware using the async factory pattern with loggers
        return await McpMiddleware.CreateAsync(
            mcpClients,
            logger: mcpMiddlewareLogger,
            functionCallLogger: functionCallMiddlewareLogger,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Creates a transport object from configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="clientSettings">The client configuration settings</param>
    /// <returns>The created transport</returns>
    private static IClientTransport CreateTransportFromConfig(string clientId, object clientSettings)
    {
        // Handle JsonElement case (when deserialized from JSON)
        if (clientSettings is JsonElement jsonElement)
        {
            return CreateTransportFromJsonElement(clientId, jsonElement);
        }

        // Handle direct object case - try to extract command and arguments
        return clientSettings is Dictionary<string, object> configDict
            ? CreateTransportFromDictionary(clientId, configDict)
            : throw new InvalidOperationException(
                $"Unsupported client configuration type for client '{clientId}': {clientSettings.GetType()}"
            );
    }

    /// <summary>
    /// Creates a transport from a JsonElement configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="jsonElement">The JSON configuration</param>
    /// <returns>The created transport</returns>
    private static IClientTransport CreateTransportFromJsonElement(string clientId, JsonElement jsonElement)
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
        if (
            jsonElement.TryGetProperty("arguments", out var argsElement)
            && argsElement.ValueKind == JsonValueKind.Array
        )
        {
            arguments = [.. argsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
        }
        else if (
            jsonElement.TryGetProperty("Arguments", out argsElement)
            && argsElement.ValueKind == JsonValueKind.Array
        )
        {
            arguments = [.. argsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
        }
        else if (jsonElement.TryGetProperty("args", out argsElement) && argsElement.ValueKind == JsonValueKind.Array)
        {
            arguments = [.. argsElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
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
                            arguments = [.. parts.Skip(1)];
                            break;
                        }
                    }
                }
            }
        }

        return string.IsNullOrEmpty(command)
            ? throw new InvalidOperationException($"No command found in configuration for client '{clientId}'")
            : (IClientTransport)
                new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = clientId,
                        Command = command,
                        Arguments = arguments ?? [],
                    }
                );
    }

    /// <summary>
    /// Creates a transport from a dictionary configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="configDict">The configuration dictionary</param>
    /// <returns>The created transport</returns>
    private static IClientTransport CreateTransportFromDictionary(
        string clientId,
        Dictionary<string, object> configDict
    )
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

        return string.IsNullOrEmpty(command)
            ? throw new InvalidOperationException($"No command found in configuration for client '{clientId}'")
            : (IClientTransport)
                new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Name = clientId,
                        Command = command,
                        Arguments = arguments ?? [],
                    }
                );
    }

    /// <summary>
    /// Extracts arguments from various object types
    /// </summary>
    /// <param name="argsObj">The arguments object</param>
    /// <returns>Array of argument strings</returns>
    private static string[] ExtractArgumentsFromObject(object argsObj)
    {
        return argsObj switch
        {
            string[] stringArray => stringArray,
            IEnumerable<string> stringEnumerable => [.. stringEnumerable],
            IEnumerable<object> objectEnumerable => [.. objectEnumerable.Select(o => o?.ToString() ?? string.Empty)],
            string singleArg => singleArg.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            _ => [],
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
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Creating MCP middleware from {ClientCount} clients asynchronously", mcpClients.Count);

        _logger.LogDebug(
            "Client validation: Validating {ClientCount} provided clients: {ClientIds}",
            mcpClients.Count,
            string.Join(", ", mcpClients.Keys)
        );

        // Validate clients
        foreach (var kvp in mcpClients)
        {
            if (kvp.Value == null)
            {
                _logger.LogDebug("Client validation failed: ClientId={ClientId} has null client instance", kvp.Key);
                throw new ArgumentException($"Client '{kvp.Key}' is null", nameof(mcpClients));
            }
            _logger.LogDebug(
                "Client validation passed: ClientId={ClientId}, ClientType={ClientType}",
                kvp.Key,
                kvp.Value.GetType().Name
            );
        }

        _logger.LogDebug("Middleware setup: All clients validated, preparing middleware creation");

        // Create loggers for middleware components
        var mcpMiddlewareLogger = _loggerFactory?.CreateLogger<McpMiddleware>();
        var functionCallMiddlewareLogger = _loggerFactory?.CreateLogger<FunctionCallMiddleware>();

        _logger.LogDebug(
            "Creating MCP middleware from clients with logger propagation: HasMcpLogger={HasMcpLogger}, HasFunctionLogger={HasFunctionLogger}",
            mcpMiddlewareLogger != null,
            functionCallMiddlewareLogger != null
        );

        _logger.LogDebug("Function contract processing: Starting automatic contract extraction from clients");

        // Use the async factory pattern from McpMiddleware with loggers
        // This will automatically extract function contracts from the clients
        return await McpMiddleware.CreateAsync(
            mcpClients,
            logger: mcpMiddlewareLogger,
            functionCallLogger: functionCallMiddlewareLogger,
            cancellationToken: cancellationToken
        );
    }
}
