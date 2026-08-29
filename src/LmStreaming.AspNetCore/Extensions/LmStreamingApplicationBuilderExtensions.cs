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
    /// Key under which <see cref="UseLmStreamingCors"/> records that it has already run on this
    /// pipeline, so a host may call it early without <see cref="UseLmStreaming"/> adding a second
    /// copy behind it.
    /// </summary>
    private const string CorsRegisteredKey = "LmStreaming.CorsRegistered";

    /// <summary>
    /// Configures the application to use LmStreaming with WebSocket support.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    /// <remarks>
    /// CORS is registered here only if <see cref="UseLmStreamingCors"/> has not already run. A
    /// host that authenticates its API wants CORS registered EARLIER than this - see that method's
    /// remarks for why - and calls it directly before its identity middleware; this call then does
    /// nothing but WebSockets.
    /// </remarks>
    public static IApplicationBuilder UseLmStreaming(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetRequiredService<IOptions<LmStreamingOptions>>().Value;

        // Enable WebSockets
        _ = app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.KeepAliveInterval });

        return app.UseLmStreamingCors();
    }

    /// <summary>
    /// Registers the CORS middleware for the configured origins. Idempotent: calling it twice on
    /// one pipeline registers one copy.
    /// </summary>
    /// <param name="app">The application builder</param>
    /// <returns>The application builder for chaining</returns>
    /// <remarks>
    /// <para>
    /// Split out of <see cref="UseLmStreaming"/> because ORDER is the whole point (#346). CORS
    /// must run before any middleware that can refuse a request, for two reasons that a host only
    /// discovers once it turns authentication on:
    /// </para>
    /// <para>
    /// A CORS preflight is <c>OPTIONS</c> with no <c>Authorization</c> header - browsers never
    /// attach one, by specification. An authentication middleware placed ahead of CORS answers
    /// that preflight <c>401</c>, the CORS middleware never runs, the response carries no
    /// <c>Access-Control-Allow-Origin</c>, and the browser abandons the real request before it is
    /// ever sent.
    /// </para>
    /// <para>
    /// And a refusal that DOES get written - a <c>403</c> naming why an organisation was rejected -
    /// leaves without CORS headers if this middleware is behind the one that wrote it. The
    /// response is then unreadable to the cross-origin client it was written for, which defeats
    /// the entire point of answering with a stable, machine-readable code. This middleware applies
    /// its headers through <c>Response.OnStarting</c>, so registering it FIRST covers responses
    /// written by everything downstream.
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseLmStreamingCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Properties.ContainsKey(CorsRegisteredKey))
        {
            return app;
        }

        app.Properties[CorsRegisteredKey] = true;

        var options = app.ApplicationServices.GetRequiredService<IOptions<LmStreamingOptions>>().Value;

        if (!options.EnableCors)
        {
            return app;
        }

        return app.UseCors(builder =>
            _ = options.AllowedOrigins.Contains("*")
                ? builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
                : builder.WithOrigins([.. options.AllowedOrigins]).AllowAnyMethod().AllowAnyHeader().AllowCredentials()
        );
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

        _ = endpoints.Map(
            options.WebSocketPath,
            async context =>
            {
                var handler = context.RequestServices.GetRequiredService<IMessageWebSocketHandler>();
                await handler.HandleWebSocketAsync(context, context.RequestAborted);
            }
        );

        return endpoints;
    }
}
