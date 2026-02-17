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
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return _enabledTools == null || _enabledTools.Contains(toolName);
    }

    public bool IsMcpToolAllowed(string? serverName, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        if (!_mcpServers.TryGetValue(serverName, out var server))
        {
            return false;
        }

        if (server.Enabled == false)
        {
            return false;
        }

        if (_enabledTools != null && !_enabledTools.Contains(toolName))
        {
            return false;
        }

        if (server.EnabledTools is { Count: > 0 }
            && !server.EnabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (server.DisabledTools is { Count: > 0 }
            && server.DisabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public bool IsDynamicToolAllowed(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        if (!_dynamicToolNames.Contains(toolName))
        {
            return false;
        }

        return _enabledTools == null || _enabledTools.Contains(toolName);
    }
}
