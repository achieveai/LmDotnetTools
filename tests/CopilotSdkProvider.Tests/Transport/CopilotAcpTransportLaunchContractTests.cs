using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Transport;
using AchieveAi.LmDotnetTools.ProcessLauncher;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Transport;

/// <summary>
/// Contract test: ensures CopilotAcpTransport hands the launcher seam the
/// arguments / environment / host paths the plan requires. No real CLI runs.
/// </summary>
public class CopilotAcpTransportLaunchContractTests
{
    private sealed class RecordingLauncher : IProcessLauncher
    {
        public ProcessLaunchRequest? LastRequest { get; private set; }

        public IProcessHandle Launch(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            throw new ProcessLauncherException("recording launcher: never spawns");
        }
    }

    [Fact]
    public async Task StartAsync_PassesAcpStdioArgumentsAndHostPath()
    {
        var recorder = new RecordingLauncher();
        var options = new CopilotSdkOptions
        {
            CopilotCliPath = "copilot-cli-mock",
            ProcessLauncher = recorder,
        };

        await using var transport = new CopilotAcpTransport(options);

        var workingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDir);
        try
        {
            var act = async () => await transport.StartAsync(
                workingDir,
                apiKey: "ghp_test-token",
                baseUrl: "https://copilot.example.test",
                requestHandler: (_, _, _) => Task.FromResult(default(JsonElement)),
                notificationHandler: (_, _) => { },
                ct: CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(workingDir, recursive: true);
        }

        recorder.LastRequest.Should().NotBeNull();
        var request = recorder.LastRequest!;
        request.Agent.Should().Be(CliAgentKind.Copilot);
        request.ExecutableHint.Should().Be("copilot-cli-mock");
        request.Arguments.Should().Equal("--acp", "--stdio");
        request.WorkingDirectory.Should().Be(workingDir);
        request.EnvironmentOverrides.Should().ContainKey("COPILOT_API_KEY").WhoseValue.Should().Be("ghp_test-token");
        request.EnvironmentOverrides.Should().ContainKey("GITHUB_TOKEN").WhoseValue.Should().Be("ghp_test-token");
        request.EnvironmentOverrides.Should().ContainKey("COPILOT_BASE_URL").WhoseValue.Should().Be("https://copilot.example.test");
        request.HostPaths.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new HostPathReference(workingDir, HostPathKind.WorkingDirectory));
    }
}
