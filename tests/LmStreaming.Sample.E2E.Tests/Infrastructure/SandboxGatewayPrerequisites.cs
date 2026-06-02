using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.E2E.Tests.Infrastructure;

/// <summary>
/// Detects whether a real sandbox MCP gateway is available for the gated workspace E2E test and,
/// when it is, applies the matching <c>SandboxGateway:*</c> configuration via environment variables.
/// </summary>
/// <remarks>
/// <para>
/// The test is skipped unless ONE of the following is true:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>SANDBOX_GATEWAY_EXE</c> points at an existing <c>mcp-gateway.exe</c> (with <c>agent-cli.exe</c>
/// beside it, or at <c>SANDBOX_AGENT_CLI</c>) — the host spawns the gateway (adopt-or-spawn).
/// </description></item>
/// <item><description>
/// A gateway already responds <c>200</c> at <c>{SANDBOX_GATEWAY_URL ?? http://localhost:3000}/health</c>
/// — the host adopts it.
/// </description></item>
/// </list>
/// <para>
/// This keeps the test green-by-skip in CI (which has no Rust gateway) while exercising the full
/// stack on any machine "configured with the path to the gateway".
/// </para>
/// </remarks>
public sealed class SandboxGatewayPrerequisites
{
    private const string DefaultBaseUrl = "http://localhost:3000";

    private SandboxGatewayPrerequisites(
        bool available,
        string skipReason,
        bool spawnMode,
        string? gatewayExePath,
        string? agentCliPath,
        string baseUrl)
    {
        Available = available;
        SkipReason = skipReason;
        SpawnMode = spawnMode;
        GatewayExePath = gatewayExePath;
        AgentCliPath = agentCliPath;
        BaseUrl = baseUrl;
    }

    /// <summary>True when a gateway is configured/reachable and the gated test may run.</summary>
    public bool Available { get; }

    /// <summary>Human-readable reason shown when the test is skipped.</summary>
    public string SkipReason { get; }

    /// <summary>True when the host should spawn the gateway from <see cref="GatewayExePath"/>; false when adopting a running one.</summary>
    public bool SpawnMode { get; }

    /// <summary>Resolved path to <c>mcp-gateway.exe</c> (spawn mode only).</summary>
    public string? GatewayExePath { get; }

    /// <summary>Resolved path to <c>agent-cli.exe</c> (spawn mode only).</summary>
    public string? AgentCliPath { get; }

    /// <summary>Base URL the app talks to (spawned or adopted).</summary>
    public string BaseUrl { get; }

    /// <summary>Probes the environment and returns the resolved availability + skip reason.</summary>
    public static SandboxGatewayPrerequisites Detect()
    {
        var baseUrl = NonEmpty(Environment.GetEnvironmentVariable("SANDBOX_GATEWAY_URL")) ?? DefaultBaseUrl;

        var exe = NonEmpty(Environment.GetEnvironmentVariable("SANDBOX_GATEWAY_EXE"));
        if (exe is not null)
        {
            if (!File.Exists(exe))
            {
                return Unavailable(
                    $"SANDBOX_GATEWAY_EXE is set to '{exe}' but no file exists there.");
            }

            var agentCli =
                NonEmpty(Environment.GetEnvironmentVariable("SANDBOX_AGENT_CLI"))
                ?? Path.Combine(Path.GetDirectoryName(exe)!, "agent-cli.exe");
            if (!File.Exists(agentCli))
            {
                return Unavailable(
                    $"agent-cli.exe was not found at '{agentCli}'. Place it beside the gateway exe "
                        + "or set SANDBOX_AGENT_CLI.");
            }

            return new SandboxGatewayPrerequisites(
                available: true,
                skipReason: string.Empty,
                spawnMode: true,
                gatewayExePath: exe,
                agentCliPath: agentCli,
                baseUrl: baseUrl);
        }

        // No exe configured — adopt a gateway only if one is already healthy.
        if (IsHealthy(baseUrl))
        {
            return new SandboxGatewayPrerequisites(
                available: true,
                skipReason: string.Empty,
                spawnMode: false,
                gatewayExePath: null,
                agentCliPath: null,
                baseUrl: baseUrl);
        }

        return Unavailable(
            "No sandbox gateway is configured. Set SANDBOX_GATEWAY_EXE to mcp-gateway.exe "
                + "(with agent-cli.exe beside it) or run a gateway reachable at "
                + $"{baseUrl}/health to enable this test.");
    }

    /// <summary>
    /// Creates a temp host workspace, applies the resolved <c>SandboxGateway:*</c> settings as
    /// environment variables (so the in-memory host binds them), and returns a scope that restores
    /// the prior environment and deletes the temp workspace on dispose.
    /// </summary>
    public GatewayConfigScope CreateConfigScope()
    {
        if (!Available)
        {
            throw new InvalidOperationException("CreateConfigScope must not be called when the gateway is unavailable.");
        }

        var workspaceBase = Path.Combine(Path.GetTempPath(), "lmstreaming-sandbox-e2e");
        const string workspaceLeaf = "workspace";
        _ = Directory.CreateDirectory(Path.Combine(workspaceBase, workspaceLeaf));

        var vars = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{SandboxGatewayOptions.SectionName}__BaseUrl"] = BaseUrl,
            [$"{SandboxGatewayOptions.SectionName}__WorkspaceBasePath"] = workspaceBase,
            [$"{SandboxGatewayOptions.SectionName}__Workspace"] = workspaceLeaf,
            [$"{SandboxGatewayOptions.SectionName}__AutoSpawn"] = SpawnMode ? "true" : "false",
            [$"{SandboxGatewayOptions.SectionName}__GatewayExePath"] = GatewayExePath,
            [$"{SandboxGatewayOptions.SectionName}__AgentCliPath"] = AgentCliPath,
        };

        return new GatewayConfigScope(vars, Path.Combine(workspaceBase, workspaceLeaf));
    }

    private static SandboxGatewayPrerequisites Unavailable(string reason) =>
        new(false, reason, false, null, null, DefaultBaseUrl);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsHealthy(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync($"{baseUrl.TrimEnd('/')}/health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Any failure (connection refused, timeout, DNS) means "no gateway to adopt".
            return false;
        }
    }
}

/// <summary>
/// Applies a set of environment variables for the duration of a test and restores their prior
/// values (and removes the temp workspace) on dispose.
/// </summary>
public sealed class GatewayConfigScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
    private readonly string _workspacePath;
    private bool _disposed;

    internal GatewayConfigScope(Dictionary<string, string?> vars, string workspacePath)
    {
        _workspacePath = workspacePath;
        foreach (var (key, value) in vars)
        {
            _previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>Absolute path to the host workspace directory configured for this scope.</summary>
    public string WorkspacePath => _workspacePath;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (key, value) in _previous)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            if (Directory.Exists(_workspacePath))
            {
                Directory.Delete(_workspacePath, recursive: true);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
