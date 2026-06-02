using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// End-to-end proof that the <c>Workspace Agent</c> mode drives the REAL sandbox MCP gateway:
/// a mock-Anthropic provider returns a scripted instruction chain (one <c>sandbox-Bash</c> tool
/// call, then a final text), the middleware executes that tool call against the gateway, and we
/// assert both the tool-result round-trip and the write-through to the host workspace directory.
/// </summary>
/// <remarks>
/// <para>
/// Why <c>test-anthropic</c>: it is NOT one of the providers the Workspace-Agent guard rejects, so
/// it flows through the same middleware path as the real Anthropic/OpenAI providers — the path that
/// folds the gateway's MCP tools into the per-agent function registry. Tool names are registered as
/// <c>{clientId}-{tool}</c>, i.e. <c>sandbox-Bash</c>.
/// </para>
/// <para>
/// Gated: the test runs only when a gateway is configured/reachable
/// (see <see cref="SandboxGatewayPrerequisites"/>); otherwise it is skipped so CI stays green
/// without the Rust gateway. All progress is logged via <see cref="LoggingTestBase"/> to the shared
/// <c>.logs/tests/tests.jsonl</c> file with test correlation.
/// </para>
/// </remarks>
public sealed class SandboxWorkspaceGatewayE2ETests : LoggingTestBase
{
    public SandboxWorkspaceGatewayE2ETests(ITestOutputHelper output)
        : base(output)
    {
    }

    [SkippableFact]
    public async Task Workspace_agent_runs_sandbox_tool_through_gateway_and_writes_to_host()
    {
        LogTestStart();

        var prereq = SandboxGatewayPrerequisites.Detect();
        Logger.LogInformation(
            "Sandbox gateway prerequisites resolved: Available={Available}, SpawnMode={SpawnMode}, "
                + "BaseUrl={BaseUrl}, GatewayExe={GatewayExe}, SkipReason={SkipReason}",
            prereq.Available,
            prereq.SpawnMode,
            prereq.BaseUrl,
            prereq.GatewayExePath ?? "(adopt running gateway)",
            string.IsNullOrEmpty(prereq.SkipReason) ? "(none)" : prereq.SkipReason);
        Skip.IfNot(prereq.Available, prereq.SkipReason);

        using var config = prereq.CreateConfigScope();
        Logger.LogInformation(
            "Applied SandboxGateway config: WorkspacePath={WorkspacePath}, AutoSpawn={AutoSpawn}",
            config.WorkspacePath,
            prereq.SpawnMode);

        // Unique marker + file so reruns (and a shared adopted gateway) never collide.
        var marker = "e2e_marker_" + Guid.NewGuid().ToString("N")[..8];
        var fileName = "sandbox-e2e-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        // Bash starts in the workspace directory on the local backend, so a relative path lands
        // inside the host workspace. echo+cat writes the marker AND returns it as the tool result.
        var command = $"echo {marker} > {fileName} && cat {fileName}";
        Logger.LogInformation(
            "Scripted instruction chain: turn 1 -> tool_use sandbox-Bash (command={Command}); turn 2 -> final text",
            command);

        var responder = ScriptedSseResponder.New()
            .ForRole("workspace-agent", _ => true)
                .Turn(t => t.ToolCall("sandbox-Bash", new { command }))
                .Turn(t => t.Text("Wrote and read the marker file."))
            .Build();

        using var factory = new E2EWebAppFactory(
            "test-anthropic",
            new ScriptedBuilder(responder.AsAnthropicHandler()));

        var threadId = "sandbox-ws-" + Guid.NewGuid().ToString("N");
        Logger.LogInformation(
            "Connecting WebSocket thread {ThreadId} in mode {ModeId} (provider test-anthropic, middleware path)",
            threadId,
            SystemChatModes.WorkspaceAgentModeId);
        var socket = await factory.ConnectWebSocketAsync(threadId, SystemChatModes.WorkspaceAgentModeId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("Create the marker file in the workspace and read it back.");
        Logger.LogInformation("Sent user message; collecting streamed frames (timeout 120s)...");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(120));
        Logger.LogInformation("Collected {FrameCount} WebSocket frame(s)", frames.Count);

        var toolNames = frames.ToolCallNames();
        var toolResults = frames.ToolCallResults();
        Logger.LogInformation(
            "Observed {ToolCount} tool call(s): [{ToolNames}]",
            toolNames.Count,
            string.Join(", ", toolNames));
        LogData("toolResults", toolResults);

        // (1) The model's tool call reached the registry under the prefixed sandbox tool name.
        toolNames.Should().Contain(
            "sandbox-Bash",
            "the gateway's Bash tool is folded into the registry as 'sandbox-Bash'");

        // (2) Round-trip through the gateway: executing Bash returned the marker it just wrote.
        string.Concat(toolResults).Should().Contain(
            marker,
            "the sandbox Bash tool executed echo+cat through the gateway and returned the marker");

        // (3) Write-through: the file the model created exists on the REAL host workspace.
        //     HostPath is gateway-reported, so read it from the (single-flight) live session.
        var session = await factory.Services
            .GetRequiredService<SandboxSessionRegistry>()
            .GetOrCreateSessionAsync("default");
        Logger.LogInformation(
            "Resolved live sandbox session {SessionId}; host workspace path {HostPath}",
            session.SessionId,
            session.HostPath);

        var hostFile = Path.Combine(session.HostPath, fileName);
        Logger.LogInformation("Asserting host write-through at {HostFile}", hostFile);
        File.Exists(hostFile).Should().BeTrue(
            $"the model wrote {fileName} via the sandbox gateway into the host workspace {session.HostPath}");
        var hostContent = await File.ReadAllTextAsync(hostFile);
        Logger.LogInformation("Host file content length {Length}; contains marker={ContainsMarker}",
            hostContent.Length,
            hostContent.Contains(marker, StringComparison.Ordinal));
        hostContent.Should().Contain(marker);

        // (4) The agent finished with its scripted closing text.
        frames.ConcatText().Should().Contain("Wrote and read the marker file.");

        try
        {
            File.Delete(hostFile);
            Logger.LogInformation("Cleaned up probe file {HostFile}", hostFile);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Best-effort cleanup of probe file {HostFile} failed", hostFile);
        }

        LogTestEnd();
    }
}
