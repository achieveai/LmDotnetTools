using System.Diagnostics;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;

/// <summary>
/// Detects whether the OpenAI Codex CLI is present on the current machine and meets the
/// minimum version required by <see cref="CodexSdkOptions.CodexCliMinVersion"/>. E2E tests
/// skip rather than fail when these prerequisites are missing — CI without the CLI installed
/// (or with a stale CLI) should not block the build.
///
/// Recent Codex builds ship as a native binary; older npm-distributed builds are reachable via
/// the npm-global shim. Either path satisfies the <c>codex --version</c> probe — no separate
/// Node.js probe is required (contrast with the Claude Agent SDK CLI which shells out to Node).
/// </summary>
internal static class CodexCliPrerequisites
{
    // Mirrors the regex used inside CodexVersionChecker (which is internal to CodexSdkProvider).
    // Keeping a tiny copy here lets the prerequisite probe avoid widening the production assembly's
    // visibility just for tests.
    private static readonly Regex VersionRegex = new(@"\b(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)\b", RegexOptions.Compiled);

    public static (bool Available, string? Reason, string? CliPath) Detect()
    {
        var minVersion = new CodexSdkOptions().CodexCliMinVersion;

        foreach (var candidate in EnumerateCandidates())
        {
            if (!TryRun(candidate, "--version", out var output))
            {
                continue;
            }

            var detected = ExtractVersion(output);
            if (string.IsNullOrWhiteSpace(detected))
            {
                // Probe succeeded but the version line was unparseable; treat as found but
                // unverifiable so the test surfaces a clear skip reason.
                return (false, $"Codex CLI '{candidate}' returned an unparseable version line: {Truncate(output)}", null);
            }

            return CompareVersion(detected, minVersion) < 0
                ? (false, $"Codex CLI version '{detected}' is below required minimum '{minVersion}'.", null)
                : (true, null, candidate);
        }

        return (false, "Codex CLI was not found (looked for 'codex' on PATH and standard install locations).", null);
    }

    private static string? ExtractVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var match = VersionRegex.Match(value);
        return match.Success ? $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}" : null;
    }

    private static int CompareVersion(string left, string right)
    {
        var lp = ParseVersion(left);
        var rp = ParseVersion(right);
        for (var i = 0; i < 3; i++)
        {
            var c = lp[i].CompareTo(rp[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    private static int[] ParseVersion(string version)
    {
        var match = VersionRegex.Match(version ?? string.Empty);
        return match.Success
            ? [
                int.Parse(match.Groups["major"].Value),
                int.Parse(match.Groups["minor"].Value),
                int.Parse(match.Groups["patch"].Value),
              ]
            : [0, 0, 0];
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        yield return "codex";

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrEmpty(userProfile))
        {
            // Native installer (Codex Desktop / curl-based install).
            yield return Path.Combine(userProfile, ".codex", "bin", "codex");
            // npm-global on Linux/macOS.
            yield return Path.Combine(userProfile, ".npm-global", "bin", "codex");
        }

        if (!string.IsNullOrEmpty(appData))
        {
            // npm-global on Windows.
            yield return Path.Combine(appData, "npm", "codex.cmd");
        }
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

            // WaitForExit *before* ReadToEnd so a binary that never closes stdout cannot hang
            // the probe — synchronous ReadToEnd blocks until stdout is closed by the child,
            // which a hung CLI may never do.
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
            System.Diagnostics.Debug.WriteLine(
                $"CodexCliPrerequisites probe failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string Truncate(string value, int max = 200)
        => value.Length <= max ? value : value[..max] + "…";
}
