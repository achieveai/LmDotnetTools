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

    public int TurnCompletionTimeoutMs { get; init; } = 120_000;

    public int TurnInterruptGracePeriodMs { get; init; } = 5_000;

    public string Model { get; init; } = "gpt-5.3-codex";

    public string ApprovalPolicy { get; init; } = "on-request";

    public string SandboxMode { get; init; } = "workspace-write";

    public bool SkipGitRepoCheck { get; init; } = true;

    public bool NetworkAccessEnabled { get; init; } = true;

    public string WebSearchMode { get; init; } = "disabled";

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

    public string? CodexSessionId { get; init; }

    public bool ExposeCodexInternalToolsAsToolMessages { get; init; } = true;

    public bool EmitLegacyInternalToolReasoningSummaries { get; init; } = false;

    public int ProcessTimeoutMs { get; init; } = 600_000;

    public string Provider { get; init; } = "codex";

    public string ProviderMode { get; init; } = "codex";

    // Experimental diagnostic toggle. Raw provider streaming is the default path.
    public bool EmitSyntheticMessageUpdates { get; init; } = false;

    // Retained for compatibility with existing env/config shape; ignored in raw streaming mode.
    public int SyntheticMessageUpdateChunkChars { get; init; } = 28;
}
