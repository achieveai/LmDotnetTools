using System.Diagnostics;

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
        if (!TryRun("node", "--version", out _))
        {
            return (false, "Node.js was not found on PATH.");
        }

        var cli = FindClaudeAgentSdkCli();
        return cli is null
            ? (false, "Claude Agent SDK CLI was not found (looked for 'claude' on PATH and standard install locations).")
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
                "claude"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "claude.cmd"),
        };

        foreach (var candidate in candidates)
        {
            if (TryRun(candidate, "--version", out _))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool TryRun(string fileName, string args, out string output)
    {
        output = string.Empty;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                return false;
            }

            // WaitForExit *before* ReadToEnd so a binary that never closes stdout cannot
            // hang the probe — synchronous ReadToEnd blocks until stdout is closed by the
            // child, which a hung CLI may never do.
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return false;
            }
            output = p.StandardOutput.ReadToEnd();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
