namespace LmStreaming.Sample.Services;

/// <summary>
/// Strongly-typed configuration for the Rust sandbox MCP gateway integration.
/// Bound from the <c>SandboxGateway</c> configuration section.
/// </summary>
public sealed class SandboxGatewayOptions
{
    /// <summary>
    /// Configuration section name these options are bound from.
    /// </summary>
    public const string SectionName = "SandboxGateway";

    /// <summary>
    /// Base URL of the gateway the app connects to.
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>
    /// Workspace path relative to <see cref="WorkspaceBasePath"/>, mounted as the sandbox
    /// workspace. On the Docker backend this is <c>/workspace</c>; on the local/Windows
    /// backend the workspace is the resolved host directory itself (no <c>/workspace</c> mount).
    /// </summary>
    public string? Workspace { get; set; }

    /// <summary>
    /// Host base directory the gateway resolves the workspace under.
    /// Becomes <c>WORKSPACE_BASE_PATH</c> when the gateway is spawned.
    /// </summary>
    public string? WorkspaceBasePath { get; set; }

    /// <summary>
    /// App id sent in the sandbox-create request.
    /// </summary>
    public string AppId { get; set; } = "lmstreaming-sample";

    /// <summary>
    /// When <c>true</c> and the gateway is not already healthy, spawn it.
    /// </summary>
    public bool AutoSpawn { get; set; } = true;

    /// <summary>
    /// Absolute path to <c>mcp-gateway.exe</c>, used for auto-spawn.
    /// </summary>
    public string? GatewayExePath { get; set; }

    /// <summary>
    /// Absolute path to <c>agent-cli.exe</c>.
    /// Becomes <c>LOCAL_AGENT_CLI_PATH</c> when the gateway is spawned.
    /// </summary>
    public string? AgentCliPath { get; set; }

    /// <summary>
    /// Directory of sandbox skills.
    /// Becomes <c>SKILLS_DIRS</c> when the gateway is spawned (the gateway uses
    /// <c>';'</c>-separated values on Windows).
    /// </summary>
    public string? SkillsDir { get; set; }
}
