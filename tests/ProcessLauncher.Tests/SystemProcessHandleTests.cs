using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.ProcessLauncher.Tests;

/// <summary>
/// Tests <see cref="SystemProcessHandle"/> behaviour by launching a benign
/// child process through <see cref="DefaultProcessLauncher"/>. Avoids any
/// reliance on a specific provider CLI being installed.
/// </summary>
public class SystemProcessHandleTests
{
    private static IProcessHandle LaunchBenign(IReadOnlyList<string>? args = null)
    {
        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = "dotnet",
            // `--version` (a single short line) rather than `--info`: these tests don't read the redirected
            // stdout/stderr before calling WaitForExitAsync, so a child whose output exceeds the OS pipe
            // buffer would block writing and never exit — deadlocking the wait. `--info` grows with the number
            // of installed SDKs/runtimes and crosses that threshold on developer boxes (it stayed under it on
            // CI's clean image, so the hang only reproduced locally). `--version` can never fill the buffer.
            Arguments = args ?? ["--version"],
        };
        return DefaultProcessLauncher.Instance.Launch(request);
    }

    /// <summary>
    /// A long-running benign child (a sleeper that writes nothing to stdout, so no pipe-buffer deadlock)
    /// that stays alive until <see cref="IProcessHandle.Kill"/>. Used where a test must attach a subscriber
    /// or observe the running state BEFORE the child exits.
    /// </summary>
    private static IProcessHandle LaunchLongRunning()
    {
        IReadOnlyList<string> args;
        string fileName;
        if (OperatingSystem.IsWindows())
        {
            fileName = "cmd";
            args = ["/c", "timeout", "/t", "30", "/nobreak"];
        }
        else
        {
            fileName = "sh";
            args = ["-c", "sleep 30"];
        }

        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = fileName,
            Arguments = args,
        };
        return DefaultProcessLauncher.Instance.Launch(request);
    }

    /// <summary>
    /// Drains both redirected streams concurrently with the exit wait. Both streams are redirected
    /// but left unread otherwise; <c>dotnet --info</c>'s output is close to the pipe's buffer capacity,
    /// so an unread pipe blocks the child's write and <see cref="IProcessHandle.WaitForExitAsync"/>
    /// then waits forever for an exit that can never happen.
    /// </summary>
    private static async Task<int> WaitForExitDrainedAsync(IProcessHandle handle)
    {
        var stdoutTask = handle.StandardOutput.ReadToEndAsync();
        var stderrTask = handle.StandardError.ReadToEndAsync();
        var exit = await handle.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        return exit;
    }

    [Fact]
    public async Task WaitForExitAsync_ReturnsExitCode()
    {
        using var handle = LaunchBenign();

        var exit = await WaitForExitDrainedAsync(handle);

        exit.Should().Be(0);
        handle.ExitCode.Should().Be(0);
        handle.HasExited.Should().BeTrue();
        handle.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task ExitedEvent_FiresExactlyOnce()
    {
        // A controllable long-running child (killed on demand), NOT a very short-lived one: SystemProcessHandle
        // raises its one-shot Exited event during construction when the child has already exited, so a fast
        // child (e.g. `dotnet --version`) could exit before this subscriber attaches and the handler would never
        // fire. The sleeper stays alive until Kill, so Exited fires while the subscriber is attached.
        using var handle = LaunchLongRunning();
        var count = 0;
        handle.Exited += (_, _) => Interlocked.Increment(ref count);

        // LaunchLongRunning() is a sleeper that never exits on its own, so it must be killed to
        // trigger Exited (main's WaitForExitDrainedAsync would block forever waiting for a self-exit).
        // It writes nothing to stdout, so there is no pipe-buffer to drain here.
        handle.Kill(entireProcessTree: true);
        _ = await handle.WaitForExitAsync();
        // Give the event loop a moment to deliver the Exited callback.
        await Task.Delay(100);

        count.Should().Be(1);
    }

    [Fact]
    public async Task Kill_TerminatesRunningProcess()
    {
        // Long-running benign child. We avoid `cmd /c pause` on Windows because
        // its console-mode read interacts unpredictably with the redirected
        // stdin pipe (can hang or exit immediately depending on runner image).
        // `timeout /t 30 /nobreak` is a stable 30-second sleep that does not
        // read stdin, so the pipe redirection does not affect it.
        IReadOnlyList<string> args;
        string fileName;
        if (OperatingSystem.IsWindows())
        {
            fileName = "cmd";
            args = ["/c", "timeout", "/t", "30", "/nobreak"];
        }
        else
        {
            fileName = "sh";
            args = ["-c", "sleep 30"];
        }

        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = fileName,
            Arguments = args,
        };

        using var handle = DefaultProcessLauncher.Instance.Launch(request);
        handle.HasExited.Should().BeFalse();

        handle.Kill(entireProcessTree: true);
        _ = await handle.WaitForExitAsync();

        handle.HasExited.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var handle = LaunchBenign();
        _ = await WaitForExitDrainedAsync(handle);

        await handle.DisposeAsync();
        await handle.DisposeAsync();
        handle.Dispose();
    }

    [Fact]
    public void StandardStreams_AreAccessible()
    {
        using var handle = LaunchBenign();

        handle.StandardInput.Should().NotBeNull();
        handle.StandardOutput.Should().NotBeNull();
        handle.StandardError.Should().NotBeNull();
    }
}
