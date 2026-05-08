using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tools;

public sealed class CodexToolPolicyEngine
{
    private readonly IReadOnlyDictionary<string, CodexMcpServerConfig> _mcpServers;
    private readonly HashSet<string> _dynamicToolNames;
    private readonly HashSet<string>? _enabledTools;

    public CodexToolPolicyEngine(
        IReadOnlyDictionary<string, CodexMcpServerConfig>? mcpServers = null,
        IEnumerable<string>? dynamicToolNames = null,
        IEnumerable<string>? enabledTools = null)
    {
        _mcpServers = mcpServers ?? new Dictionary<string, CodexMcpServerConfig>(StringComparer.OrdinalIgnoreCase);
        _dynamicToolNames = new HashSet<string>(
            (dynamicToolNames ?? []).Where(static x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);
        _enabledTools = enabledTools == null
            ? null
            : new HashSet<string>(
                enabledTools.Where(static x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);
    }

    public bool IsBuiltInAllowed(string toolName)
    {
        return !string.IsNullOrWhiteSpace(toolName) && (_enabledTools == null || _enabledTools.Contains(toolName));
    }

    public bool IsMcpToolAllowed(string? serverName, string? toolName)
    {
        return !string.IsNullOrWhiteSpace(serverName)
            && !string.IsNullOrWhiteSpace(toolName)
            && _mcpServers.TryGetValue(serverName, out var server)
            && server.Enabled != false
            && (_enabledTools == null || _enabledTools.Contains(toolName))
            && (server.EnabledTools is not { Count: > 0 }
                || server.EnabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            && (server.DisabledTools is not { Count: > 0 }
                || !server.DisabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase));
    }

    public bool IsDynamicToolAllowed(string? toolName)
    {
        return !string.IsNullOrWhiteSpace(toolName) && _dynamicToolNames.Contains(toolName) && (_enabledTools == null || _enabledTools.Contains(toolName));
    }
}
