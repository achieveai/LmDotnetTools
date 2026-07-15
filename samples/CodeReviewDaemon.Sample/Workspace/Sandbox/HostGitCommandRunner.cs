using System.Diagnostics;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Runs deterministic git/fs commands as HOST processes (design §6): the daemon's retention push lives
/// OUTSIDE the sandbox, so the untrusted review agent's tools — which run inside the sandbox — can never
/// share the write credential. A git command that talks to a remote gets the credential injected via
/// <see cref="HostGitCredentialEnv"/> (token off argv + off on-disk config).
/// </summary>
internal sealed class HostGitCommandRunner(
    Func<CancellationToken, Task<IReadOnlyList<GitProviderToken>>> credentialsSource,
    ILogger<HostGitCommandRunner> logger,
    IReadOnlyCollection<string>? adoOrgs = null) : ISandboxCommandRunner
{
    public async Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Argv.Count == 0)
        {
            throw new ArgumentException("Argv must be non-empty.", nameof(command));
        }

        // Process.Start throws Win32Exception(267) if WorkingDirectory is set but doesn't exist yet — e.g.
        // the sweeper's first-ever probe of a checkout dir that hasn't been cloned. Fail gracefully instead
        // of crashing so callers like ReviewBotCheckout can fall through from "probe" to "clone" (which
        // creates the directory) rather than aborting the whole sweep.
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory) && !Directory.Exists(command.WorkingDirectory))
        {
            var missingDirResult = new SandboxCommandResult(
                1,
                string.Empty,
                $"working directory '{command.WorkingDirectory}' does not exist");
            logger.LogDebug("Host git '{Argv}' exited {Exit}: {Stderr}",
                string.Join(' ', command.Argv), missingDirResult.ExitCode, missingDirResult.Stderr);
            return missingDirResult;
        }

        var psi = new ProcessStartInfo
        {
            FileName = command.Argv[0],
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < command.Argv.Count; i++)
        {
            psi.ArgumentList.Add(command.Argv[i]);
        }

        // Inject each signed-in provider's credential only when this is a git command (the sole
        // remote-talking case here) — GitHub and/or Azure DevOps, per which providers are signed in.
        // HostGitCredentialEnv always sets GIT_TERMINAL_PROMPT=0, so even a credential-less git command
        // fails fast rather than hanging on a prompt.
        if (string.Equals(command.Argv[0], "git", StringComparison.OrdinalIgnoreCase))
        {
            var credentials = await credentialsSource(cancellationToken).ConfigureAwait(false);
            foreach (var (k, v) in HostGitCredentialEnv.Build(credentials, adoOrgs))
            {
                psi.Environment[k] = v;
            }
        }

        using var process = new Process { StartInfo = psi };
        _ = process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var result = new SandboxCommandResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
        if (!result.Succeeded)
        {
            logger.LogDebug("Host git '{Argv}' exited {Exit}: {Stderr}",
                string.Join(' ', command.Argv), result.ExitCode, result.Stderr);
        }

        return result;
    }
}
