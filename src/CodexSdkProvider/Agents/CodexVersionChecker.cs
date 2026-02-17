using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

/// <summary>
/// Validates and compares Codex CLI versions.
/// </summary>
internal static class CodexVersionChecker
{
    private static readonly Regex VersionRegex = new(@"\b(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)\b", RegexOptions.Compiled);

    public static async Task<string> EnsureCodexCliVersionAsync(
        string cliPath,
        string minVersion,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start Codex CLI '{cliPath}'. Ensure it is installed and on PATH.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        await process.WaitForExitAsync(timeoutCts.Token);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        var combined = string.Join(Environment.NewLine, [stdOut, stdErr]).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Codex CLI version check failed (exit={process.ExitCode}): {CodexEventParser.Truncate(combined)}");
        }

        var detectedVersion = ExtractVersion(combined);
        if (string.IsNullOrWhiteSpace(detectedVersion))
        {
            throw new InvalidOperationException(
                $"Could not parse Codex CLI version from output: {CodexEventParser.Truncate(combined)}");
        }

        if (CompareVersion(detectedVersion, minVersion) < 0)
        {
            throw new InvalidOperationException(
                $"Codex CLI version '{detectedVersion}' is below minimum required '{minVersion}'.");
        }

        return detectedVersion;
    }

    public static string? ExtractVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionRegex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
    }

    public static int CompareVersion(string left, string right)
    {
        var leftParts = ParseVersion(left);
        var rightParts = ParseVersion(right);

        for (var index = 0; index < 3; index++)
        {
            var compare = leftParts[index].CompareTo(rightParts[index]);
            if (compare != 0)
            {
                return compare;
            }
        }

        return 0;
    }

    public static int[] ParseVersion(string version)
    {
        var match = VersionRegex.Match(version ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Invalid version string '{version}'.");
        }

        return
        [
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
        ];
    }
}
