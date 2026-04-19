namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tools;

/// <summary>
/// Decides which tools are permitted for a Copilot session. Copilot does not support
/// MCP server bridging so only the built-in tool allowlist and registered dynamic
/// tool names are evaluated.
/// </summary>
public sealed class CopilotToolPolicyEngine
{
    private readonly HashSet<string> _dynamicToolNames;
    private readonly HashSet<string>? _enabledTools;

    public CopilotToolPolicyEngine(
        IEnumerable<string>? dynamicToolNames = null,
        IEnumerable<string>? enabledTools = null)
    {
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

    public bool IsDynamicToolAllowed(string? toolName)
    {
        return !string.IsNullOrWhiteSpace(toolName)
            && _dynamicToolNames.Contains(toolName)
            && (_enabledTools == null || _enabledTools.Contains(toolName));
    }
}
