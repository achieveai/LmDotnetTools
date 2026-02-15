namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Request configuration for claude-agent-sdk CLI invocation
/// </summary>
public record ClaudeAgentSdkRequest
{
    /// <summary>
    ///     Model ID to use (e.g., "claude-sonnet-4-5-20250929", "haiku", "opus")
    /// </summary>
    public required string ModelId { get; init; }

    /// <summary>
    ///     System prompt text
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    ///     Maximum number of conversation turns
    /// </summary>
    public int MaxTurns { get; init; } = 40;

    /// <summary>
    ///     Maximum thinking tokens (0 to disable extended thinking)
    /// </summary>
    public int MaxThinkingTokens { get; init; } = 0;

    /// <summary>
    ///     Session ID for resuming existing session (null for new session)
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    ///     Comma-separated list of allowed tools
    /// </summary>
    public string? AllowedTools { get; init; }

    /// <summary>
    ///     MCP server configurations
    /// </summary>
    public Dictionary<string, McpServerConfig>? McpServers { get; init; }

    /// <summary>
    ///     Input messages to send to the agent
    /// </summary>
    public List<object>? InputMessages { get; init; }

    /// <summary>
    ///     Output format (default: "stream-json")
    /// </summary>
    public string OutputFormat { get; init; } = "stream-json";

    /// <summary>
    ///     Input format (default: "stream-json")
    /// </summary>
    public string InputFormat { get; init; } = "stream-json";

    /// <summary>
    ///     Enable verbose logging
    /// </summary>
    public bool Verbose { get; init; } = true;

    /// <summary>
    ///     Permission mode (default: "bypassPermissions")
    /// </summary>
    public string PermissionMode { get; init; } = "bypassPermissions";

    /// <summary>
    ///     Setting sources
    /// </summary>
    public string SettingSources { get; init; } = "";
}
