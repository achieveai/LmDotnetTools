using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace LmConfigUsageExample;

/// <summary>
/// Configuration model for .mcp.json file format
/// </summary>
public class McpJsonConfig
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, LocalMcpServerConfig>? McpServers { get; set; }
}

/// <summary>
/// Configuration for a single MCP server (local definition for .mcp.json parsing)
/// </summary>
public class LocalMcpServerConfig
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// Helper to load MCP servers from .mcp.json configuration
/// </summary>
public sealed class McpConfigLoader : IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = [];
    private readonly ILogger<McpConfigLoader> _logger;
    private bool _disposed;

    public McpConfigLoader(ILogger<McpConfigLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<McpConfigLoader>.Instance;
    }

    /// <summary>
    /// Gets the loaded MCP clients
    /// </summary>
    public IReadOnlyDictionary<string, McpClient> Clients => _clients;

    /// <summary>
    /// Loads MCP servers from the specified config file
    /// </summary>
    /// <param name="configPath">Path to .mcp.json file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of server name to MCP client</returns>
    public async Task<Dictionary<string, McpClient>> LoadFromFileAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);

        if (!File.Exists(configPath))
        {
            _logger.LogWarning("MCP config file not found: {ConfigPath}", configPath);
            return _clients;
        }

        _logger.LogInformation("Loading MCP configuration from: {ConfigPath}", configPath);

        var json = await File.ReadAllTextAsync(configPath, cancellationToken);
        var config = JsonSerializer.Deserialize<McpJsonConfig>(json);

        if (config?.McpServers == null || config.McpServers.Count == 0)
        {
            _logger.LogWarning("No MCP servers found in config file: {ConfigPath}", configPath);
            return _clients;
        }

        _logger.LogInformation("Found {ServerCount} MCP server(s) in config", config.McpServers.Count);

        foreach (var (serverName, serverConfig) in config.McpServers)
        {
            try
            {
                var client = await CreateClientAsync(serverName, serverConfig, cancellationToken);
                _clients[serverName] = client;
                _logger.LogInformation("Successfully started MCP server: {ServerName}", serverName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start MCP server: {ServerName}", serverName);
                // Continue with other servers
            }
        }

        _logger.LogInformation(
            "MCP servers loaded: {LoadedCount}/{TotalCount}",
            _clients.Count,
            config.McpServers.Count);

        return _clients;
    }

    private async Task<McpClient> CreateClientAsync(
        string serverName,
        LocalMcpServerConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(config.Command))
        {
            throw new InvalidOperationException($"No command specified for MCP server '{serverName}'");
        }

        _logger.LogDebug(
            "Starting MCP server: {ServerName}, Command: {Command}, Args: [{Args}]",
            serverName,
            config.Command,
            string.Join(", ", config.Args ?? []));

        var transportOptions = new StdioClientTransportOptions
        {
            Name = serverName,
            Command = config.Command,
            Arguments = config.Args ?? [],
        };

        // Add environment variables if specified
        if (config.Env != null && config.Env.Count > 0)
        {
            transportOptions.EnvironmentVariables = config.Env.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value);
        }

        var transport = new StdioClientTransport(transportOptions);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

        // Log available tools
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            _logger.LogDebug(
                "MCP server '{ServerName}' provides {ToolCount} tool(s): [{Tools}]",
                serverName,
                tools.Count,
                string.Join(", ", tools.Select(t => t.Name)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list tools for MCP server: {ServerName}", serverName);
        }

        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (serverName, client) in _clients)
        {
            try
            {
                if (client is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (client is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                _logger.LogDebug("Disposed MCP client: {ServerName}", serverName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP client: {ServerName}", serverName);
            }
        }

        _clients.Clear();
    }
}
