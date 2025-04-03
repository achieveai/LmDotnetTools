using ModelContextProtocol.Protocol.Types;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Interface for MCP clients
/// </summary>
public interface IMcpClient
{
    /// <summary>
    /// Lists the tools available from the MCP server
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of tools</returns>
    Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls a tool on the MCP server
    /// </summary>
    /// <param name="toolName">Name of the tool to call</param>
    /// <param name="arguments">Arguments for the tool</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response from the tool</returns>
    Task<CallToolResponse> CallToolAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);
}
