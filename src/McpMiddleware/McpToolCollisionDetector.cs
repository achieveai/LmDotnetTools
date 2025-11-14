using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Handles collision detection and name resolution for MCP tools across multiple servers
/// Legacy wrapper around FunctionCollisionDetector for backward compatibility
/// </summary>
[Obsolete("Use AchieveAi.LmDotnetTools.LmCore.Middleware.FunctionCollisionDetector instead")]
public class McpToolCollisionDetector
{
    private readonly FunctionCollisionDetector _functionCollisionDetector;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the McpToolCollisionDetector class
    /// </summary>
    /// <param name="logger">Optional logger for debugging</param>
    public McpToolCollisionDetector(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _functionCollisionDetector = new FunctionCollisionDetector(logger);
    }

    /// <summary>
    /// Detects name collisions among tools from different servers and resolves them
    /// </summary>
    /// <param name="toolsByServer">Dictionary mapping server IDs to their list of tools</param>
    /// <param name="usePrefixOnlyForCollisions">When true, only apply prefixes to colliding tools; when false, prefix all tools</param>
    /// <returns>Dictionary mapping (serverId, toolName) tuples to their registered names</returns>
    public Dictionary<(string serverId, string toolName), string> DetectAndResolveCollisions(
        Dictionary<string, List<McpClientTool>> toolsByServer,
        bool usePrefixOnlyForCollisions
    )
    {
        // Convert MCP tools to function descriptors
        var descriptors = new List<FunctionDescriptor>();
        var toolToDescriptorMap = new Dictionary<(string serverId, string toolName), FunctionDescriptor>();

        foreach (var (serverId, tools) in toolsByServer)
        {
            foreach (var tool in tools)
            {
                var descriptor = new FunctionDescriptor
                {
                    Contract = new FunctionContract
                    {
                        Name = tool.Name,
                        Description = tool.Description ?? string.Empty,
                    },
                    Handler = _ => System.Threading.Tasks.Task.FromResult(string.Empty), // Dummy handler
                    ProviderName = serverId,
                };

                descriptors.Add(descriptor);
                toolToDescriptorMap[(serverId, tool.Name)] = descriptor;
            }
        }

        // Use the generalized collision detector
        var config = new FunctionFilterConfig { UsePrefixOnlyForCollisions = usePrefixOnlyForCollisions };

        var namingMap = _functionCollisionDetector.DetectAndResolveCollisions(descriptors, config);

        // Convert back to the expected format
        var result = new Dictionary<(string serverId, string toolName), string>();
        foreach (var ((serverId, toolName), descriptor) in toolToDescriptorMap)
        {
            if (namingMap.TryGetValue(descriptor.Key, out var registeredName))
            {
                result[(serverId, toolName)] = registeredName;
            }
        }

        return result;
    }

    /// <summary>
    /// Sanitizes a tool or server name to comply with OpenAI's function name requirements
    /// Delegates to the generalized FunctionCollisionDetector
    /// </summary>
    /// <param name="name">The name to sanitize</param>
    /// <returns>Sanitized name that complies with OpenAI requirements</returns>
    private static string SanitizeToolName(string name)
    {
        return FunctionCollisionDetector.SanitizeName(name);
    }
}
