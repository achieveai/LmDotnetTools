using Microsoft.Extensions.DependencyInjection;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Default implementation of IFunctionProviderRegistry
/// </summary>
public class FunctionProviderRegistry : IFunctionProviderRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly List<IFunctionProvider> _providers = new();
    private readonly List<Func<IServiceProvider, IFunctionProvider>> _factories = new();
    private readonly List<Type> _providerTypes = new();

    public FunctionProviderRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void RegisterProvider(IFunctionProvider provider)
    {
        _providers.Add(provider);
    }

    public void RegisterProvider<TProvider>()
        where TProvider : class, IFunctionProvider
    {
        _providerTypes.Add(typeof(TProvider));
    }

    public void RegisterProviderFactory(Func<IServiceProvider, IFunctionProvider> factory)
    {
        _factories.Add(factory);
    }

    public IEnumerable<IFunctionProvider> GetProviders()
    {
        var allProviders = new List<IFunctionProvider>();

        // Add directly registered providers
        allProviders.AddRange(_providers);

        // Add providers from factories
        allProviders.AddRange(_factories.Select(f => f(_serviceProvider)));

        // Add providers from types (resolved via DI)
        foreach (var type in _providerTypes)
        {
            if (_serviceProvider.GetService(type) is IFunctionProvider provider)
            {
                allProviders.Add(provider);
            }
        }

        // Sort by priority
        return allProviders.OrderBy(p => p.Priority).ToList();
    }

    public IFunctionProvider? GetProvider(string name)
    {
        return GetProviders().FirstOrDefault(p => p.ProviderName == name);
    }
}
