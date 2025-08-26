namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Default implementation of IFunctionCallMiddlewareFactory
/// </summary>
public class FunctionCallMiddlewareFactory : IFunctionCallMiddlewareFactory
{
    private readonly IFunctionProviderRegistry _registry;

    public FunctionCallMiddlewareFactory(IFunctionProviderRegistry registry)
    {
        _registry = registry;
    }

    public FunctionCallMiddleware Create(
        string? name = null,
        Action<FunctionRegistry>? configure = null
    )
    {
        return Create(null, name, configure);
    }

    public FunctionCallMiddleware Create(
        IToolResultCallback? resultCallback,
        string? name = null,
        Action<FunctionRegistry>? configure = null
    )
    {
        var registry = CreateRegistry();
        configure?.Invoke(registry);
        var middleware = registry.BuildMiddleware(name, null, resultCallback);
        return middleware;
    }

    public FunctionRegistry CreateRegistry()
    {
        var registry = new FunctionRegistry();

        // Add all registered providers
        foreach (var provider in _registry.GetProviders())
        {
            registry.AddProvider(provider);
        }

        return registry;
    }
}
