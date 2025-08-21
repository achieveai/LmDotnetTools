namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Service for discovering and managing function providers
/// </summary>
public interface IFunctionProviderRegistry
{
    /// <summary>
    /// Register a function provider instance
    /// </summary>
    void RegisterProvider(IFunctionProvider provider);

    /// <summary>
    /// Register a function provider type to be resolved via DI
    /// </summary>
    void RegisterProvider<TProvider>() where TProvider : class, IFunctionProvider;

    /// <summary>
    /// Register a factory for creating function providers
    /// </summary>
    void RegisterProviderFactory(Func<IServiceProvider, IFunctionProvider> factory);

    /// <summary>
    /// Get all registered providers (ordered by priority)
    /// </summary>
    IEnumerable<IFunctionProvider> GetProviders();

    /// <summary>
    /// Get a specific provider by name
    /// </summary>
    IFunctionProvider? GetProvider(string name);
}