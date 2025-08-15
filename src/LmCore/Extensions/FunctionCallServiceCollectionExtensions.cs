using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace AchieveAi.LmDotnetTools.LmCore.Extensions;

/// <summary>
/// Extension methods for registering function call services
/// </summary>
public static class FunctionCallServiceCollectionExtensions
{
    /// <summary>
    /// Register core function handling services
    /// </summary>
    public static IServiceCollection AddFunctionCallServices(this IServiceCollection services)
    {
        services.AddSingleton<IFunctionProviderRegistry, FunctionProviderRegistry>();
        services.AddSingleton<IFunctionCallMiddlewareFactory, FunctionCallMiddlewareFactory>();
        return services;
    }

    /// <summary>
    /// Register a function provider type
    /// </summary>
    public static IServiceCollection AddFunctionProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IFunctionProvider
    {
        services.AddSingleton<TProvider>();
        services.AddSingleton<IFunctionProvider, TProvider>(sp => sp.GetRequiredService<TProvider>());
        return services;
    }

    /// <summary>
    /// Register function provider factory
    /// </summary>
    public static IServiceCollection AddFunctionProvider(this IServiceCollection services,
        Func<IServiceProvider, IFunctionProvider> factory)
    {
        services.AddSingleton<IFunctionProvider>(factory);
        return services;
    }

    /// <summary>
    /// Configure function providers during startup
    /// </summary>
    public static IServiceCollection ConfigureFunctionProviders(this IServiceCollection services,
        Action<IFunctionProviderRegistry, IServiceProvider> configure)
    {
        services.AddSingleton<Action<IFunctionProviderRegistry, IServiceProvider>>(configure);
        return services;
    }
}