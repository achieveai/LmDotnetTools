using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.ProcessLauncher;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

/// <summary>
/// Contract test: ensures ClaudeAgentSdkClient hands the launcher seam the
/// arguments / environment / host paths the plan requires. No real CLI runs.
/// </summary>
public class ClaudeAgentSdkClientLaunchContractTests
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
    public async Task StartAsync_PassesClaudeAgentKindAndHostPaths()
    {
        var workingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(workingDir);

        var recorder = new RecordingLauncher();
        var options = new ClaudeAgentSdkOptions
        {
            CliPath = "/fake/cli.js",
            NodeJsPath = "/fake/node",
            ProjectRoot = workingDir,
            McpConfigPath = "/fake/.mcp.json",
            Mode = ClaudeAgentSdkMode.OneShot,
            ProcessLauncher = recorder,
        };

        var client = new ClaudeAgentSdkClient(options);
        try
        {
            var request = new ClaudeAgentSdkRequest
            {
                ModelId = "claude-sonnet-4-5-20250929",
                MaxTurns = 7,
                ReasoningEffort = "medium",
                StagingDirectory = "/fake/staging",
                SystemPrompt = "You are a useful assistant.",
            };

            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await client.StartAsync(request, CancellationToken.None));
        }
        finally
        {
            client.Dispose();
            Directory.Delete(workingDir, recursive: true);
        }

        Assert.NotNull(recorder.LastRequest);
        var captured = recorder.LastRequest!;
        Assert.Equal(CliAgentKind.Claude, captured.Agent);
        Assert.Equal("cli.js", captured.ExecutableHint);
        Assert.Equal("/fake/cli.js", captured.ExecutableOverride);
        Assert.Equal("/fake/node", captured.NodeJsPath);
        Assert.Equal(workingDir, captured.WorkingDirectory);

        Assert.Contains("--model", captured.Arguments);
        Assert.Contains("claude-sonnet-4-5-20250929", captured.Arguments);
        Assert.Contains("--max-turns", captured.Arguments);
        Assert.Contains("7", captured.Arguments);

        Assert.Equal("4096", captured.EnvironmentOverrides["MAX_THINKING_TOKENS"]);
        Assert.Equal("300", captured.EnvironmentOverrides["CLAUDE_CODE_STREAM_CLOSE_TIMEOUT"]);

        Assert.Contains(captured.HostPaths,
            p => p.Kind == HostPathKind.WorkingDirectory && p.Path == workingDir);
        Assert.Contains(captured.HostPaths,
            p => p.Kind == HostPathKind.McpConfigFile && p.Path == "/fake/.mcp.json");
        Assert.Contains(captured.HostPaths,
            p => p.Kind == HostPathKind.StagingDirectory && p.Path == "/fake/staging");
        Assert.Contains(captured.HostPaths,
            p => p.Kind == HostPathKind.SystemPromptFile);
    }
}
