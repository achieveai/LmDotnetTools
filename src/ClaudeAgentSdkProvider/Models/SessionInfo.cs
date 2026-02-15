namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Information about a claude-agent-sdk session
/// </summary>
public record SessionInfo
{
    public required string SessionId { get; init; }

    public string? FilePath { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public string? ProjectRoot { get; init; }
}
