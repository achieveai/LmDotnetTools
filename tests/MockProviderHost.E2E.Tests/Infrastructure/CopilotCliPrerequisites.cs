using System.Diagnostics;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Detects whether the GitHub Copilot CLI (<c>@github/copilot</c>) is present on the
/// current machine. E2E tests skip rather than fail when this prerequisite is missing
/// so CI without the CLI installed should not block the build.
///
/// Mirrors <see cref="ClaudeCliPrerequisites"/>: probes a small set of well-known
/// install locations and PATH, returns <c>(false, reason)</c> when nothing is found.
/// </summary>
internal static class CopilotCliPrerequisites
{
    public static (bool Available, string? Reason) Detect()
    {
        if (!TryRun("node", "--version", out _))
        {
            return (false, "Node.js was not found on PATH.");
        }

        var cli = FindCopilotCli();
        return cli is null
            ? (false, "GitHub Copilot CLI was not found (looked for 'copilot' on PATH and standard install locations).")
            : (true, null);
    }

    private static string? FindCopilotCli()
    {
        var candidates = new[]
        {
            "copilot",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".npm-global",
                "bin",
                "copilot"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "copilot.cmd"),
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
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or PlatformNotSupportedException
                                       or System.IO.IOException)
        {
            // Probe is best-effort (file not on PATH, permission denied, platform mismatch).
            // Surface a Debug trace so a missing-tool diagnosis isn't silent on machines where
            // the CLI is supposedly installed.
            Debug.WriteLine(
                $"CopilotCliPrerequisites probe failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
