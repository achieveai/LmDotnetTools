using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
///     Factory for creating MCP middleware
/// </summary>
public class McpMiddlewareFactory
{
    private readonly ILogger<McpMiddlewareFactory> _logger;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>
    ///     Creates a new instance of the McpMiddlewareFactory
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory for creating typed loggers</param>
    public McpMiddlewareFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<McpMiddlewareFactory>() ?? NullLogger<McpMiddlewareFactory>.Instance;
    }

    /// <summary>
    ///     Creates a new MCP middleware from a configuration file asynchronously
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
    ///     Creates a new MCP middleware from a configuration object asynchronously
    /// </summary>
    /// <param name="config">The configuration object</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created middleware</returns>
    public async Task<IStreamingMiddleware> CreateFromConfigAsync(
        McpMiddlewareConfiguration config,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(config);

        _logger.LogInformation("Creating MCP middleware from config with {ClientCount} clients", config.Clients.Count);

        _logger.LogDebug(
            "MCP client configuration validation: ClientIds={ClientIds}",
            string.Join(", ", config.Clients.Keys)
        );

        // Create MCP clients from the configuration
        var mcpClients = new Dictionary<string, McpClient>();

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

                var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
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
    ///     Creates a transport object from configuration
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
    ///     Creates a transport from a JsonElement configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="jsonElement">The JSON configuration</param>
    /// <returns>The created transport</returns>
    private static IClientTransport CreateTransportFromJsonElement(string clientId, JsonElement jsonElement)
    {
        // Check transport type - default to stdio if command is present, otherwise http
        var transportType = GetStringProperty(jsonElement, "type", "Type") ?? "stdio";

        // Check for HTTP transport indicators
        var url = GetStringProperty(jsonElement, "url", "Url", "endpoint", "Endpoint");
        if (!string.IsNullOrEmpty(url) || transportType.Equals("http", StringComparison.OrdinalIgnoreCase)
            || transportType.Equals("sse", StringComparison.OrdinalIgnoreCase))
        {
            return CreateHttpTransportFromJsonElement(clientId, jsonElement, url);
        }

        // Default to stdio transport
        return CreateStdioTransportFromJsonElement(clientId, jsonElement);
    }

    /// <summary>
    /// Creates an HTTP/SSE transport from JSON configuration
    /// </summary>
    private static IClientTransport CreateHttpTransportFromJsonElement(
        string clientId,
        JsonElement jsonElement,
        string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                $"HTTP transport requires 'url' or 'endpoint' property for client '{clientId}'");
        }

        var options = new HttpClientTransportOptions
        {
            Name = clientId,
            Endpoint = new Uri(url),
        };

        // Extract headers if present
        if (jsonElement.TryGetProperty("headers", out var headersElement)
            && headersElement.ValueKind == JsonValueKind.Object)
        {
            var headers = new Dictionary<string, string>();
            foreach (var prop in headersElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    headers[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
            if (headers.Count > 0)
            {
                options.AdditionalHeaders = headers;
            }
        }
        else if (jsonElement.TryGetProperty("Headers", out headersElement)
            && headersElement.ValueKind == JsonValueKind.Object)
        {
            var headers = new Dictionary<string, string>();
            foreach (var prop in headersElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    headers[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
            if (headers.Count > 0)
            {
                options.AdditionalHeaders = headers;
            }
        }

        // Check for timeout
        if (jsonElement.TryGetProperty("timeout", out var timeoutElement)
            && timeoutElement.TryGetInt32(out var timeoutSeconds))
        {
            options.ConnectionTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        return new HttpClientTransport(options);
    }

    /// <summary>
    /// Creates a stdio transport from JSON configuration
    /// </summary>
    private static IClientTransport CreateStdioTransportFromJsonElement(string clientId, JsonElement jsonElement)
    {
        var command = GetStringProperty(jsonElement, "command", "Command");
        string[]? arguments = null;
        Dictionary<string, string?>? environmentVariables = null;

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

        // Extract environment variables
        if (jsonElement.TryGetProperty("env", out var envElement)
            && envElement.ValueKind == JsonValueKind.Object)
        {
            environmentVariables = [];
            foreach (var prop in envElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    environmentVariables[prop.Name] = prop.Value.GetString();
                }
            }
        }
        else if (jsonElement.TryGetProperty("Env", out envElement)
            && envElement.ValueKind == JsonValueKind.Object)
        {
            environmentVariables = [];
            foreach (var prop in envElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    environmentVariables[prop.Name] = prop.Value.GetString();
                }
            }
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

        if (string.IsNullOrEmpty(command))
        {
            throw new InvalidOperationException($"No command found in configuration for client '{clientId}'");
        }

        var options = new StdioClientTransportOptions
        {
            Name = clientId,
            Command = command,
            Arguments = arguments ?? [],
        };

        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            options.EnvironmentVariables = environmentVariables;
        }

        return new StdioClientTransport(options);
    }

    /// <summary>
    /// Helper to get a string property from JsonElement with multiple possible names
    /// </summary>
    private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        return null;
    }

    /// <summary>
    ///     Creates a transport from a dictionary configuration
    /// </summary>
    /// <param name="clientId">The client ID</param>
    /// <param name="configDict">The configuration dictionary</param>
    /// <returns>The created transport</returns>
    private static IClientTransport CreateTransportFromDictionary(
        string clientId,
        Dictionary<string, object> configDict
    )
    {
        // Check transport type
        var transportType = GetDictStringValue(configDict, "type", "Type") ?? "stdio";

        // Check for HTTP transport indicators
        var url = GetDictStringValue(configDict, "url", "Url", "endpoint", "Endpoint");
        return !string.IsNullOrEmpty(url) || transportType.Equals("http", StringComparison.OrdinalIgnoreCase)
            || transportType.Equals("sse", StringComparison.OrdinalIgnoreCase)
            ? CreateHttpTransportFromDictionary(clientId, configDict, url)
            : CreateStdioTransportFromDictionary(clientId, configDict);
    }

    /// <summary>
    /// Creates an HTTP/SSE transport from dictionary configuration
    /// </summary>
    private static IClientTransport CreateHttpTransportFromDictionary(
        string clientId,
        Dictionary<string, object> configDict,
        string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException(
                $"HTTP transport requires 'url' or 'endpoint' property for client '{clientId}'");
        }

        var options = new HttpClientTransportOptions
        {
            Name = clientId,
            Endpoint = new Uri(url),
        };

        // Extract headers if present
        var headers = GetDictDictionaryValue(configDict, "headers", "Headers");
        if (headers != null && headers.Count > 0)
        {
            options.AdditionalHeaders = headers.Where(kvp => kvp.Value != null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);
        }

        // Check for timeout
        if (configDict.TryGetValue("timeout", out var timeoutObj) && timeoutObj is int timeoutSeconds)
        {
            options.ConnectionTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        return new HttpClientTransport(options);
    }

    /// <summary>
    /// Creates a stdio transport from dictionary configuration
    /// </summary>
    private static IClientTransport CreateStdioTransportFromDictionary(
        string clientId,
        Dictionary<string, object> configDict)
    {
        var command = GetDictStringValue(configDict, "command", "Command");
        string[]? arguments = null;
        Dictionary<string, string?>? environmentVariables = null;

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

        // Extract environment variables
        environmentVariables = GetDictDictionaryValue(configDict, "env", "Env");

        if (string.IsNullOrEmpty(command))
        {
            throw new InvalidOperationException($"No command found in configuration for client '{clientId}'");
        }

        var options = new StdioClientTransportOptions
        {
            Name = clientId,
            Command = command,
            Arguments = arguments ?? [],
        };

        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            options.EnvironmentVariables = environmentVariables;
        }

        return new StdioClientTransport(options);
    }

    /// <summary>
    /// Helper to get a string value from dictionary with multiple possible keys
    /// </summary>
    private static string? GetDictStringValue(Dictionary<string, object> dict, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (dict.TryGetValue(key, out var value) && value is string strValue)
            {
                return strValue;
            }
        }
        return null;
    }

    /// <summary>
    /// Helper to get a dictionary value from dictionary with multiple possible keys
    /// </summary>
    private static Dictionary<string, string?>? GetDictDictionaryValue(
        Dictionary<string, object> dict,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (dict.TryGetValue(key, out var value))
            {
                if (value is Dictionary<string, string> stringDict)
                {
                    // Convert to nullable value dictionary
                    var result = new Dictionary<string, string?>();
                    foreach (var kvp in stringDict)
                    {
                        result[kvp.Key] = kvp.Value;
                    }
                    return result;
                }
                if (value is Dictionary<string, object> objDict)
                {
                    var result = new Dictionary<string, string?>();
                    foreach (var kvp in objDict)
                    {
                        result[kvp.Key] = kvp.Value is string strVal ? strVal : kvp.Value?.ToString();
                    }
                    return result;
                }
            }
        }
        return null;
    }

    /// <summary>
    ///     Extracts arguments from various object types
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
    ///     Creates a new MCP middleware from a collection of MCP clients asynchronously
    /// </summary>
    /// <param name="mcpClients">Dictionary of MCP clients</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created middleware</returns>
    public async Task<IStreamingMiddleware> CreateFromClientsAsync(
        Dictionary<string, McpClient> mcpClients,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mcpClients);

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
