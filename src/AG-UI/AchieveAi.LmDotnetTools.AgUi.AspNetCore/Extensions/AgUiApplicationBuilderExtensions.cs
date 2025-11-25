using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Configuration;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Middleware;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Extensions;

/// <summary>
///     Extension methods for configuring AG-UI middleware in the application pipeline
/// </summary>
public static class AgUiApplicationBuilderExtensions
{
    /// <summary>
    ///     Adds AG-UI middleware to the application pipeline
    ///     This must be called after UseWebSockets() or it will automatically add WebSockets support
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseAgUi(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app, nameof(app));

        // Verify services are registered
        var options =
            app.ApplicationServices.GetService<IOptions<AgUiOptions>>()
            ?? throw new InvalidOperationException(
                "AG-UI services not registered. Call services.AddAgUi() in ConfigureServices."
            );

        // Enable WebSockets if not already enabled
        _ = app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.Value.KeepAliveInterval });

        // Add CORS if enabled
        if (options.Value.EnableCors && options.Value.AllowedOrigins.Count > 0)
        {
            _ = app.UseCors(builder =>
            {
                var origins = options.Value.AllowedOrigins.ToArray();
                _ = origins.Contains("*")
                    ? builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
                    : builder.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
            });
        }

        // Add AG-UI middleware
        _ = app.UseMiddleware<AgUiMiddleware>();

        return app;
    }

    /// <summary>
    ///     Maps AG-UI WebSocket endpoint using endpoint routing
    ///     Alternative to UseAgUi() for applications using endpoint routing
    /// </summary>
    /// <param name="endpoints">The endpoint route builder</param>
    /// <returns>The endpoint route builder for chaining</returns>
    public static IEndpointRouteBuilder MapAgUi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints, nameof(endpoints));

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<AgUiOptions>>().Value;

        _ = endpoints.Map(
            options.WebSocketPath,
            async context =>
            {
                var handler = context.RequestServices.GetRequiredService<AgUiWebSocketHandler>();
                await handler.HandleWebSocketAsync(context, context.RequestAborted);
            }
        );

        return endpoints;
    }
}
