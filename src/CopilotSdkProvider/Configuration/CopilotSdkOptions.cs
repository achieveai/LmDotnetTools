namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;

/// <summary>
/// Extensibility enum for Copilot tool bridging. Currently only the direct ACP
/// dynamic tool bridge is supported (no MCP path).
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
}
