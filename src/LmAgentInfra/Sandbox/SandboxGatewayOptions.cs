namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

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
    /// Base URL of the gateway the app connects to. Uses the IPv4 loopback literal (not
    /// <c>localhost</c>) on purpose: the gateway binds <c>BIND_ADDRESS=127.0.0.1</c> (IPv4 only),
    /// while <c>localhost</c> resolves to <c>::1</c> first on Windows — the IPv6 connect then
    /// black-holes and burns the full HttpClient timeout before falling back to IPv4.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:3000";

    /// <summary>
    /// Workspace path relative to <see cref="WorkspaceBasePath"/>, mounted as the sandbox
    /// workspace. On the Docker backend this is <c>/workspace</c>; on the local/Windows
    /// backend the workspace is the resolved host directory itself (no <c>/workspace</c> mount).
    /// </summary>
    public string? Workspace { get; set; }

    /// <summary>
    /// Host base directory the gateway resolves the workspace under.
    /// Becomes <c>WORKSPACE_BASE_PATH</c> when the gateway is spawned.
    /// <para>
    /// OPTIONAL. Needed only for the same-host <b>spawn</b> path (and the same-host adopt
    /// optimization where the client pre-creates the workspace directory). When it is unset, the
    /// client forwards the workspace leaf to the gateway and lets the gateway own directory
    /// creation — so a workspace-backed session works against a REMOTE or per-app-rooting gateway
    /// (ADR 0029) with no local base configured. See <see cref="ResolveWorkspace(string)"/>.
    /// </para>
    /// </summary>
    public string? WorkspaceBasePath { get; set; }

    /// <summary>
    /// Optional ABSOLUTE path to the workspace directory. When set, it takes precedence over
    /// <see cref="WorkspaceBasePath"/> + <see cref="Workspace"/>: the app spawns the gateway with
    /// this path's parent as <c>WORKSPACE_BASE_PATH</c>, uses the final folder name as the session
    /// workspace, and creates the directory if it doesn't exist. This is the simplest way to point
    /// the sandbox at ANY folder (e.g. an existing repo) at startup without splitting it into
    /// base + leaf yourself. Only honored when the app SPAWNS the gateway — a pre-running/adopted
    /// gateway keeps its own <c>WORKSPACE_BASE_PATH</c>.
    /// </summary>
    public string? WorkspacePath { get; set; }

    /// <summary>
    /// Resolves the workspace into its three forms in a single pass: the gateway base directory
    /// (<c>WORKSPACE_BASE_PATH</c>), the session workspace leaf, and the absolute host path.
    /// When <see cref="WorkspacePath"/> is set it wins — its parent becomes the base and its final
    /// folder the leaf; otherwise <see cref="WorkspaceBasePath"/> + <see cref="Workspace"/> are used.
    /// <c>FullPath</c> is <c>null</c> when no workspace is configured, so callers skip directory
    /// creation. A relative <see cref="WorkspacePath"/> is made absolute against the current directory
    /// by <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    public (string? BasePath, string? Leaf, string? FullPath) ResolveWorkspace()
    {
        if (!string.IsNullOrWhiteSpace(WorkspacePath))
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkspacePath));
            return (Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath), fullPath);
        }

        var combined = !string.IsNullOrWhiteSpace(WorkspaceBasePath) && !string.IsNullOrWhiteSpace(Workspace)
            ? Path.Combine(WorkspaceBasePath, Workspace)
            : null;
        return (WorkspaceBasePath, Workspace, combined);
    }

    /// <summary>
    /// Resolves a per-workspace directory leaf into base/leaf/full-path. When
    /// <paramref name="relPathOverride"/> is null/whitespace this is identical to
    /// <see cref="ResolveWorkspace()"/>. Otherwise <paramref name="relPathOverride"/> is treated as
    /// the workspace leaf. A rooted override, or one containing a <c>..</c> traversal segment, is
    /// always rejected (via <see cref="InvalidOperationException"/>) regardless of whether a base is
    /// configured — a workspace identifier is never an absolute path and never traverses upward.
    /// <para>
    /// If a local base is configured (<see cref="WorkspaceBasePath"/>/<see cref="WorkspacePath"/>),
    /// the leaf is resolved under it and additionally rejected if it escapes the base, so a chosen
    /// workspace can never mount a directory outside the configured base. If NO base is
    /// configured, resolution returns just the leaf with a null base and null full path: the gateway
    /// may be remote, or root the workspace under its own per-app base (ADR 0029), so the client
    /// forwards the leaf and lets the gateway own directory creation instead of resolving/creating a
    /// local path. A base is thus OPTIONAL — required only for the same-host spawn optimization.
    /// </para>
    /// </summary>
    public (string? BasePath, string? Leaf, string? FullPath) ResolveWorkspace(string? relPathOverride)
    {
        if (string.IsNullOrWhiteSpace(relPathOverride))
        {
            return ResolveWorkspace();
        }

        // A workspace override is always a RELATIVE leaf (a workspace identifier), never an absolute
        // path — reject a rooted value up front, independent of whether a local base is configured.
        if (Path.IsPathRooted(relPathOverride))
        {
            throw new InvalidOperationException(
                $"Workspace directory '{relPathOverride}' must be relative to the workspace base."
            );
        }

        // Defense-in-depth: a workspace identifier never legitimately contains a parent-directory
        // traversal segment. Reject '..' regardless of whether a base is configured — on the no-base
        // path the leaf is forwarded straight to the (possibly remote/permissive) gateway, so this is
        // the client's only containment guard there; with a base, it fails fast before the escape
        // check below would catch the same thing.
        if (Array.Exists(relPathOverride.Split('/', '\\'), segment => segment == ".."))
        {
            throw new InvalidOperationException(
                $"Workspace directory '{relPathOverride}' must not contain '..' path segments."
            );
        }

        var (basePath, _, _) = ResolveWorkspace();
        if (string.IsNullOrWhiteSpace(basePath))
        {
            // No local base is configured — e.g. the client adopts a gateway that may be REMOTE, or
            // that roots the workspace under its own per-app base (ADR 0029). The client neither
            // resolves an absolute path nor pre-creates the directory on a filesystem it may not even
            // share: it forwards the leaf (the workspace identifier) and lets the gateway own
            // creation. WorkspaceBasePath / WorkspacePath is therefore OPTIONAL, needed only for the
            // same-host spawn path where the client pre-creates the workspace dir as an optimization.
            return (null, relPathOverride, null);
        }

        var baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(baseFull, relPathOverride)));

        var baseWithSeparator = baseFull + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(baseWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workspace directory '{relPathOverride}' escapes the workspace base directory."
            );
        }

        return (basePath, relPathOverride, fullPath);
    }

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
    /// Optional subset of marketplace aliases to activate per sandbox session, as a comma-separated
    /// list (e.g. <c>"official,claude_plugins"</c>). The aliases are the canonical names the gateway
    /// derives from <see cref="PluginsDirs"/>; the authoritative set can be read from the gateway's
    /// <c>GET /api/v1/marketplaces/preview</c> catalog. Sent as the <c>marketplaces</c> array on the
    /// sandbox-create request. When unset (or blank) the field is omitted and the gateway applies its
    /// own default set (<c>DEFAULT_MARKETPLACES</c>, unset ⇒ all). An unknown alias makes the gateway
    /// reject the create with a 400 listing the available aliases.
    /// </summary>
    public string? Marketplaces { get; set; }

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
