using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;
using System.Text;

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
    public FunctionCallMiddleware BuildMiddleware(
        string? name = null,
        ILogger<FunctionCallMiddleware>? logger = null,
        IToolResultCallback? resultCallback = null)
    {
        var (contracts, handlers) = Build();
        return new FunctionCallMiddleware(
            contracts,
            handlers,
            name,
            logger: logger,
            resultCallback: resultCallback);
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
        GenerateFunctionsSection(sb, resolvedFunctions);

        return sb.ToString();
    }

    private void GenerateSummarySection(StringBuilder sb, Dictionary<string, FunctionDescriptor> resolvedFunctions)
    {
        var totalFunctions = resolvedFunctions.Count;
        var totalProviders = resolvedFunctions.Values
            .Select(f => f.ProviderName)
            .Distinct()
            .Count();

        sb.AppendLine("## Summary");
        sb.AppendLine($"- **Total Functions:** {totalFunctions}");
        sb.AppendLine($"- **Total Providers:** {totalProviders}");
        sb.AppendLine($"- **Conflict Resolution:** {_conflictResolution}");
        sb.AppendLine();
    }

    private void GenerateProvidersSection(StringBuilder sb, Dictionary<string, FunctionDescriptor> resolvedFunctions)
    {
        sb.AppendLine("## Providers");

        var providerStats = resolvedFunctions.Values
            .GroupBy(f => f.ProviderName)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Priority = _providers.FirstOrDefault(p => p.ProviderName == g.Key)?.Priority ?? -1
            })
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Name);

        foreach (var provider in providerStats)
        {
            var priorityText = provider.Priority >= 0 ? $" (Priority: {provider.Priority})" : "";
            sb.AppendLine($"- **{provider.Name}**{priorityText}: {provider.Count} function{(provider.Count == 1 ? "" : "s")}");
        }
        sb.AppendLine();
    }

    private void GenerateFunctionsSection(StringBuilder sb, Dictionary<string, FunctionDescriptor> resolvedFunctions)
    {
        sb.AppendLine("## Functions");
        sb.AppendLine();

        var sortedFunctions = resolvedFunctions.Values
            .OrderBy(f => f.ProviderName)
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
                var defaultText = param.DefaultValue != null ? $", default: {param.DefaultValue}" : "";

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
        if (function.Contract.ReturnType != null || !string.IsNullOrWhiteSpace(function.Contract.ReturnDescription))
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
        if (parameterType == null) return "unknown";

        if (parameterType is JsonSchemaObject schema)
        {
            return FormatJsonSchemaType(schema);
        }

        return $"`{parameterType.GetType().Name}`";
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
            if (schema.Minimum.HasValue) constraints.Add($"min: {schema.Minimum}");
            if (schema.Maximum.HasValue) constraints.Add($"max: {schema.Maximum}");
            if (schema.MinItems.HasValue) constraints.Add($"minItems: {schema.MinItems}");

            var constraintText = constraints.Any() ? $" ({string.Join(", ", constraints)})" : "";
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