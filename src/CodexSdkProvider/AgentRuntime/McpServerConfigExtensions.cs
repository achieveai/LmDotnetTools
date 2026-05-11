using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.AgentRuntime;

/// <summary>
///     Projection helpers from the neutral <see cref="McpServerConfig"/> to
///     Codex-specific <see cref="CodexMcpServerConfig"/>.
/// </summary>
public static class McpServerConfigExtensions
{
    /// <summary>
    ///     Maps shared MCP fields (command/args/env/url) onto a Codex-shaped record.
    ///     Codex-only fields (<c>Enabled</c>, <c>EnabledTools</c>, <c>DisabledTools</c>,
    ///     <c>ToolTimeoutSec</c>, <c>StartupTimeoutSec</c>) stay at their defaults.
    /// </summary>
    public static CodexMcpServerConfig ToCodexConfig(this McpServerConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);

        IReadOnlyList<string>? args = source.Args is null
            ? null
            : [.. source.Args];

        IReadOnlyDictionary<string, string>? env = source.Env?
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return new CodexMcpServerConfig
        {
            Url = source.Url,
            Command = source.Command,
            Args = args,
            Env = env,
        };
    }
}
