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

    /// <summary>
    /// Claude-plugin marketplace directories to load, as one or more comma-separated
    /// <c>alias=path</c> entries (e.g. <c>"claude_plugins=B:\sources\claude_plugins"</c>).
    /// Becomes <c>PLUGINS_DIRS</c> when the gateway is spawned. Each directory may hold a
    /// <c>.claude-plugin/marketplace.json</c> catalog (or be scanned for plugin subdirectories
    /// containing <c>.claude-plugin/plugin.json</c>, <c>.mcp.json</c>, <c>SKILL.md</c>, or a
    /// <c>skills/</c> subdir); the gateway surfaces each plugin's skills and <c>.mcp.json</c>
    /// MCP servers to the sandbox.
    /// </summary>
    public string? PluginsDirs { get; set; }

    /// <summary>
    /// Absolute path to <c>egress-proxy.exe</c>. When this and <see cref="CaCertPath"/>/<see cref="CaKeyPath"/>
    /// are set, the app adopt-or-spawns the egress proxy so sandbox outbound traffic is policy-enforced and
    /// OAuth tokens can be injected (the auth-webhook path). Without it, the gateway tells sandboxes to use a
    /// proxy that isn't running, so external API calls (GitHub/ADO) fail to connect.
    /// </summary>
    public string? EgressProxyExePath { get; set; }

    /// <summary>
    /// <c>host:port</c> the egress proxy listens on (also the gateway's <c>EGRESS_PROXY_URL</c>).
    /// Defaults to the value the gateway expects on the local backend.
    /// </summary>
    public string EgressProxyListen { get; set; } = "127.0.0.1:8090";

    /// <summary>
    /// Host path to the MITM CA certificate the egress proxy presents (becomes the proxy's
    /// <c>CA_CERT_PATH</c> and the gateway's <c>CA_CERT_HOST_PATH</c>, which it exports to sandboxes as
    /// <c>CURL_CA_BUNDLE</c>/<c>SSL_CERT_FILE</c>).
    /// </summary>
    public string? CaCertPath { get; set; }

    /// <summary>Host path to the MITM CA private key (the egress proxy's <c>CA_KEY_PATH</c>).</summary>
    public string? CaKeyPath { get; set; }
}
