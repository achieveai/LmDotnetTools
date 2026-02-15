namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore;

/// <summary>
/// Options for configuring the MCP Function Provider Server.
/// </summary>
public class McpFunctionProviderServerOptions
{
    /// <summary>
    /// Port to listen on. Use 0 for dynamic allocation (default).
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Whether to include stateful functions in the MCP server.
    /// When false, only stateless functions are exposed.
    /// Default is true for backward compatibility.
    /// </summary>
    public bool IncludeStatefulFunctions { get; set; } = true;

    /// <summary>
    /// MCP endpoint path (default: "/mcp").
    /// </summary>
    public string EndpointPath { get; set; } = "/mcp";
}
