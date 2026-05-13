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
            Arguments = args ?? ["--info"],
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
        // Long-running benign child: dotnet --help paginates on some shells but
        // exits quickly. We instead use a sleep equivalent via dotnet test runner-
        // safe approach: invoke an infinite tail via 'sh' (Linux) or 'cmd' (Windows).
        IReadOnlyList<string> args;
        string fileName;
        if (OperatingSystem.IsWindows())
        {
            fileName = "cmd";
            args = ["/c", "pause"];
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
