using System.Diagnostics;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// An <see cref="IWorkspaceFileBrowser"/> backed by a REAL directory and a REAL POSIX shell, instead of
/// by recorded calls. It exists for exactly one claim the recording fake cannot make: that the bytes the
/// transcript writer stages and the shell script it splices them with actually produce a parseable
/// <c>.jsonl</c> file on a filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Everything else about the writer is asserted against <see cref="FakeFileBrowser"/>, which records argv
/// verbatim — the right tool for "what did it ask for". This one answers "and what did that DO", which is
/// where a quoting bug, a missing <c>mkdir</c> or a newline weld that welds the wrong way actually shows
/// up. The gateway runs the same argv against <c>/bin/sh</c> in a container; here it runs against the
/// host's, in a temp directory.
/// </para>
/// <para>
/// Argv is passed through, never re-interpreted: an <c>sh</c> command is handed to the shell as-is, and any
/// other command is executed through <c>sh -c 'exec "$@"' sh …</c> so the vector reaches the program
/// unsplit. Reimplementing <c>tail</c>/<c>mv</c>/the append script in C# would test the reimplementation.
/// </para>
/// </remarks>
/// <param name="root">Workspace root; every relative path resolves under it.</param>
/// <param name="shell">Absolute path to a POSIX <c>sh</c>.</param>
internal sealed class LocalShellWorkspaceBrowser(string root, string shell) : IWorkspaceFileBrowser
{
    /// <summary>Resolves an <c>sh</c> on this machine, or null when there is none to run.</summary>
    /// <remarks>
    /// On Windows the shell ships with Git but is frequently absent from a non-Bash <c>PATH</c>, so the
    /// well-known install locations are probed too — the alternative is a test that silently skips on
    /// every developer machine.
    /// </remarks>
    public static string? FindPosixShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/bin/sh") ? "/bin/sh" : null;
        }

        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir, "sh.exe"))
            .Concat(
                new[]
                {
                    Environment.GetEnvironmentVariable("ProgramFiles"),
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                    Environment.GetEnvironmentVariable("ProgramW6432"),
                }
                    .Where(programFiles => !string.IsNullOrWhiteSpace(programFiles))
                    .Select(programFiles => Path.Combine(programFiles!, "Git", "usr", "bin", "sh.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }

    public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(
        string threadId,
        string persistedWorkspaceId,
        SandboxCredential? requestCredential,
        CancellationToken ct = default) =>
        Task.FromResult(
            new SandboxSessionResolution(
                SandboxSessionResolutionOutcome.Resolved,
                new SandboxSession(persistedWorkspaceId, "sess-local", "/workspace", root),
                "app",
                null));

    public Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(
        string sessionId,
        string relativePath,
        CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SandboxDirectoryEntry>>([]);

    public Task<byte[]> ReadWorkspaceFileBytesAsync(
        string sessionId,
        string relativePath,
        long? maxBytes,
        CancellationToken ct = default)
    {
        var full = Resolve(relativePath);
        return Task.FromResult(File.Exists(full) ? File.ReadAllBytes(full) : []);
    }

    public async Task WriteWorkspaceFileBytesAsync(
        string sessionId,
        string relativePath,
        byte[] bytes,
        CancellationToken ct = default)
    {
        var full = Resolve(relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct).ConfigureAwait(false);
    }

    public async Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(
        string sessionId,
        SandboxCommand command,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(shell)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (string.Equals(command.Arguments[0], "sh", StringComparison.Ordinal))
        {
            foreach (var argument in command.Arguments.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exec \"$@\"");
            startInfo.ArgumentList.Add("sh");
            foreach (var argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{shell}'.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new SandboxCommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            OperationId = "local",
        };
    }

    private string Resolve(string relativePath) =>
        Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
