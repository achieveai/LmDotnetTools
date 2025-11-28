#pragma warning disable IDE0005 // Using directive is unnecessary - IEndpointRouteBuilder requires this

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol.AspNetCore;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;

/// <summary>
/// Extension methods for mapping MCP endpoints in ASP.NET Core applications
/// </summary>
public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps MCP endpoints to expose function providers as MCP tools.
    /// This creates the /mcp endpoint that can be used with mcp-remote.
    /// </summary>
    /// <param name="app">The web application</param>
    /// <param name="pattern">Optional custom endpoint pattern (default: "/mcp")</param>
    /// <returns>The endpoint convention builder for further configuration</returns>
    public static IEndpointConventionBuilder MapMcpFunctionProviders(
        this IEndpointRouteBuilder app,
        string pattern = "/mcp")
    {
        // Use the standard ModelContextProtocol.AspNetCore extension
        // which maps the MCP protocol endpoints
        return app.MapMcp(pattern);
    }
}
