namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Detects whether the Claude Agent SDK CLI and Node.js are present on the current machine.
/// E2E tests skip rather than fail when these prerequisites are missing — CI without the CLI
/// installed should not block the build.
/// </summary>
internal static class ClaudeCliPrerequisites
{
    public static (bool Available, string? Reason) Detect()
    {
        if (!CliProbe.TryRun("node", "--version", out _, nameof(ClaudeCliPrerequisites)))
        {
            return (false, "Node.js was not found on PATH.");
        }

        var cli = FindClaudeAgentSdkCli();
        return cli is null
            ? (
                false,
                "Claude Agent SDK CLI was not found (looked for 'claude' on PATH and standard install locations)."
            )
            : (true, null);
    }

    private static string? FindClaudeAgentSdkCli()
    {
        var candidates = new[]
        {
            "claude",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".npm-global",
                "bin",
                "claude"
            ),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"),
        };

        foreach (var candidate in candidates)
        {
            if (CliProbe.TryRun(candidate, "--version", out _, nameof(ClaudeCliPrerequisites)))
            {
                return candidate;
            }
        }
        return null;
    }
}
