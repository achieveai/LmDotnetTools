using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.ProcessLauncher.Tests;

/// <summary>
/// Exercises <see cref="DefaultProcessLauncher"/> end-to-end against a benign
/// child process. We use <c>dotnet --version</c> as the benign child because it
/// is always present in the test environment, prints to stdout, and exits
/// cleanly on its own — and, unlike <c>dotnet --info</c>, its output is a single
/// short line that can never fill the redirected stdout/stderr pipe buffer, so a
/// test that waits for exit without draining the streams cannot deadlock. (`--info`
/// grows with installed SDKs and crossed that threshold on developer machines,
/// hanging locally while passing on CI's clean image.)
/// </summary>
public class DefaultProcessLauncherTests
{
    [Fact]
    public async Task Launch_Codex_RunsBenignChildProcess()
    {
        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = "dotnet",
            Arguments = ["--version"],
        };

        using var handle = DefaultProcessLauncher.Instance.Launch(request);
        var exit = await handle.WaitForExitAsync();

        exit.Should().Be(0);
        handle.HasExited.Should().BeTrue();
        handle.ProcessId.Should().NotBeNull();
    }

    [Fact]
    public async Task Launch_Codex_AppliesWorkingDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            string fileName;
            IReadOnlyList<string> args;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                fileName = "cmd";
                args = ["/c", "cd"];
            }
            else
            {
                fileName = "pwd";
                args = [];
            }

            var request = new ProcessLaunchRequest
            {
                Agent = CliAgentKind.Codex,
                ExecutableHint = fileName,
                Arguments = args,
                WorkingDirectory = tempDir,
            };

            using var handle = DefaultProcessLauncher.Instance.Launch(request);
            var stdout = await handle.StandardOutput.ReadToEndAsync();
            _ = await handle.WaitForExitAsync();

            stdout.Trim().Should().Contain(Path.GetFileName(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Launch_Codex_AppliesEnvironmentOverrides()
    {
        string fileName;
        IReadOnlyList<string> args;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileName = "cmd";
            args = ["/c", "echo %LAUNCHER_TEST_VAR%"];
        }
        else
        {
            fileName = "sh";
            args = ["-c", "echo $LAUNCHER_TEST_VAR"];
        }

        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = fileName,
            Arguments = args,
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["LAUNCHER_TEST_VAR"] = "hello-launcher",
            },
        };

        using var handle = DefaultProcessLauncher.Instance.Launch(request);
        var stdout = await handle.StandardOutput.ReadToEndAsync();
        _ = await handle.WaitForExitAsync();

        stdout.Trim().Should().Be("hello-launcher");
    }

    [Fact]
    public void Launch_NullRequest_Throws()
    {
        var act = () => DefaultProcessLauncher.Instance.Launch(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Launch_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = "dotnet",
            Arguments = ["--info"],
        };

        var act = () => DefaultProcessLauncher.Instance.Launch(request, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Launch_MissingExecutable_ThrowsProcessLauncherException()
    {
        var request = new ProcessLaunchRequest
        {
            Agent = CliAgentKind.Codex,
            ExecutableHint = "this-executable-does-not-exist-anywhere-9f8e7d6c",
            Arguments = [],
        };

        var act = () => DefaultProcessLauncher.Instance.Launch(request);
        act.Should().Throw<ProcessLauncherException>();
    }
}
