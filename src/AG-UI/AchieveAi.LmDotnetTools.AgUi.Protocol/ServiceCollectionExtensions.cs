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
        Action<AgUiMiddlewareOptions>? configureOptions = null
    )
    {
        // Configure options
        _ = configureOptions != null ? services.Configure(configureOptions) : services.Configure<AgUiMiddlewareOptions>(options => { });

        // Register core services
        _ = services.AddSingleton<IEventPublisher, ChannelEventPublisher>();
        _ = services.AddScoped<IMessageConverter, MessageToAgUiConverter>(); // Scoped for instance state
        _ = services.AddSingleton<IToolCallTracker, ToolCallTracker>();

        // Register the middleware as Scoped since it depends on scoped IMessageConverter
        _ = services.AddScoped<AgUiStreamingMiddleware>();

        return services;
    }
}
