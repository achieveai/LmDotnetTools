namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;

/// <summary>
/// Configuration options for ClaudeAgentSDK provider
/// </summary>
public record ClaudeAgentSdkOptions
{
    /// <summary>
    /// Path to Node.js executable (auto-detected if null)
    /// </summary>
    public string? NodeJsPath { get; init; }

    /// <summary>
    /// Path to claude-agent-sdk CLI (auto-detected if null)
    /// </summary>
    public string? CliPath { get; init; }

    /// <summary>
    /// Process timeout in milliseconds (default: 10 minutes)
    /// </summary>
    public int ProcessTimeoutMs { get; init; } = 600000;

    /// <summary>
    /// Project root directory for session storage
    /// </summary>
    public string? ProjectRoot { get; init; }

    /// <summary>
    /// Path to .mcp.json configuration file
    /// </summary>
    public string? McpConfigPath { get; init; }
}
