using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;

/// <summary>
/// Extension methods for configuring MCP server with function providers
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds MCP server configured to expose IFunctionProvider instances as tools
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="port">Optional port number for the HTTP server (default: use existing configuration)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddMcpServerFromFunctionProviders(
        this IServiceCollection services,
        int? port = null)
    {
        // First, configure the MCP server options with handlers from function providers
        services.AddMcpServerHandlers();

        // Then add the MCP server with HTTP transport
        services.AddMcpServer().WithHttpTransport();

        // Configure port if specified
        if (port.HasValue)
        {
            services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
            {
                options.Listen(System.Net.IPAddress.Loopback, port.Value);
            });
        }

        return services;
    }

    /// <summary>
    /// Registers a function provider with the service collection
    /// </summary>
    /// <typeparam name="TProvider">The function provider type</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddFunctionProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IFunctionProvider
    {
        services.AddSingleton<IFunctionProvider, TProvider>();
        return services;
    }

    /// <summary>
    /// Registers a function provider instance with the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="provider">The function provider instance</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddFunctionProvider(
        this IServiceCollection services,
        IFunctionProvider provider)
    {
        services.AddSingleton(provider);
        return services;
    }
}
