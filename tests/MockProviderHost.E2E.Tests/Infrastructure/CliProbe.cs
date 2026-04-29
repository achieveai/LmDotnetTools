using System.Diagnostics;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Shared best-effort process launcher for CLI prerequisite probes. Both Claude and Codex
/// detection paths run a candidate executable with <c>--version</c> and read its stdout; this
/// helper centralises the WaitForExit-before-Read ordering and the catch-clause pattern so a
/// single change (e.g. a new "this exec wedges on Linux" workaround) lands in one place.
/// </summary>
internal static class CliProbe
{
    public static bool TryRun(string fileName, string args, out string output, string callerName)
    {
        output = string.Empty;
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            if (p is null)
            {
                return false;
            }

            // WaitForExit *before* ReadToEnd so a binary that never closes stdout cannot hang
            // the probe — synchronous ReadToEnd blocks until stdout is closed by the child,
            // which a hung CLI may never do.
            if (!p.WaitForExit(5000))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                { /* best-effort */
                }
                return false;
            }
            output = p.StandardOutput.ReadToEnd();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
            when (ex
                    is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        or PlatformNotSupportedException
                        or System.IO.IOException
            )
        {
            // Probe is best-effort (file not on PATH, permission denied, platform mismatch).
            // Surface a Debug trace so a missing-tool diagnosis isn't silent on machines where
            // the CLI is supposedly installed.
            Debug.WriteLine($"{callerName} probe failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
