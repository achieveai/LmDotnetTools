using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Configuration;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.WebSockets;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Database;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Extensions;

/// <summary>
/// Extension methods for registering AG-UI services in the dependency injection container
/// </summary>
public static class AgUiServiceCollectionExtensions
{
    /// <summary>
    /// Adds AG-UI services to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Optional configuration delegate</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddAgUi(
        this IServiceCollection services,
        Action<AgUiOptions>? configureOptions = null
    )
    {
        // Configure options
        _ = configureOptions != null ? services.Configure(configureOptions) : services.Configure<AgUiOptions>(_ => { });

        // Validate options on startup
        _ = services
            .AddOptions<AgUiOptions>()
            .Validate(
                options =>
                {
                    try
                    {
                        options.Validate();
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                },
                "AG-UI options validation failed"
            );

        // Register core AG-UI Protocol services
        RegisterProtocolServices(services);

        // Register ASP.NET Core specific services
        RegisterAspNetCoreServices(services);

        // Conditionally register persistence services
        RegisterPersistenceServices(services);

        return services;
    }

    /// <summary>
    /// Registers core AG-UI Protocol layer services
    /// </summary>
    private static void RegisterProtocolServices(IServiceCollection services)
    {
        // Event publisher (singleton for all sessions)
        services.TryAddSingleton<IEventPublisher>(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgUiOptions>>().Value;
            var logger = provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ChannelEventPublisher>>();
            return new ChannelEventPublisher(logger, options.EventBufferSize);
        });

        // Message converter
        services.TryAddSingleton<IMessageConverter>(provider =>
        {
            var toolCallTracker = provider.GetRequiredService<Protocol.Tracking.IToolCallTracker>();
            var logger =
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Protocol.Converters.MessageToAgUiConverter>>();
            return new Protocol.Converters.MessageToAgUiConverter(toolCallTracker, logger);
        });

        // Tool call tracker
        services.TryAddSingleton<Protocol.Tracking.IToolCallTracker, Protocol.Tracking.ToolCallTracker>();

        // LmCore to AG-UI converter
        services.TryAddSingleton<ILmCoreToAgUiConverter, LmCoreToAgUiConverter>();

        // AG-UI to LmCore converter
        services.TryAddSingleton<IAgUiToLmCoreConverter, AgUiToLmCoreConverter>();
    }

    /// <summary>
    /// Registers ASP.NET Core specific services
    /// </summary>
    private static void RegisterAspNetCoreServices(IServiceCollection services)
    {
        // WebSocket connection manager (singleton)
        services.TryAddSingleton<IWebSocketConnectionManager>(provider =>
        {
            var logger =
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebSocketConnectionManager>>();
            return new WebSocketConnectionManager(logger);
        });

        // WebSocket handler (scoped per connection)
        services.TryAddScoped<AgUiWebSocketHandler>();
    }

    /// <summary>
    /// Conditionally registers persistence services if enabled in options
    /// </summary>
    private static void RegisterPersistenceServices(IServiceCollection services)
    {
#pragma warning disable CS8621 // Nullability of reference types in return type doesn't match the target delegate
#pragma warning disable CS8634 // The type cannot be used as type parameter. Nullability doesn't match 'class' constraint
        // Register persistence services only if enabled
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgUiOptions>>().Value;
            if (!options.EnablePersistence)
            {
                return null as IDbConnectionFactory;
            }

            var connectionString = $"Data Source={options.DatabasePath}";
            var logger = provider.GetService<Microsoft.Extensions.Logging.ILogger<SqliteConnectionFactory>>();
            return new SqliteConnectionFactory(connectionString, options.MaxDatabaseConnections, logger);
        });

        services.TryAddSingleton(provider =>
        {
            var factory = provider.GetService<IDbConnectionFactory>();
            if (factory == null)
            {
                return null as ISessionRepository;
            }

            var logger = provider.GetService<Microsoft.Extensions.Logging.ILogger<SessionRepository>>();
            return new SessionRepository(factory, logger);
        });

        services.TryAddSingleton(provider =>
        {
            var factory = provider.GetService<IDbConnectionFactory>();
            if (factory == null)
            {
                return null as IMessageRepository;
            }

            var logger = provider.GetService<Microsoft.Extensions.Logging.ILogger<MessageRepository>>();
            return new MessageRepository(factory, logger);
        });

        services.TryAddSingleton(provider =>
        {
            var factory = provider.GetService<IDbConnectionFactory>();
            if (factory == null)
            {
                return null as IEventRepository;
            }

            var logger = provider.GetService<Microsoft.Extensions.Logging.ILogger<EventRepository>>();
            return new EventRepository(factory, logger);
        });

        services.TryAddSingleton(provider =>
        {
            var factory = provider.GetService<IDbConnectionFactory>();
            if (factory == null)
            {
                return null;
            }

            var logger = provider.GetService<Microsoft.Extensions.Logging.ILogger<DatabaseInitializer>>();
            return new DatabaseInitializer(factory, logger);
        });
#pragma warning restore CS8634
#pragma warning restore CS8621
    }
}
