namespace AchieveAi.LmDotnetTools.ProcessLauncher;

/// <summary>
/// Identifies the CLI agent being launched. Custom launchers can use this to
/// apply agent-specific behavior (e.g., a Docker launcher may select different
/// base images per agent kind).
/// </summary>
public enum CliAgentKind
{
    /// <summary>The <c>@anthropic-ai/claude-agent-sdk</c> Node.js CLI.</summary>
    Claude,

    /// <summary>The OpenAI Codex <c>codex app-server</c> CLI.</summary>
    Codex,

    /// <summary>The GitHub Copilot <c>copilot --acp</c> CLI.</summary>
    Copilot,
}
