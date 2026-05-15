using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

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
    ///     Session ID for resuming existing session (null for new session).
    ///     Emits <c>--resume &lt;id&gt;</c>. Mutually exclusive with
    ///     <see cref="AssignedSessionId"/>; if both are set, <c>SessionId</c>
    ///     wins (the captured live id takes precedence over the host-chosen
    ///     seed). Callers in the multi-turn loop must populate at most one.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    ///     Host-chosen session id for a first run. Emits <c>--session-id &lt;guid&gt;</c>,
    ///     which directs the SDK to create a new on-disk session under the supplied id.
    ///     Mutually exclusive with <see cref="SessionId"/> (the resume flag); see
    ///     <see cref="SessionId"/> remarks for precedence.
    /// </summary>
    public string? AssignedSessionId { get; init; }

    /// <summary>
    ///     Comma-separated list of built-in tools available to the agent.
    ///     Passed as the --tools CLI flag to restrict which tools the model can see and use.
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
    ///     Comma-separated list of settings sources to load (e.g. "user,project,local").
    ///     When null or empty, the flag is omitted and the CLI applies its own default
    ///     (user,project,local). Pass an explicit value only when narrowing the set.
    /// </summary>
    public string? SettingSources { get; init; }

    /// <summary>
    ///     Reasoning effort level (low, medium, high, xhigh)
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    ///     Optional path to a directory the spawned CLI should treat as its
    ///     <c>~/.claude/</c> root. Mapped to the <c>CLAUDE_CONFIG_DIR</c> environment
    ///     variable on the child process. Used by <c>AgentRuntimeProfile</c>
    ///     materialization to expose client-supplied skills, sub-agents, and MCP
    ///     entries without polluting the host's real config.
    /// </summary>
    public string? StagingDirectory { get; init; }
}
