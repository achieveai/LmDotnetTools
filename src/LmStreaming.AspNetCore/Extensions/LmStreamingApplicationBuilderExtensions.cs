using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Configuration;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;

/// <summary>
/// Extension methods for configuring LmStreaming middleware.
/// </summary>
public static class LmStreamingApplicationBuilderExtensions
{
    /// <summary>
    /// Configures the application to use LmStreaming with WebSocket support.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    public static IApplicationBuilder UseLmStreaming(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetRequiredService<IOptions<LmStreamingOptions>>().Value;

        // Enable WebSockets
        _ = app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = options.KeepAliveInterval,
        });

        // Configure CORS if enabled
        if (options.EnableCors)
        {
            _ = app.UseCors(builder =>
                _ = options.AllowedOrigins.Contains("*")
                    ? builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
                    : builder.WithOrigins([.. options.AllowedOrigins])
                           .AllowAnyMethod()
                           .AllowAnyHeader()
                           .AllowCredentials());
        }

        return app;
    }

    /// <summary>
    /// Maps the LmStreaming WebSocket endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder</param>
    /// <returns>The endpoint route builder for chaining</returns>
    public static IEndpointRouteBuilder MapLmStreamingWebSocket(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<LmStreamingOptions>>().Value;

        _ = endpoints.Map(options.WebSocketPath, async context =>
        {
            var handler = context.RequestServices.GetRequiredService<IMessageWebSocketHandler>();
            await handler.HandleWebSocketAsync(context, context.RequestAborted);
        });

        return endpoints;
    }
}
