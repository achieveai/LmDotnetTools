using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

/// <summary>
/// Validates and compares Copilot CLI versions.
/// </summary>
internal static class CopilotVersionChecker
{
    private static readonly Regex VersionRegex = new(@"\b(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)\b", RegexOptions.Compiled);

    public static async Task<string> EnsureCopilotCliVersionAsync(
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
                $"Failed to start Copilot CLI '{cliPath}'. Ensure it is installed and on PATH.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        string stdOut;
        string stdErr;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            stdOut = await stdOutTask;
            stdErr = await stdErrTask;
        }
        catch (OperationCanceledException)
        {
            // Terminate a hung `copilot --version` so we don't leak the child process.
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Process may have exited between the HasExited check and Kill; ignore.
                }
            }

            throw;
        }

        var combined = string.Join(Environment.NewLine, [stdOut, stdErr]).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Copilot CLI version check failed (exit={process.ExitCode}): {CopilotEventParser.Truncate(combined)}");
        }

        var detectedVersion = ExtractVersion(combined);
        return string.IsNullOrWhiteSpace(detectedVersion)
            ? throw new InvalidOperationException(
                $"Could not parse Copilot CLI version from output: {CopilotEventParser.Truncate(combined)}")
            : CompareVersion(detectedVersion, minVersion) < 0
            ? throw new InvalidOperationException(
                $"Copilot CLI version '{detectedVersion}' is below minimum required '{minVersion}'.")
            : detectedVersion;
    }

    public static string? ExtractVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = VersionRegex.Match(value);
        return !match.Success ? null : $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
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
        return !match.Success
            ? throw new InvalidOperationException($"Invalid version string '{version}'.")
            : [
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
        ];
    }
}
