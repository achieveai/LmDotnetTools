using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Transport;
using AchieveAi.LmDotnetTools.ProcessLauncher;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Transport;

/// <summary>
/// Contract test: ensures CodexAppServerTransport hands the launcher seam the
/// arguments / environment / host paths the plan requires. No real CLI runs.
/// </summary>
public class CodexAppServerTransportLaunchContractTests
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
    public async Task StartAsync_PassesAppServerArgumentsAndHostPath()
    {
        var recorder = new RecordingLauncher();
        var options = new CodexSdkOptions
        {
            CodexCliPath = "codex-cli-mock",
            ProcessLauncher = recorder,
        };

        await using var transport = new CodexAppServerTransport(options);

        var workingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDir);
        try
        {
            var act = async () => await transport.StartAsync(
                workingDir,
                apiKey: "sk-test-key",
                baseUrl: "https://example.test",
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
        request.Agent.Should().Be(CliAgentKind.Codex);
        request.ExecutableHint.Should().Be("codex-cli-mock");
        request.Arguments.Should().Equal("app-server", "--listen", "stdio://");
        request.WorkingDirectory.Should().Be(workingDir);
        request.EnvironmentOverrides.Should().ContainKey("OPENAI_API_KEY").WhoseValue.Should().Be("sk-test-key");
        request.EnvironmentOverrides.Should().ContainKey("OPENAI_BASE_URL").WhoseValue.Should().Be("https://example.test");
        request.HostPaths.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new HostPathReference(workingDir, HostPathKind.WorkingDirectory));
    }
}
