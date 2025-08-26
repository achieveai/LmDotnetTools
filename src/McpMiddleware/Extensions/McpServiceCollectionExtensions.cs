using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Extensions;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpMiddleware.Extensions;

/// <summary>
/// Extension methods for registering MCP function providers
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Register MCP function provider for specific assembly
    /// </summary>
    public static IServiceCollection AddMcpFunctions(
        this IServiceCollection services,
        Assembly? assembly = null,
        string? name = null
    )
    {
        return services.AddFunctionProvider(sp => new McpFunctionProvider(assembly, name));
    }

    /// <summary>
    /// Register MCP function provider with automatic discovery using assembly marker type
    /// </summary>
    public static IServiceCollection AddMcpFunctions<TAssemblyMarker>(
        this IServiceCollection services,
        string? name = null
    )
    {
        return services.AddMcpFunctions(typeof(TAssemblyMarker).Assembly, name);
    }

    /// <summary>
    /// Auto-discover and register all MCP assemblies from loaded assemblies
    /// </summary>
    public static IServiceCollection AddMcpFunctionsFromLoadedAssemblies(
        this IServiceCollection services
    )
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(HasMcpTools).ToList();

        foreach (var assembly in assemblies)
        {
            services.AddMcpFunctions(assembly);
        }

        return services;
    }

    /// <summary>
    /// Check if assembly contains MCP tools
    /// </summary>
    private static bool HasMcpTools(Assembly assembly)
    {
        try
        {
            return assembly
                .GetTypes()
                .Any(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);
        }
        catch
        {
            return false; // Handle reflection issues gracefully
        }
    }
}
