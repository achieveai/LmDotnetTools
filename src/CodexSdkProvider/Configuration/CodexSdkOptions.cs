namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;

public record CodexSdkOptions
{
    public string? NodeJsPath { get; init; }

    public string? NpmPath { get; init; }

    public string? BridgeScriptPath { get; init; }

    public string Model { get; init; } = "gpt-5.3-codex";

    public string ApprovalPolicy { get; init; } = "on-request";

    public string SandboxMode { get; init; } = "workspace-write";

    public bool SkipGitRepoCheck { get; init; } = true;

    public bool NetworkAccessEnabled { get; init; } = true;

    public string WebSearchMode { get; init; } = "disabled";

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? WorkingDirectory { get; init; }

    public int ProcessTimeoutMs { get; init; } = 600_000;

    public bool AutoInstallBridgeDependencies { get; init; } = true;

    public string Provider { get; init; } = "codex";

    public string ProviderMode { get; init; } = "codex";

    // Experimental diagnostic toggle. Raw provider streaming is the default path.
    public bool EmitSyntheticMessageUpdates { get; init; } = false;

    // Retained for compatibility with existing env/config shape; ignored in raw streaming mode.
    public int SyntheticMessageUpdateChunkChars { get; init; } = 28;
}
