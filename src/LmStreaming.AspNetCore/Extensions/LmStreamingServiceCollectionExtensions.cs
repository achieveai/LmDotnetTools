using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Configuration;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.SSE;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;

/// <summary>
/// Extension methods for registering LmStreaming services.
/// </summary>
public static class LmStreamingServiceCollectionExtensions
{
    /// <summary>
    /// Adds LmStreaming services to the service collection.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddLmStreaming(
        this IServiceCollection services,
        Action<LmStreamingOptions>? configure = null)
    {
        // Configure options
        var options = new LmStreamingOptions();
        configure?.Invoke(options);
        _ = services.Configure<LmStreamingOptions>(o =>
        {
            o.WebSocketPath = options.WebSocketPath;
            o.SsePath = options.SsePath;
            o.EnableCors = options.EnableCors;
            o.AllowedOrigins = options.AllowedOrigins;
            o.KeepAliveInterval = options.KeepAliveInterval;
            o.MaxMessageSizeBytes = options.MaxMessageSizeBytes;
            o.WriteIndentedJson = options.WriteIndentedJson;
        });

        // Add CORS services if enabled
        if (options.EnableCors)
        {
            _ = services.AddCors();
        }

        // WebSocket services
        services.TryAddSingleton<IWebSocketConnectionManager, WebSocketConnectionManager>();
        services.TryAddScoped<IMessageWebSocketHandler>();

        // SSE services
        services.TryAddScoped<IMessageSseStreamer>();

        return services;
    }
}
