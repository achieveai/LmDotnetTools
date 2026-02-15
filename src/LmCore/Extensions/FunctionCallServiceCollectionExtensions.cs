using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace AchieveAi.LmDotnetTools.LmCore.Extensions;

/// <summary>
///     Extension methods for registering function call services
/// </summary>
public static class FunctionCallServiceCollectionExtensions
{
    /// <summary>
    ///     Register core function handling services
    /// </summary>
    public static IServiceCollection AddFunctionCallServices(this IServiceCollection services)
    {
        _ = services.AddSingleton<IFunctionProviderRegistry, FunctionProviderRegistry>();
        _ = services.AddSingleton<IFunctionCallMiddlewareFactory, FunctionCallMiddlewareFactory>();
        return services;
    }

    /// <summary>
    ///     Register a function provider type
    /// </summary>
    public static IServiceCollection AddFunctionProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, IFunctionProvider
    {
        _ = services.AddSingleton<TProvider>();
        _ = services.AddSingleton<IFunctionProvider, TProvider>(sp => sp.GetRequiredService<TProvider>());
        return services;
    }

    /// <summary>
    ///     Register function provider factory
    /// </summary>
    public static IServiceCollection AddFunctionProvider(
        this IServiceCollection services,
        Func<IServiceProvider, IFunctionProvider> factory
    )
    {
        _ = services.AddSingleton(factory);
        return services;
    }

    /// <summary>
    ///     Configure function providers during startup
    /// </summary>
    public static IServiceCollection ConfigureFunctionProviders(
        this IServiceCollection services,
        Action<IFunctionProviderRegistry, IServiceProvider> configure
    )
    {
        _ = services.AddSingleton(configure);
        return services;
    }
}
