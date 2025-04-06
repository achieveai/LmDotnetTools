using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Messages;
using ModelContextProtocol.Protocol.Types;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Wrapper for MCP client
/// </summary>
public class McpClientWrapper : IMcpClient
{
    private readonly IMcpClient _client;

    public ServerCapabilities? ServerCapabilities => _client.ServerCapabilities;

    public Implementation? ServerInfo => _client.ServerInfo;

    public string? ServerInstructions => _client.ServerInstructions;


    /// <summary>
    /// Creates a new instance of the McpClientWrapper
    /// </summary>
    /// <param name="client">The MCP client to wrap</param>
    public McpClientWrapper(IMcpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Lists the tools available from the MCP server
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of tools</returns>
    public Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        return McpClientExtensions.ListToolsAsync(_client, cancellationToken);
    }

    /// <summary>
    /// Calls a tool on the MCP server
    /// </summary>
    /// <param name="toolName">Name of the tool to call</param>
    /// <param name="arguments">Arguments for the tool</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response from the tool</returns>
    public Task<CallToolResponse> CallToolAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        return _client.CallToolAsync(toolName, arguments, cancellationToken);
    }

    public void AddNotificationHandler(string method, Func<JsonRpcNotification, Task> handler)
    {
        _client.AddNotificationHandler(method, handler);
    }

    public Task<TResult> SendRequestAsync<TResult>(JsonRpcRequest request, CancellationToken cancellationToken = default) where TResult : class
    {
        return _client.SendRequestAsync<TResult>(request, cancellationToken);
    }

    public Task SendMessageAsync(IJsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        return _client.SendMessageAsync(message, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        return _client.DisposeAsync();
    }

}
