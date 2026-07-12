using System.Diagnostics;
using System.Text;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>The truncated stdout, raw stderr, and exit code of one script run under a real POSIX shell.</summary>
internal readonly record struct ShellResult(string Stdout, string Stderr, int ExitCode);

/// <summary>A disposable per-test workspace: its native (host) path and the path a POSIX shell sees for it.</summary>
internal sealed class ShellWorkspace : IDisposable
{
    public ShellWorkspace(string hostPath, string shellPath)
    {
        HostPath = hostPath;
        ShellPath = shellPath;
    }

    /// <summary>The workspace root as the host (test process) sees it — used to seed and inspect files directly.</summary>
    public string HostPath { get; }

    /// <summary>The workspace root as the POSIX shell sees it (a <c>/mnt/…</c> path under WSL, identical to <see cref="HostPath"/> on Linux).</summary>
    public string ShellPath { get; }

    public string HostFile(string relative) =>
        Path.Combine(HostPath, relative.Replace('/', Path.DirectorySeparatorChar));

    public bool HostFileExists(string relative) => File.Exists(HostFile(relative));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(HostPath))
            {
                Directory.Delete(HostPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a transient test workspace.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a transient test workspace.
        }
    }
}

/// <summary>
/// F3: runs the ACTUAL scripts <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts"/>
/// generates under an available POSIX shell (native <c>/bin/sh</c> on Linux/macOS, or WSL <c>bash</c>
/// driving <c>/bin/sh</c> on Windows), so the real wrapper — not just a model — is exercised. Capability
/// is detected once; tests that need it call <see cref="RequireCapabilityAsync"/>, which SKIPS visibly
/// when no shell/coreutils are present, or FAILS when <c>LMSBX_REQUIRE_POSIX_SHELL</c> is set (so a "must
/// have a shell" environment can never pass by silently skipping). It is never a contract test that
/// passes without a shell.
/// </summary>
internal static class PosixShellHarness
{
    private static readonly TimeSpan S_processTimeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<Task<Capability>> S_capability = new(DetectAsync);

    /// <summary>Skips the test when no POSIX shell is available — or fails it when the environment explicitly requires one.</summary>
    public static async Task RequireCapabilityAsync()
    {
        var capability = await S_capability.Value.ConfigureAwait(false);
        if (RequiredByEnvironment && !capability.Available)
        {
            Assert.Fail(
                $"LMSBX_REQUIRE_POSIX_SHELL is set but no usable POSIX shell was found: {capability.SkipReason}"
            );
        }

        Skip.IfNot(capability.Available, capability.SkipReason);
    }

    private static bool RequiredByEnvironment =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LMSBX_REQUIRE_POSIX_SHELL"));

    public static ShellWorkspace NewWorkspace()
    {
        var hostPath = Path.Combine(AppContext.BaseDirectory, "realshell-ws", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostPath);
        return new ShellWorkspace(hostPath, ToShellPath(hostPath));
    }

    /// <summary>
    /// Runs <paramref name="script"/> under <c>/bin/sh</c> with <c>SANDBOX_WORKSPACE</c> pointing at
    /// <paramref name="workspace"/>, and returns the shell's stdout AFTER modelling the gateway's exec
    /// truncation — exactly what the SDK would receive.
    /// </summary>
    public static async Task<ShellResult> RunAsync(string script, ShellWorkspace workspace)
    {
        var capability = await S_capability.Value.ConfigureAwait(false);
        var prelude = $"SANDBOX_WORKSPACE='{workspace.ShellPath}'\nexport SANDBOX_WORKSPACE\n";
        var scriptFile = Path.Combine(workspace.HostPath, Guid.NewGuid().ToString("N") + ".sh");
        // UTF-8, no BOM, LF preserved (the script is already '\n'-joined) so dash reads it verbatim.
        await File.WriteAllTextAsync(
                scriptFile,
                prelude + script,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            )
            .ConfigureAwait(false);

        var raw = OperatingSystem.IsWindows()
            ? await ExecAsync(capability.ShellExe!, ["-c", $"sh '{ToShellPath(scriptFile)}'"], workspace.HostPath)
                .ConfigureAwait(false)
            : await ExecAsync("/bin/sh", [scriptFile], workspace.HostPath).ConfigureAwait(false);

        return new ShellResult(GatewayTruncation.Apply(raw.Stdout), raw.Stderr, raw.ExitCode);
    }

    private static async Task<Capability> DetectAsync()
    {
        const string probe = "command -v base64 sha256sum head tail wc cut tr date >/dev/null 2>&1 && printf OK";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bash = LocateWindowsBash();
                if (bash is null)
                {
                    return new Capability(false, "bash.exe (WSL) was not found on this Windows host.", null);
                }

                var result = await ExecAsync(bash, ["-c", $"sh -c '{probe}'"]).ConfigureAwait(false);
                return result.Stdout.Trim() == "OK"
                    ? new Capability(true, string.Empty, bash)
                    : new Capability(false, "WSL /bin/sh is missing required coreutils (base64/sha256sum/…).", null);
            }

            var native = await ExecAsync("/bin/sh", ["-c", probe]).ConfigureAwait(false);
            return native.Stdout.Trim() == "OK"
                ? new Capability(true, string.Empty, "/bin/sh")
                : new Capability(false, "/bin/sh is missing required coreutils (base64/sha256sum/…).", null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or IOException)
        {
            return new Capability(false, $"No usable POSIX shell could be launched: {ex.Message}", null);
        }
    }

    private static string? LocateWindowsBash()
    {
        var system32 = Path.Combine(Environment.SystemDirectory, "bash.exe");
        return File.Exists(system32) ? system32 : null;
    }

    /// <summary>Translates a host path to the path a POSIX shell sees: identity on Linux/macOS, <c>C:\a\b</c> → <c>/mnt/c/a/b</c> under WSL.</summary>
    private static string ToShellPath(string hostPath)
    {
        var full = Path.GetFullPath(hostPath);
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }

        var drive = char.ToLowerInvariant(full[0]);
        var rest = full[2..].Replace('\\', '/');
        return $"/mnt/{drive}{rest}";
    }

    private static async Task<ProcOutput> ExecAsync(string fileName, string[] arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            // Anchor the shell's working directory to the test workspace, so any stray file a hostile
            // path/name might create (were an injection to succeed) lands where a test can detect it.
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new IOException($"Failed to start '{fileName}'.");
        using var timeout = new CancellationTokenSource(S_processTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcOutput(stdout, stderr, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the timeout and the kill.
            }

            throw new TimeoutException($"'{fileName}' did not exit within {S_processTimeout.TotalSeconds:0}s.");
        }
    }

    private readonly record struct ProcOutput(string Stdout, string Stderr, int ExitCode);

    private readonly record struct Capability(bool Available, string SkipReason, string? ShellExe);
}
