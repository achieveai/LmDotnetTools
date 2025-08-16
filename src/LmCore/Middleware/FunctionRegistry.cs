using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Builder for combining functions from multiple sources with conflict resolution
/// </summary>
public class FunctionRegistry
{
    private readonly List<IFunctionProvider> _providers = new();
    private readonly Dictionary<string, FunctionDescriptor> _explicitFunctions = new();
    private ConflictResolution _conflictResolution = ConflictResolution.Throw;
    private Func<string, IEnumerable<FunctionDescriptor>, FunctionDescriptor>? _conflictHandler;

    /// <summary>
    /// Add functions from a provider (MCP, Natural, etc.)
    /// </summary>
    public FunctionRegistry AddProvider(IFunctionProvider provider)
    {
        _providers.Add(provider);
        return this;
    }

    /// <summary>
    /// Add a single function explicitly
    /// </summary>
    public FunctionRegistry AddFunction(FunctionContract contract, Func<string, Task<string>> handler, string? providerName = null)
    {
        var descriptor = new FunctionDescriptor 
        { 
            Contract = contract, 
            Handler = handler, 
            ProviderName = providerName ?? "Explicit"
        };
        _explicitFunctions[descriptor.Key] = descriptor;
        return this;
    }

    /// <summary>
    /// Set conflict resolution strategy
    /// </summary>
    public FunctionRegistry WithConflictResolution(ConflictResolution strategy)
    {
        _conflictResolution = strategy;
        return this;
    }

    /// <summary>
    /// Set custom conflict resolution handler
    /// </summary>
    public FunctionRegistry WithConflictHandler(Func<string, IEnumerable<FunctionDescriptor>, FunctionDescriptor> handler)
    {
        _conflictHandler = handler;
        return this;
    }

    /// <summary>
    /// Build the final function collections for FunctionCallMiddleware
    /// </summary>
    public (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) Build()
    {
        var allFunctions = new Dictionary<string, List<FunctionDescriptor>>();

        // Collect functions from all providers (sorted by priority)
        foreach (var provider in _providers.OrderBy(p => p.Priority))
        {
            foreach (var function in provider.GetFunctions())
            {
                if (!allFunctions.ContainsKey(function.Key))
                    allFunctions[function.Key] = new List<FunctionDescriptor>();
                allFunctions[function.Key].Add(function);
            }
        }

        // Add explicit functions (they take precedence)
        foreach (var kvp in _explicitFunctions)
        {
            allFunctions[kvp.Key] = new List<FunctionDescriptor> { kvp.Value };
        }

        // Resolve conflicts
        var resolvedFunctions = new Dictionary<string, FunctionDescriptor>();
        foreach (var kvp in allFunctions)
        {
            if (kvp.Value.Count == 1)
            {
                resolvedFunctions[kvp.Key] = kvp.Value.First();
            }
            else
            {
                resolvedFunctions[kvp.Key] = ResolveConflict(kvp.Key, kvp.Value);
            }
        }

        // Split into contracts and handlers
        var contracts = resolvedFunctions.Values.Select(f => f.Contract);
        var handlers = resolvedFunctions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Handler);

        return (contracts, handlers);
    }

    /// <summary>
    /// Build and create FunctionCallMiddleware directly
    /// </summary>
    public FunctionCallMiddleware BuildMiddleware(string? name = null, IToolResultCallback? resultCallback = null)
    {
        var (contracts, handlers) = Build();
        return new FunctionCallMiddleware(contracts, handlers, name, resultCallback: resultCallback);
    }

    private FunctionDescriptor ResolveConflict(string key, List<FunctionDescriptor> candidates)
    {
        // Custom handler takes precedence
        if (_conflictHandler != null)
            return _conflictHandler(key, candidates);

        return _conflictResolution switch
        {
            ConflictResolution.TakeFirst => candidates.First(),
            ConflictResolution.TakeLast => candidates.Last(),
            ConflictResolution.PreferMcp => candidates.FirstOrDefault(c => IsMcpProvider(c)) ?? candidates.First(),
            ConflictResolution.PreferNatural => candidates.FirstOrDefault(c => IsNaturalProvider(c)) ?? candidates.First(),
            ConflictResolution.RequireExplicit => throw new InvalidOperationException(
                $"Function '{key}' has conflicts from multiple providers. " +
                $"Providers: {string.Join(", ", candidates.Select(c => c.ProviderName))}. " +
                $"Use WithConflictHandler() to resolve explicitly."),
            ConflictResolution.Throw => throw new InvalidOperationException(
                $"Function '{key}' is defined by multiple providers: {string.Join(", ", candidates.Select(c => c.ProviderName))}"),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static bool IsMcpProvider(FunctionDescriptor descriptor) => 
        descriptor.Contract.ClassName != null; // MCP functions have class names

    private static bool IsNaturalProvider(FunctionDescriptor descriptor) => 
        descriptor.Contract.ClassName == null; // Natural functions typically don't
}