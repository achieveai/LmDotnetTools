using ModelContextProtocol;
using ModelContextProtocol.Protocol.Types;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Wrapper for MCP client
/// </summary>
public class McpClientWrapper : IMcpClient
{
    private readonly IMcpClient _client;

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
        return _client.ListToolsAsync(cancellationToken);
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
}
