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

    [Fact]
    public async Task WaitForExitAsync_ReturnsExitCode()
    {
        using var handle = LaunchBenign();

        var exit = await handle.WaitForExitAsync();

        exit.Should().Be(0);
        handle.ExitCode.Should().Be(0);
        handle.HasExited.Should().BeTrue();
        handle.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task ExitedEvent_FiresExactlyOnce()
    {
        using var handle = LaunchBenign();
        var count = 0;
        handle.Exited += (_, _) => Interlocked.Increment(ref count);

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
        _ = await handle.WaitForExitAsync();

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
