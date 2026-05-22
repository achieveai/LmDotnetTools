using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.ProcessLauncher;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;

/// <summary>
/// Extensibility enum for Copilot tool bridging. The dynamic ACP tool bridge
/// hosts client-side tool implementations; orthogonally, external MCP servers
/// can be advertised to the Copilot CLI via <see cref="CopilotSdkOptions.McpServers"/>.
/// </summary>
public enum CopilotToolBridgeMode
{
    Dynamic,
}

public record CopilotSdkOptions
{
    public string CopilotCliPath { get; init; } = "copilot";

    public string CopilotCliMinVersion { get; init; } = "0.0.410";

    public int AcpStartupTimeoutMs { get; init; } = 30_000;

    public int TurnCompletionTimeoutMs { get; init; } = 300_000;

    public int TurnInterruptGracePeriodMs { get; init; } = 5_000;

    public string Model { get; init; } = "claude-sonnet-4.5";

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? BaseInstructions { get; init; }

    public string? DeveloperInstructions { get; init; }

    public string? ModelInstructionsFile { get; init; }

    public int UseModelInstructionsFileThresholdChars { get; init; } = 8_000;

    public CopilotToolBridgeMode ToolBridgeMode { get; init; } = CopilotToolBridgeMode.Dynamic;

    public bool EnableRpcTrace { get; init; }

    public string? RpcTraceFilePath { get; init; }

    /// <summary>
    /// Best-effort Copilot ACP session id used to drive cross-run session reuse.
    /// When the Copilot CLI advertises <c>agentCapabilities.sessions.load=true</c>
    /// on the <c>initialize</c> response, the client calls <c>session/load</c> with
    /// this id and falls back to <c>session/new</c> on any RPC error (the id is
    /// also forwarded into <c>session/new</c> params, where the server is
    /// authoritative on whether the id is honoured). When the CLI does not
    /// advertise <c>sessions.load</c> the client goes straight to <c>session/new</c>
    /// and emits a one-time <c>copilot.session.load.unsupported</c> log entry.
    /// </summary>
    public string? CopilotSessionId { get; init; }

    public int ProcessTimeoutMs { get; init; } = 600_000;

    public string Provider { get; init; } = "copilot";

    public string ProviderMode { get; init; } = "copilot";

    /// <summary>
    /// ACP protocol version advertised in the <c>initialize</c> handshake. Copilot CLI &gt;= 1.0.x
    /// validates this field as a number and rejects requests that omit it. The ACP spec currently
    /// defines version 1; override only if the CLI negotiates a different major.
    /// </summary>
    public int AcpProtocolVersion { get; init; } = 1;

    /// <summary>
    /// When true, after <c>initialize</c>+<c>session/new</c> the provider fails fast if the configured
    /// <see cref="Model"/> is not present in the model list returned by the Copilot CLI.
    /// </summary>
    public bool ModelAllowlistProbeEnabled { get; init; } = true;

    /// <summary>
    /// Default decision to return for server-initiated <c>session/request_permission</c> requests.
    /// </summary>
    public string DefaultPermissionDecision { get; init; } = "allow";

    // Retained for compatibility with existing env/config shape; ignored in raw streaming mode.
    public bool EmitSyntheticMessageUpdates { get; init; } = false;

    public int SyntheticMessageUpdateChunkChars { get; init; } = 28;

    /// <summary>
    ///     Optional client-supplied runtime inputs (system prompt, MCP servers, etc.).
    ///     Copilot consumes <see cref="AgentRuntimeProfile.SystemPrompt"/> (overrides
    ///     <see cref="DeveloperInstructions"/>). External MCP servers are taken from
    ///     <see cref="McpServers"/> rather than the profile; profile-level
    ///     <see cref="AgentRuntimeProfile.McpServers"/> are ignored (with a one-time
    ///     warning) so callers have a single, explicit knob. Profile <c>Skills</c>
    ///     and <c>SubAgents</c> remain unsupported by the Copilot ACP protocol.
    /// </summary>
    public AgentRuntimeProfile? Profile { get; init; }

    /// <summary>
    ///     External MCP servers to advertise to the Copilot CLI in the
    ///     <c>session/new</c> ACP request, keyed by server name. Empty (the
    ///     default) means the agent runs with no external MCP-served tools and
    ///     the wire field is emitted as <c>[]</c>. Stdio-typed entries are
    ///     forwarded in full; HTTP-typed entries are forwarded best-effort
    ///     (raw ACP <c>session/new</c> is stdio-centric) and may be dropped or
    ///     rejected by older Copilot CLI builds.
    /// </summary>
    public IReadOnlyDictionary<string, McpServerConfig> McpServers { get; init; }
        = ImmutableDictionary<string, McpServerConfig>.Empty;

    /// <summary>
    ///     Names of MCP servers the Copilot CLI should be told to disable. Each
    ///     non-empty entry projects to one <c>--disable-mcp-server &lt;name&gt;</c>
    ///     argument on the spawned CLI process. Null or whitespace entries are
    ///     skipped silently. Order is preserved.
    /// </summary>
    public IReadOnlyList<string> DisabledMcpServers { get; init; } = [];

    /// <summary>
    ///     When true, the Copilot CLI is launched with <c>--disable-builtin-mcps</c>,
    ///     suppressing the CLI's bundled built-in MCP servers. Default <c>false</c>.
    /// </summary>
    public bool DisableBuiltinMcps { get; init; }

    /// <summary>
    ///     Pluggable launcher used to spawn the Copilot CLI process. Defaults to
    ///     <see cref="DefaultProcessLauncher.Instance"/> which executes the CLI
    ///     directly on the host. Inject a custom <see cref="IProcessLauncher"/>
    ///     to redirect spawn (Docker, SSH, etc.) without touching provider internals.
    /// </summary>
    public IProcessLauncher ProcessLauncher { get; init; } = DefaultProcessLauncher.Instance;
}
