using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.ProcessLauncher;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;

public enum CodexToolBridgeMode
{
    Mcp,
    Dynamic,
    Hybrid,
}

public record CodexSdkOptions
{
    public string CodexCliPath { get; init; } = "codex";

    public string CodexCliMinVersion { get; init; } = "0.101.0";

    public int AppServerStartupTimeoutMs { get; init; } = 30_000;

    public int TurnCompletionTimeoutMs { get; init; } = 300_000;

    public int TurnInterruptGracePeriodMs { get; init; } = 5_000;

    public string Model { get; init; } = "gpt-5.3-codex";

    public string ApprovalPolicy { get; init; } = "on-request";

    public string SandboxMode { get; init; } = "workspace-write";

    public bool SkipGitRepoCheck { get; init; } = true;

    public bool NetworkAccessEnabled { get; init; } = true;

    public string WebSearchMode { get; init; } = "disabled";

    /// <summary>
    /// Feature flags to explicitly disable. These are passed as features.{name} = false in the config.
    /// Defaults disable shell/file-edit tools so the model only uses MCP tools, web search, and web fetch.
    /// </summary>
    public IReadOnlyList<string> DisabledFeatures { get; init; } =
    [
        "shell_tool",           // Disable shell command execution
        "apply_patch_freeform", // Disable freeform file patching
        "unified_exec",         // Disable PTY-backed exec
        "multi_agent",          // Disable multi-agent coordination
        "apps",                 // Disable apps feature
        "apps_mcp_gateway",     // Disable apps MCP gateway
    ];

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? BaseInstructions { get; init; }

    public string? DeveloperInstructions { get; init; }

    public string? ModelInstructionsFile { get; init; }

    public int UseModelInstructionsFileThresholdChars { get; init; } = 8_000;

    public CodexToolBridgeMode ToolBridgeMode { get; init; } = CodexToolBridgeMode.Hybrid;

    public bool EnableRpcTrace { get; init; }

    public string? RpcTraceFilePath { get; init; }

    /// <summary>
    /// Trace-only label forwarded to <c>CodexRpcTraceWriter</c>. Does NOT drive
    /// conversation resume on the Codex app-server — Codex uses
    /// <c>thread/resume</c> against <c>codex_thread_id</c> persisted by
    /// <c>AchieveAi.LmDotnetTools.LmMultiTurn.CodexAgentLoop</c>.
    /// </summary>
    /// <remarks>
    /// Use <see cref="InitialThreadId"/> to seed the first-run thread id when no
    /// metadata is persisted yet.
    /// </remarks>
    [Obsolete("CodexSessionId is a trace-only label and does not control session lifetime. Use " + nameof(InitialThreadId) + " to drive cross-run thread resume.")]
    public string? CodexSessionId { get; init; }

    /// <summary>
    /// Optional first-run Codex thread id. Acts as a fallback when
    /// <c>AchieveAi.LmDotnetTools.LmMultiTurn.CodexAgentLoop</c> has no
    /// persisted <c>codex_thread_id</c> in <c>ThreadMetadata.Properties</c>
    /// (i.e. on cold restarts where the store is empty). When non-empty AND
    /// the metadata lookup misses, the value seeds <c>_codexThreadId</c> so the
    /// first Codex app-server call goes through <c>thread/resume</c> instead of
    /// <c>thread/start</c>. After the SDK reports its own thread id, the
    /// captured value wins. Persisted metadata always wins over this seed —
    /// the option only fills the void when nothing is stored.
    /// </summary>
    public string? InitialThreadId { get; init; }

    public bool ExposeCodexInternalToolsAsToolMessages { get; init; } = true;

    public bool EmitLegacyInternalToolReasoningSummaries { get; init; } = false;

    public int ProcessTimeoutMs { get; init; } = 600_000;

    public string? ReasoningEffort { get; init; }

    public string Provider { get; init; } = "codex";

    public string ProviderMode { get; init; } = "codex";

    // Experimental diagnostic toggle. Raw provider streaming is the default path.
    public bool EmitSyntheticMessageUpdates { get; init; } = false;

    // Retained for compatibility with existing env/config shape; ignored in raw streaming mode.
    public int SyntheticMessageUpdateChunkChars { get; init; } = 28;

    /// <summary>
    ///     Optional client-supplied runtime inputs (system prompt, MCP servers, etc.).
    ///     Codex consumes <see cref="AgentRuntimeProfile.SystemPrompt"/> (overrides
    ///     <see cref="DeveloperInstructions"/>) and <see cref="AgentRuntimeProfile.McpServers"/>
    ///     (merged with bridge-init MCP, profile entries win on key collision).
    ///     <see cref="AgentRuntimeProfile.Skills"/> and <see cref="AgentRuntimeProfile.SubAgents"/>
    ///     are not supported by Codex's CLI; their presence triggers a one-time
    ///     warning log entry per loop and otherwise has no effect.
    /// </summary>
    public AgentRuntimeProfile? Profile { get; init; }

    /// <summary>
    ///     Pluggable launcher used to spawn the Codex CLI process. Defaults to
    ///     <see cref="DefaultProcessLauncher.Instance"/> which executes the CLI
    ///     directly on the host. Inject a custom <see cref="IProcessLauncher"/>
    ///     to redirect spawn (Docker, SSH, etc.) without touching provider internals.
    /// </summary>
    public IProcessLauncher ProcessLauncher { get; init; } = DefaultProcessLauncher.Instance;
}
