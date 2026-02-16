using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Bootstrap;

public sealed class CodexBridgeDependencyInstaller
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks = new();

    public static string RequiredSdkVersion => "0.101.0";

    public async Task EnsureInstalledAsync(
        string bridgeDirectory,
        string npmPath,
        ILogger? logger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(npmPath);

        var lockObj = InstallLocks.GetOrAdd(bridgeDirectory, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(ct);

        try
        {
            if (IsInstallSatisfied(bridgeDirectory))
            {
                logger?.LogInformation(
                    "{event_type} {event_status} {provider} {dependency_install_state} {bridge_dir}",
                    "codex.dependency.install",
                    "skipped",
                    "codex",
                    "ready",
                    bridgeDirectory);
                return;
            }

            logger?.LogInformation(
                "{event_type} {event_status} {provider} {dependency_install_state} {bridge_dir}",
                "codex.dependency.install.started",
                "started",
                "codex",
                "installing",
                bridgeDirectory);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = npmPath,
                    Arguments = "ci --no-audit --silent",
                    WorkingDirectory = bridgeDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start npm process.");

                var stdErrTask = process.StandardError.ReadToEndAsync(ct);
                var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);

                await process.WaitForExitAsync(ct);
                var stdErr = await stdErrTask;
                var stdOut = await stdOutTask;

                if (process.ExitCode != 0)
                {
                    logger?.LogError(
                        "{event_type} {event_status} {provider} {dependency_install_state} {error_code} {exit_code} {exception_type} {stdout} {stderr}",
                        "codex.dependency.install.failed",
                        "failed",
                        "codex",
                        "failed",
                        "npm_ci_failed",
                        process.ExitCode,
                        nameof(InvalidOperationException),
                        Truncate(stdOut),
                        Truncate(stdErr));

                    throw new InvalidOperationException(
                        $"Failed to install Codex bridge dependencies (exit={process.ExitCode}): {Truncate(stdErr)}");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                logger?.LogError(
                    ex,
                    "{event_type} {event_status} {provider} {dependency_install_state} {error_code} {exception_type}",
                    "codex.dependency.install.failed",
                    "failed",
                    "codex",
                    "failed",
                    "npm_ci_exception",
                    ex.GetType().Name);

                throw;
            }

            logger?.LogInformation(
                "{event_type} {event_status} {provider} {dependency_install_state}",
                "codex.dependency.install.completed",
                "completed",
                "codex",
                "ready");
        }
        finally
        {
            lockObj.Release();
        }
    }

    internal static bool IsInstallSatisfied(string bridgeDirectory)
    {
        var lockPath = Path.Combine(bridgeDirectory, "package-lock.json");
        if (!File.Exists(lockPath))
        {
            return false;
        }

        var nodeModulesCodexSdk = Path.Combine(bridgeDirectory, "node_modules", "@openai", "codex-sdk", "package.json");
        if (!File.Exists(nodeModulesCodexSdk))
        {
            return false;
        }

        try
        {
            var text = File.ReadAllText(nodeModulesCodexSdk);
            return text.Contains($"\"version\": \"{RequiredSdkVersion}\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 2000 ? value : value[..2000];
    }
}
