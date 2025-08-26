using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

// Legacy aliases for backward compatibility
// These now map to the generalized configuration classes in LmCore

/// <summary>
/// Configuration for MCP tool filtering - now an alias for FunctionFilterConfig
/// </summary>
[Obsolete("Use AchieveAi.LmDotnetTools.LmCore.Configuration.FunctionFilterConfig instead")]
public class McpToolFilterConfig : FunctionFilterConfig
{
    // Inherits all properties from FunctionFilterConfig
}

/// <summary>
/// Configuration for an MCP server - now an alias for ProviderFilterConfig
/// </summary>
[Obsolete("Use AchieveAi.LmDotnetTools.LmCore.Configuration.ProviderFilterConfig instead")]
public class McpServerFilterConfig : ProviderFilterConfig
{
    // Inherits all properties from ProviderFilterConfig
}

/// <summary>
/// Handles tool filtering based on configuration rules
/// Legacy wrapper around FunctionFilter for backward compatibility
/// </summary>
[Obsolete("Use AchieveAi.LmDotnetTools.LmCore.Middleware.FunctionFilter instead")]
public class McpToolFilter
{
    private readonly FunctionFilter _functionFilter;
    private readonly FunctionFilterConfig? _config;

    /// <summary>
    /// Initializes a new instance of the McpToolFilter class
    /// </summary>
    /// <param name="globalConfig">Global tool filtering configuration</param>
    /// <param name="serverConfigs">Per-server configurations</param>
    /// <param name="logger">Optional logger for debugging</param>
    public McpToolFilter(
        McpToolFilterConfig? globalConfig,
        Dictionary<string, McpServerFilterConfig> serverConfigs,
        ILogger? logger = null)
    {
        // Convert MCP-specific config to generalized config
        _config = globalConfig;
        if (_config != null && serverConfigs != null)
        {
            _config.ProviderConfigs = serverConfigs.ToDictionary(
                kvp => kvp.Key,
                kvp => (ProviderFilterConfig)kvp.Value);
        }

        _functionFilter = new FunctionFilter(_config, logger);
    }

    /// <summary>
    /// Determines whether a tool should be filtered out based on configuration
    /// </summary>
    /// <param name="serverId">The ID of the server providing the tool</param>
    /// <param name="originalToolName">The original tool name from the server</param>
    /// <param name="registeredToolName">The registered name (possibly with prefix)</param>
    /// <returns>True if the tool should be filtered out (excluded), false if it should be included</returns>
    public bool ShouldFilterTool(
        string serverId,
        string originalToolName,
        string registeredToolName)
    {
        // Create a temporary descriptor to pass to the generalized filter
        var descriptor = new FunctionDescriptor
        {
            Contract = new AchieveAi.LmDotnetTools.LmCore.Agents.FunctionContract
            {
                Name = originalToolName
            },
            Handler = _ => Task.FromResult(string.Empty), // Dummy handler
            ProviderName = serverId
        };

        return _functionFilter.ShouldFilterFunction(descriptor, registeredToolName);
    }

}