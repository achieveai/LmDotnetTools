using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
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
    public static (bool Available, string? Reason, string? CliPath) Detect()
    {
        var minVersion = new CodexSdkOptions().CodexCliMinVersion;

        foreach (var candidate in EnumerateCandidates())
        {
            if (!CliProbe.TryRun(candidate, "--version", out var output, nameof(CodexCliPrerequisites)))
            {
                continue;
            }

            var detected = CodexVersionChecker.ExtractVersion(output);
            if (string.IsNullOrWhiteSpace(detected))
            {
                // Probe succeeded but the version line was unparseable; treat as found but
                // unverifiable so the test surfaces a clear skip reason.
                return (
                    false,
                    $"Codex CLI '{candidate}' returned an unparseable version line: {Truncate(output)}",
                    null
                );
            }

            return CodexVersionChecker.CompareVersion(detected, minVersion) < 0
                ? (false, $"Codex CLI version '{detected}' is below required minimum '{minVersion}'.", null)
                : (true, null, candidate);
        }

        return (false, "Codex CLI was not found (looked for 'codex' on PATH and standard install locations).", null);
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

    private static string Truncate(string value, int max = 200) => value.Length <= max ? value : value[..max] + "…";
}
