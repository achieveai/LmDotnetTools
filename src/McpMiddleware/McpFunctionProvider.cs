using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
///     MCP-specific function provider that implements core interface
/// </summary>
public class McpFunctionProvider : IFunctionProvider
{
    private readonly Assembly _assembly;

    public McpFunctionProvider(Assembly? assembly = null, string? name = null)
    {
        _assembly = assembly ?? Assembly.GetCallingAssembly();
        ProviderName = name ?? $"MCP-{_assembly.GetName().Name}";
    }

    public string ProviderName { get; }

    /// <summary>
    ///     MCP functions have medium priority (100)
    /// </summary>
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var (contracts, handlers) = McpFunctionCallExtensions.CreateFunctionCallComponentsFromAssembly(_assembly);

        return contracts.Select(contract => new FunctionDescriptor
        {
            Contract = contract,
            Handler = handlers[contract.ClassName != null ? $"{contract.ClassName}-{contract.Name}" : contract.Name],
            ProviderName = ProviderName,
        });
    }
}
