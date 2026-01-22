namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;

/// <summary>
///     Execution mode for ClaudeAgentSDK
/// </summary>
public enum ClaudeAgentSdkMode
{
    /// <summary>
    ///     Interactive mode: Keep process alive for multiple turns, send messages via stdin
    /// </summary>
    Interactive,

    /// <summary>
    ///     One-shot mode: Send initial prompt, close stdin, read all output until process exits
    /// </summary>
    OneShot,
}

/// <summary>
///     Configuration options for ClaudeAgentSDK provider
/// </summary>
public record ClaudeAgentSdkOptions
{
    /// <summary>
    ///     Path to Node.js executable (auto-detected if null)
    /// </summary>
    public string? NodeJsPath { get; init; }

    /// <summary>
    ///     Path to claude-agent-sdk CLI (auto-detected if null)
    /// </summary>
    public string? CliPath { get; init; }

    /// <summary>
    ///     Process timeout in milliseconds (default: 10 minutes)
    /// </summary>
    public int ProcessTimeoutMs { get; init; } = 600000;

    /// <summary>
    ///     Project root directory for session storage
    /// </summary>
    public string? ProjectRoot { get; init; }

    /// <summary>
    ///     Path to .mcp.json configuration file
    /// </summary>
    public string? McpConfigPath { get; init; }

    /// <summary>
    ///     Maximum turns per run
    /// </summary>
    public int MaxTurnsPerRun { get; init; } = 50;

    /// <summary>
    ///     Execution mode (default: Interactive)
    /// </summary>
    public ClaudeAgentSdkMode Mode { get; init; } = ClaudeAgentSdkMode.Interactive;

    /// <summary>
    ///     Maximum thinking tokens for extended thinking (default: 0 = disabled)
    ///     Can be overridden per-request via ExtraProperties["maxThinkingTokens"]
    /// </summary>
    public int MaxThinkingTokens { get; init; } = 0;

    /// <summary>
    ///     Keepalive interval for Interactive mode (default: 12 seconds).
    ///     Sends empty lines to stdin to keep the connection alive.
    ///     Only applies in Interactive mode.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>
    ///     Comma-separated list of allowed built-in tools.
    ///     If null, defaults to: "Read,Write,Edit,Bash,Grep,Glob,TodoWrite,Task,WebSearch,WebFetch"
    /// </summary>
    public string? AllowedTools { get; init; }
}
