using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Builder for combining functions from multiple sources with conflict resolution.
///
/// IMPORTANT: This class is NOT thread-safe. It is designed to be used in a single-threaded
/// context during application initialization. The registry should be built once during startup
/// and the resulting function collections should be treated as read-only.
///
/// Typical usage pattern:
/// 1. Create a FunctionRegistry instance during initialization
/// 2. Configure it with providers and settings
/// 3. Call Build() once to generate the final function collections
/// 4. Use the built collections (which are immutable) throughout the application lifetime
///
/// Do not modify the registry after calling Build(), and do not share a FunctionRegistry
/// instance across multiple threads during configuration.
/// </summary>
public class FunctionRegistry
    : IFunctionRegistryBuilder,
        IFunctionRegistryWithProviders,
        IConfiguredFunctionRegistry
{
    private readonly List<IFunctionProvider> _providers = [];
    private readonly Dictionary<string, FunctionDescriptor> _explicitFunctions = [];
    private ConflictResolution _conflictResolution = ConflictResolution.Throw;
    private Func<string, IEnumerable<FunctionDescriptor>, FunctionDescriptor>? _conflictHandler;
    private FunctionFilterConfig? _filterConfig;
    private ILogger? _logger;

    /// <summary>
    /// Add functions from a provider (MCP, Natural, etc.)
    /// </summary>
    public FunctionRegistry AddProvider(IFunctionProvider provider)
    {
        _providers.Add(provider);
        return this;
    }

    /// <summary>
    /// Add functions from a provider (MCP, Natural, etc.) - Explicit interface implementation
    /// </summary>
    IFunctionRegistryWithProviders IFunctionRegistryBuilder.AddProvider(IFunctionProvider provider)
    {
        return AddProvider(provider);
    }

    /// <summary>
    /// Add a single function explicitly
    /// </summary>
    public FunctionRegistry AddFunction(
        FunctionContract contract,
        Func<string, Task<string>> handler,
        string? providerName = null
    )
    {
        var descriptor = new FunctionDescriptor
        {
            Contract = contract,
            Handler = handler,
            ProviderName = providerName ?? "Explicit",
        };
        _explicitFunctions[descriptor.Key] = descriptor;
        return this;
    }

    /// <summary>
    /// Add a single function explicitly - Explicit interface implementation
    /// </summary>
    IFunctionRegistryBuilder IFunctionRegistryBuilder.AddFunction(
        FunctionContract contract,
        Func<string, Task<string>> handler,
        string? providerName
    )
    {
        return AddFunction(contract, handler, providerName);
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
    /// Set conflict resolution strategy - Explicit interface implementation
    /// </summary>
    IFunctionRegistryWithProviders IFunctionRegistryWithProviders.WithConflictResolution(
        ConflictResolution strategy
    )
    {
        return WithConflictResolution(strategy);
    }

    /// <summary>
    /// Set custom conflict resolution handler
    /// </summary>
    public FunctionRegistry WithConflictHandler(
        Func<string, IEnumerable<FunctionDescriptor>, FunctionDescriptor> handler
    )
    {
        _conflictHandler = handler;
        return this;
    }

    /// <summary>
    /// Set custom conflict resolution handler - Explicit interface implementation
    /// </summary>
    IFunctionRegistryWithProviders IFunctionRegistryWithProviders.WithConflictHandler(
        Func<string, IEnumerable<FunctionDescriptor>, FunctionDescriptor> handler
    )
    {
        return WithConflictHandler(handler);
    }

    /// <summary>
    /// Configure function filtering for all providers
    /// </summary>
    public FunctionRegistry WithFilterConfig(FunctionFilterConfig? filterConfig)
    {
        _filterConfig = filterConfig;
        return this;
    }

    /// <summary>
    /// Configure function filtering for all providers - Explicit interface implementation
    /// </summary>
    IConfiguredFunctionRegistry IFunctionRegistryWithProviders.WithFilterConfig(
        FunctionFilterConfig? filterConfig
    )
    {
        return WithFilterConfig(filterConfig);
    }

    /// <summary>
    /// Set logger for debugging
    /// </summary>
    public FunctionRegistry WithLogger(ILogger? logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Set logger for debugging - Explicit interface implementation
    /// </summary>
    IFunctionRegistryBuilder IFunctionRegistryBuilder.WithLogger(ILogger? logger)
    {
        return WithLogger(logger);
    }

    /// <summary>
    /// Get all registered providers (for inspection/debugging)
    /// </summary>
    public IReadOnlyList<IFunctionProvider> GetProviders()
    {
        return _providers.AsReadOnly();
    }

    /// <summary>
    /// Proceeds to build without additional filtering configuration.
    /// </summary>
    /// <returns>A configured registry ready for building</returns>
    public IConfiguredFunctionRegistry Configure()
    {
        return this;
    }

    /// <summary>
    /// Proceeds to build without additional filtering configuration - Explicit interface implementation
    /// </summary>
    IConfiguredFunctionRegistry IFunctionRegistryWithProviders.Configure()
    {
        return Configure();
    }

    /// <summary>
    /// Validates the current configuration and returns any issues found.
    /// </summary>
    /// <returns>A collection of validation issues, empty if configuration is valid</returns>
    public IEnumerable<string> ValidateConfiguration()
    {
        var issues = new List<string>();

        // Validate filter configuration if present
        if (_filterConfig != null)
        {
            if (_filterConfig.ProviderConfigs != null)
            {
                foreach (var kvp in _filterConfig.ProviderConfigs)
                {
                    var providerName = kvp.Key;
                    var config = kvp.Value;

                    // Validate custom prefix
                    if (config.CustomPrefix != null)
                    {
                        if (!FunctionNameValidator.IsValidPrefix(config.CustomPrefix))
                        {
                            issues.Add(
                                $"Provider '{providerName}': {FunctionNameValidator.GetPrefixValidationError(config.CustomPrefix)}"
                            );
                        }
                    }
                }
            }
        }

        // Check for potential naming conflicts with prefixes
        if (_filterConfig?.UsePrefixOnlyForCollisions == false)
        {
            // When prefixing all functions, validate that provider names are valid prefixes
            foreach (var provider in _providers)
            {
                if (!FunctionNameValidator.IsValidPrefix(provider.ProviderName))
                {
                    issues.Add(
                        $"Provider name '{provider.ProviderName}' is not a valid prefix: {FunctionNameValidator.GetPrefixValidationError(provider.ProviderName)}"
                    );
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Build the final function collections for FunctionCallMiddleware
    /// </summary>
    public (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) Build()
    {
        var logger = _logger ?? NullLogger.Instance;
        logger.LogDebug(
            "Building function registry with {ProviderCount} providers",
            _providers.Count
        );

        // Step 1: Collect all functions from providers
        var allDescriptors = new List<FunctionDescriptor>();

        // Collect functions from all providers (sorted by priority)
        foreach (var provider in _providers.OrderBy(p => p.Priority))
        {
            var providerFunctions = provider.GetFunctions().ToList();
            logger.LogDebug(
                "Provider {ProviderName} contributed {FunctionCount} functions",
                provider.ProviderName,
                providerFunctions.Count
            );
            allDescriptors.AddRange(providerFunctions);
        }

        // Add explicit functions
        allDescriptors.AddRange(_explicitFunctions.Values);
        logger.LogDebug("Added {ExplicitCount} explicit functions", _explicitFunctions.Count);

        // Step 2: Apply filtering if configured
        if (_filterConfig?.EnableFiltering == true)
        {
            logger.LogInformation("Applying function filtering with configuration");
            var filter = new FunctionFilter(_filterConfig, logger);
            allDescriptors = filter.FilterFunctions(allDescriptors).ToList();
        }

        // Step 3: Group functions by key for conflict resolution
        var functionsByKey = new Dictionary<string, List<FunctionDescriptor>>();
        foreach (var descriptor in allDescriptors)
        {
            if (!functionsByKey.TryGetValue(descriptor.Key, out var value))
            {
                value = ([]);
                functionsByKey[descriptor.Key] = value;
            }

            value.Add(descriptor);
        }

        // Step 4: Resolve conflicts
        var resolvedFunctions = new List<FunctionDescriptor>();
        foreach (var kvp in functionsByKey)
        {
            FunctionDescriptor resolved;
            if (kvp.Value.Count == 1)
            {
                resolved = kvp.Value.First();
            }
            else
            {
                resolved = ResolveConflict(kvp.Key, kvp.Value);
            }
            resolvedFunctions.Add(resolved);
        }

        // Step 5: Detect and resolve collisions (after conflict resolution)
        var collisionDetector = new FunctionCollisionDetector(logger);
        var namingMap = collisionDetector.DetectAndResolveCollisions(
            resolvedFunctions,
            _filterConfig
        );

        // Step 6: Build final collections
        var finalContracts = new List<FunctionContract>();
        var finalHandlers = new Dictionary<string, Func<string, Task<string>>>();

        foreach (var resolved in resolvedFunctions)
        {
            // Get the registered name from naming map
            var registeredName = namingMap.TryGetValue(resolved.Key, out var name)
                ? name
                : resolved.Contract.Name;

            // Create contract with registered name if different
            if (registeredName != resolved.Contract.Name)
            {
                // Create a new contract with the updated name
                var contract = new FunctionContract
                {
                    Name = registeredName,
                    Description = resolved.Contract.Description,
                    Namespace = resolved.Contract.Namespace,
                    ClassName = resolved.Contract.ClassName,
                    Parameters = resolved.Contract.Parameters,
                    ReturnType = resolved.Contract.ReturnType,
                    ReturnDescription = resolved.Contract.ReturnDescription,
                };
                finalContracts.Add(contract);
            }
            else
            {
                finalContracts.Add(resolved.Contract);
            }

            finalHandlers[registeredName] = resolved.Handler;
        }

        logger.LogInformation(
            "Function registry built: {ContractCount} functions registered",
            finalContracts.Count
        );

        return (finalContracts, finalHandlers);
    }

    /// <summary>
    /// Build and create FunctionCallMiddleware directly
    /// </summary>
    public FunctionCallMiddleware BuildMiddleware(
        string? name = null,
        ILogger<FunctionCallMiddleware>? logger = null,
        IToolResultCallback? resultCallback = null
    )
    {
        var (contracts, handlers) = Build();
        return new FunctionCallMiddleware(
            contracts,
            handlers,
            name,
            logger: logger,
            resultCallback: resultCallback
        );
    }

    private FunctionDescriptor ResolveConflict(string key, List<FunctionDescriptor> candidates)
    {
        // Explicit functions always take precedence over provider functions
        var explicitFunction = candidates.FirstOrDefault(c => c.ProviderName == "Explicit");
        if (explicitFunction != null)
        {
            return explicitFunction;
        }

        // Custom handler takes precedence
        return _conflictHandler != null
            ? _conflictHandler(key, candidates)
            : _conflictResolution switch
            {
                ConflictResolution.TakeFirst => candidates.First(),
                ConflictResolution.TakeLast => candidates.Last(),
                ConflictResolution.PreferMcp => candidates.FirstOrDefault(IsMcpProvider)
                    ?? candidates.First(),
                ConflictResolution.PreferNatural => candidates.FirstOrDefault(IsNaturalProvider)
                    ?? candidates.First(),
                ConflictResolution.RequireExplicit => throw new InvalidOperationException(
                    $"Function '{key}' has conflicts from multiple providers. "
                        + $"Providers: {string.Join(", ", candidates.Select(c => c.ProviderName))}. "
                        + $"Use WithConflictHandler() to resolve explicitly."
                ),
                ConflictResolution.Throw => throw new InvalidOperationException(
                    $"Function '{key}' is defined by multiple providers: {string.Join(", ", candidates.Select(c => c.ProviderName))}"
                ),
                _ => throw new ArgumentOutOfRangeException(),
            };
    }

    private static bool IsMcpProvider(FunctionDescriptor descriptor) =>
        descriptor.Contract.ClassName != null; // MCP functions have class names

    private static bool IsNaturalProvider(FunctionDescriptor descriptor) =>
        descriptor.Contract.ClassName == null; // Natural functions typically don't

    /// <summary>
    /// Generate markdown documentation for all registered functions and providers
    /// </summary>
    /// <returns>Markdown-formatted string containing comprehensive function documentation</returns>
    public string GetMarkdownDocumentation()
    {
        var sb = new StringBuilder();

        // Collect all functions (similar to Build method)
        var allFunctions = new Dictionary<string, List<FunctionDescriptor>>();

        // Collect functions from all providers (sorted by priority)
        foreach (var provider in _providers.OrderBy(p => p.Priority))
        {
            foreach (var function in provider.GetFunctions())
            {
                if (!allFunctions.TryGetValue(function.Key, out var value))
                {
                    value = ([]);
                    allFunctions[function.Key] = value;
                }

                value.Add(function);
            }
        }

        // Add explicit functions (they take precedence)
        foreach (var kvp in _explicitFunctions)
        {
            allFunctions[kvp.Key] = [kvp.Value];
        }

        // Resolve conflicts to get final function set
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

        // Generate markdown documentation
        sb.AppendLine("# Function Registry Documentation");
        sb.AppendLine();

        // Summary section
        GenerateSummarySection(sb, resolvedFunctions);

        // Providers section
        GenerateProvidersSection(sb, resolvedFunctions);

        // Functions section
        FunctionRegistry.GenerateFunctionsSection(sb, resolvedFunctions);

        return sb.ToString();
    }

    private void GenerateSummarySection(
        StringBuilder sb,
        Dictionary<string, FunctionDescriptor> resolvedFunctions
    )
    {
        var totalFunctions = resolvedFunctions.Count;
        var totalProviders = resolvedFunctions
            .Values.Select(f => f.ProviderName)
            .Distinct()
            .Count();

        sb.AppendLine("## Summary");
        sb.AppendLine($"- **Total Functions:** {totalFunctions}");
        sb.AppendLine($"- **Total Providers:** {totalProviders}");
        sb.AppendLine($"- **Conflict Resolution:** {_conflictResolution}");
        sb.AppendLine();
    }

    private void GenerateProvidersSection(
        StringBuilder sb,
        Dictionary<string, FunctionDescriptor> resolvedFunctions
    )
    {
        sb.AppendLine("## Providers");

        var providerStats = resolvedFunctions
            .Values.GroupBy(f => f.ProviderName)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Priority = _providers.FirstOrDefault(p => p.ProviderName == g.Key)?.Priority ?? -1,
            })
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Name);

        foreach (var provider in providerStats)
        {
            var priorityText = provider.Priority >= 0 ? $" (Priority: {provider.Priority})" : "";
            sb.AppendLine(
                $"- **{provider.Name}**{priorityText}: {provider.Count} function{(provider.Count == 1 ? "" : "s")}"
            );
        }
        sb.AppendLine();
    }

    private static void GenerateFunctionsSection(
        StringBuilder sb,
        Dictionary<string, FunctionDescriptor> resolvedFunctions
    )
    {
        sb.AppendLine("## Functions");
        sb.AppendLine();

        var sortedFunctions = resolvedFunctions
            .Values.OrderBy(f => f.ProviderName)
            .ThenBy(f => f.DisplayName);

        foreach (var function in sortedFunctions)
        {
            GenerateFunctionDocumentation(sb, function);
        }
    }

    private static void GenerateFunctionDocumentation(StringBuilder sb, FunctionDescriptor function)
    {
        // Function header
        sb.AppendLine($"### {function.DisplayName}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(function.Contract.Description))
        {
            sb.AppendLine(function.Contract.Description);
            sb.AppendLine();
        }

        // Function metadata
        sb.AppendLine("Function details:");
        sb.AppendLine($"- **Provider:** {function.ProviderName}");
        sb.AppendLine($"- **Key:** `{function.Key}`");

        if (!string.IsNullOrWhiteSpace(function.Contract.Namespace))
        {
            sb.AppendLine($"- **Namespace:** {function.Contract.Namespace}");
        }

        sb.AppendLine();

        // Parameters section
        if (function.Contract.Parameters?.Any() == true)
        {
            sb.AppendLine("Parameters:");
            foreach (var param in function.Contract.Parameters)
            {
                var paramType = FormatParameterType(param.ParameterType);
                var requiredText = param.IsRequired ? " (required)" : " (optional)";
                var defaultText =
                    param.DefaultValue != null ? $", default: {param.DefaultValue}" : "";

                sb.AppendLine($"- **{param.Name}** ({paramType}{requiredText}{defaultText})");
                if (!string.IsNullOrWhiteSpace(param.Description))
                {
                    sb.AppendLine($"  {param.Description}");
                }
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Parameters:");
            sb.AppendLine("- *No parameters required*");
            sb.AppendLine();
        }

        // Return section
        if (
            function.Contract.ReturnType != null
            || !string.IsNullOrWhiteSpace(function.Contract.ReturnDescription)
        )
        {
            sb.AppendLine("Returns:");
            if (function.Contract.ReturnType != null)
            {
                sb.AppendLine($"- **Type:** `{function.Contract.ReturnType.Name}`");
            }
            if (!string.IsNullOrWhiteSpace(function.Contract.ReturnDescription))
            {
                sb.AppendLine($"- **Description:** {function.Contract.ReturnDescription}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
    }

    private static string FormatParameterType(object parameterType)
    {
        // Handle JsonSchemaObject formatting
        return parameterType == null
            ? "unknown"
            : parameterType is JsonSchemaObject schema ? FormatJsonSchemaType(schema) : $"`{parameterType.GetType().Name}`";
    }

    private static string FormatJsonSchemaType(JsonSchemaObject schema)
    {
        if (schema.Type.Is<string>())
        {
            var baseType = schema.Type.Get<string>();

            // Handle array types
            if (baseType == "array" && schema.Items != null)
            {
                var itemType = FormatJsonSchemaType(schema.Items);
                return $"`{itemType}[]`";
            }

            // Handle enum types
            if (schema.Enum?.Any() == true)
            {
                var enumValues = string.Join(" \\| ", schema.Enum.Take(3));
                if (schema.Enum.Count > 3)
                {
                    enumValues += " \\| ...";
                }
                return $"`{baseType}` ({enumValues})";
            }

            // Handle basic types with constraints
            var constraints = new List<string>();
            if (schema.Minimum.HasValue)
            {
                constraints.Add($"min: {schema.Minimum}");
            }

            if (schema.Maximum.HasValue)
            {
                constraints.Add($"max: {schema.Maximum}");
            }

            if (schema.MinItems.HasValue)
            {
                constraints.Add($"minItems: {schema.MinItems}");
            }

            var constraintText = constraints.Count != 0 ? $" ({string.Join(", ", constraints)})" : "";
            return $"`{baseType}`{constraintText}";
        }

        if (schema.Type.Is<IReadOnlyList<string>>())
        {
            var typeList = schema.Type.Get<IReadOnlyList<string>>();
            if (typeList?.Any() == true)
            {
                var types = string.Join(" \\| ", typeList);
                return $"`{types}`";
            }
        }

        return "`object`";
    }
}
