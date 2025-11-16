using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Middleware;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Tracking;
using Microsoft.Extensions.DependencyInjection;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol;

/// <summary>
/// Extension methods for configuring AG-UI protocol services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds AG-UI protocol services to the DI container
    /// </summary>
    public static IServiceCollection AddAgUiProtocol(
        this IServiceCollection services,
        Action<AgUiMiddlewareOptions>? configureOptions = null)
    {
        // Configure options
        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.Configure<AgUiMiddlewareOptions>(options => { });
        }

        // Register core services
        services.AddSingleton<IEventPublisher, ChannelEventPublisher>();
        services.AddSingleton<IMessageConverter, MessageToAgUiConverter>();
        services.AddSingleton<IToolCallTracker, ToolCallTracker>();

        // Register the middleware
        services.AddSingleton<AgUiStreamingMiddleware>();

        return services;
    }
}
