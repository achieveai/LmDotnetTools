using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        _ = services.AddMcpServerHandlers();

        // Then add the MCP server with HTTP transport
        _ = services.AddMcpServer().WithHttpTransport();

        // Configure port if specified
        if (port.HasValue)
        {
            _ = services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
                options => options.Listen(IPAddress.Loopback, port.Value));
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
        _ = services.AddSingleton<IFunctionProvider, TProvider>();
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
        _ = services.AddSingleton(provider);
        return services;
    }

    /// <summary>
    /// Adds MCP server as a singleton IHostedService integrated with the host's DI container.
    /// The server lifecycle is managed by the AspNetCore host.
    /// The server instance can be injected into any service via DI.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddMcpFunctionProviderServer(
        this IServiceCollection services,
        Action<McpFunctionProviderServerOptions>? configure = null)
    {
        // Register options
        _ = services.Configure(configure ?? (_ => { }));

        // Register McpFunctionProviderServer as SINGLETON - injectable across all scopes
        _ = services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<McpFunctionProviderServerOptions>>().Value;
            var providers = sp.GetServices<IFunctionProvider>();

            // Filter stateful functions if configured
            if (!options.IncludeStatefulFunctions)
            {
                providers = providers.Select(p => new StatelessFunctionProviderWrapper(p));
            }

            // Build WebApplication
            var builder = WebApplication.CreateBuilder();
            _ = builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
            _ = builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

            foreach (var provider in providers)
            {
                _ = builder.Services.AddFunctionProvider(provider);
            }

            _ = builder.Services.AddMcpServerFromFunctionProviders();
            _ = builder.Services.AddCors(c => c.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

            var app = builder.Build();
            _ = app.UseCors();
            _ = app.MapMcpFunctionProviders();

            return new McpFunctionProviderServer(app);
        });

        // Register as hosted service (reuses the singleton instance)
        _ = services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<McpFunctionProviderServer>());

        return services;
    }
}
