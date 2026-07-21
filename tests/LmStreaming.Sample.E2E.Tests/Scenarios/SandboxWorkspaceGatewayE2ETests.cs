using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using FluentAssertions.Execution;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// End-to-end proof that the <c>Workspace Agent</c> mode drives the REAL sandbox MCP gateway:
/// a mock-Anthropic provider returns a scripted instruction chain (one <c>Bash</c> tool
/// call, then a final text), the middleware executes that tool call against the gateway, and we
/// assert both the tool-result round-trip and the write-through to the host workspace directory.
/// </summary>
/// <remarks>
/// <para>
/// Why <c>test-anthropic</c>: it is NOT one of the providers the Workspace-Agent guard rejects, so
/// it flows through the same middleware path as the real Anthropic/OpenAI providers — the path that
/// folds the gateway's MCP tools into the per-agent function registry. The sandbox gateway is the
/// sole MCP server here, so tools are registered under their bare names (<c>omitServerPrefix: true</c>
/// in <c>Program.cs</c>'s <c>ConnectHttpMcpClient</c> call), i.e. <c>Bash</c>, not <c>sandbox-Bash</c>.
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
            "Scripted instruction chain: turn 1 -> tool_use Bash (command={Command}); turn 2 -> final text",
            command);

        var responder = ScriptedSseResponder.New()
            .ForRole("workspace-agent", _ => true)
                .Turn(t => t.ToolCall("Bash", new { command }))
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

        // (1) The model's tool call reached the registry under the bare gateway tool name.
        toolNames.Should().Contain(
            "Bash",
            "the gateway's Bash tool is folded into the registry as 'Bash' (omitServerPrefix: true)");

        // (2) Round-trip through the gateway: executing Bash returned the marker it just wrote.
        string.Concat(toolResults).Should().Contain(
            marker,
            "the sandbox Bash tool executed echo+cat through the gateway and returned the marker");

        // (3) Write-through: the file the model created exists on the REAL host workspace.
        //     session.HostPath is the gateway's CONTAINER-side path (e.g. "/workspace"), never a host
        //     filesystem path (see SandboxInfo.WorkspaceContainerPath) — by SDK design, the client only
        //     knows the real host base directory in SPAWN mode (it provisioned config.WorkspacePath for
        //     the gateway it launched). When ADOPTING a possibly-remote gateway, that gateway owns
        //     workspace-directory placement entirely and never reports a host path back, so the real
        //     directory is unknowable here — skip the filesystem check in that mode.
        var session = await factory.Services
            .GetRequiredService<SandboxSessionRegistry>()
            .GetOrCreateSessionAsync("default");
        Logger.LogInformation(
            "Resolved live sandbox session {SessionId}; container workspace path {HostPath}",
            session.SessionId,
            session.HostPath);

        if (prereq.SpawnMode)
        {
            var hostFile = Path.Combine(config.WorkspacePath, fileName);
            Logger.LogInformation("Asserting host write-through at {HostFile}", hostFile);
            File.Exists(hostFile).Should().BeTrue(
                $"the model wrote {fileName} via the sandbox gateway into the host workspace {config.WorkspacePath}");
            var hostContent = await File.ReadAllTextAsync(hostFile);
            Logger.LogInformation("Host file content length {Length}; contains marker={ContainsMarker}",
                hostContent.Length,
                hostContent.Contains(marker, StringComparison.Ordinal));
            hostContent.Should().Contain(marker);

            try
            {
                File.Delete(hostFile);
                Logger.LogInformation("Cleaned up probe file {HostFile}", hostFile);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Best-effort cleanup of probe file {HostFile} failed", hostFile);
            }
        }
        else
        {
            Logger.LogInformation(
                "Adopting a possibly-remote gateway: it owns workspace-directory placement and never "
                    + "reports a host path, so the host-filesystem write-through check is skipped; "
                    + "check (2) already confirms the gateway wrote and read back the marker.");
        }

        // (4) The agent finished with its scripted closing text.
        frames.ConcatText().Should().Contain("Wrote and read the marker file.");

        LogTestEnd();
    }

    /// <summary>
    /// Validates the CORE sandbox MCP tool set end-to-end through the real gateway by driving a
    /// scripted instruction chain that exercises, in sequence,
    /// <c>Write → Read → Bash → Glob → Grep</c>, then asserts
    /// each tool produced its expected result. Per-tool validation outcomes are logged as structured
    /// <c>SandboxToolCheck</c> events to <c>.logs/tests/tests.jsonl</c> so they can be queried with DuckDB.
    /// </summary>
    [SkippableFact]
    public async Task Workspace_agent_runs_core_sandbox_tool_chain_through_gateway()
    {
        LogTestStart();

        var prereq = SandboxGatewayPrerequisites.Detect();
        Logger.LogInformation(
            "Sandbox gateway prerequisites: Available={Available}, SpawnMode={SpawnMode}, BaseUrl={BaseUrl}, SkipReason={SkipReason}",
            prereq.Available,
            prereq.SpawnMode,
            prereq.BaseUrl,
            string.IsNullOrEmpty(prereq.SkipReason) ? "(none)" : prereq.SkipReason);
        Skip.IfNot(prereq.Available, prereq.SkipReason);

        using var config = prereq.CreateConfigScope();

        var id = Guid.NewGuid().ToString("N")[..8];
        var marker = "marker_" + id;          // appears in file content -> surfaced ONLY by Read
        var needle = "NEEDLE_" + id;          // appears in file content -> surfaced by Read AND Grep
        var bashMarker = "bashok_" + id;      // surfaced ONLY by Bash echo
        var probeName = "probe-" + id + ".txt";
        var fileContent = $"line-one {marker}\nline-two {needle}\n";

        Logger.LogInformation(
            "Scripted chain (paths relative to the session workspace cwd, portable across spawn/adopt "
                + "gateways): Read(register) -> Write -> Read -> Bash -> Glob -> Grep "
                + "(probe={Probe}, marker={Marker}, needle={Needle}, bashMarker={BashMarker})",
            probeName,
            marker,
            needle,
            bashMarker);

        // The gateway enforces Claude-Code read-before-write semantics: a path must be Read (even a
        // not-yet-existing one — a failed Read still registers it) before Write/Edit is permitted.
        // The chain therefore opens with a Read of the (missing) probe path, mirroring what the
        // Workspace Agent system prompt instructs the real LLM to do. Paths are deliberately BARE
        // relative names, not an absolute host-style path — the tools resolve them against the
        // session's own workspace cwd, which is the only path form valid whether the gateway is
        // spawned locally or adopted as an already-running (possibly remote/containerized) process;
        // an absolute host path built from config.WorkspacePath is meaningless to an adopted gateway
        // (see the write-through comment further below).
        var responder = ScriptedSseResponder.New()
            .ForRole("workspace-agent", _ => true)
                .Turn(t => t.ToolCall("Read", new { file_path = probeName }))
                .Turn(t => t.ToolCall("Write", new { file_path = probeName, content = fileContent }))
                .Turn(t => t.ToolCall("Read", new { file_path = probeName }))
                .Turn(t => t.ToolCall("Bash", new { command = $"echo {bashMarker}" }))
                .Turn(t => t.ToolCall("Glob", new { pattern = "*.txt", path = "." }))
                .Turn(t => t.ToolCall("Grep", new { pattern = needle, path = ".", output_mode = "content" }))
                .Turn(t => t.Text("Completed sandbox tool chain."))
            .Build();

        using var factory = new E2EWebAppFactory(
            "test-anthropic",
            new ScriptedBuilder(responder.AsAnthropicHandler()));

        var threadId = "sandbox-chain-" + Guid.NewGuid().ToString("N");
        Logger.LogInformation("Connecting WebSocket thread {ThreadId} in mode {ModeId}", threadId, SystemChatModes.WorkspaceAgentModeId);
        var socket = await factory.ConnectWebSocketAsync(threadId, SystemChatModes.WorkspaceAgentModeId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("Run the workspace tool chain: write, read, bash, glob, grep.");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(180));

        var toolNames = frames.ToolCallNames();
        var toolResults = frames.ToolCallResults();
        var combinedResults = string.Concat(toolResults);
        var needleHits = CountOccurrences(combinedResults, needle);
        Logger.LogInformation(
            "Observed {ToolCount} tool-call frame(s); distinct tools=[{Tools}]; combined result chars={Len}; needleHits={NeedleHits}",
            toolNames.Count,
            string.Join(", ", toolNames.Distinct()),
            combinedResults.Length,
            needleHits);
        LogData("toolCallNames", toolNames);
        LogData("toolCallResults", toolResults);

        var session = await factory.Services
            .GetRequiredService<SandboxSessionRegistry>()
            .GetOrCreateSessionAsync("default");
        Logger.LogInformation(
            "Live session {SessionId}; container workspace path {HostPath}",
            session.SessionId,
            session.HostPath);

        // Per-tool validation: each tool gets a distinct signal so a single failing tool is isolable.
        // All signals come from the tools' OWN returned text — never local host-disk state — so this
        // check set holds whether the gateway is spawned locally or adopted as a remote/containerized
        // process (see the write-through comment further below).
        var checks = new[]
        {
            new ToolCheck(
                "Write",
                toolNames.Contains("Write"),
                combinedResults.Contains("wrote", StringComparison.OrdinalIgnoreCase),
                "the sandbox Write tool reported a successful write"),
            new ToolCheck(
                "Read",
                toolNames.Contains("Read"),
                combinedResults.Contains(marker, StringComparison.Ordinal),
                "Read returned file content (marker)"),
            new ToolCheck(
                "Bash",
                toolNames.Contains("Bash"),
                combinedResults.Contains(bashMarker, StringComparison.Ordinal),
                "Bash echoed its marker"),
            new ToolCheck(
                "Glob",
                toolNames.Contains("Glob"),
                combinedResults.Contains(probeName, StringComparison.Ordinal),
                "Glob listed the probe file"),
            new ToolCheck(
                "Grep",
                toolNames.Contains("Grep"),
                needleHits >= 2,
                "Grep matched the needle (Read + Grep => >=2 hits)"),
        };

        foreach (var c in checks)
        {
            Logger.LogInformation(
                "SandboxToolCheck: tool={SandboxTool} invoked={Invoked} validated={Validated} detail={Detail}",
                c.Tool,
                c.Invoked,
                c.Validated,
                c.Detail);
        }

        using (new AssertionScope())
        {
            foreach (var c in checks)
            {
                c.Invoked.Should().BeTrue($"{c.Tool} should have been invoked through the gateway-backed registry");
                c.Validated.Should().BeTrue($"{c.Tool} should have produced its expected result — {c.Detail}");
            }
        }

        frames.ConcatText().Should().Contain(
            "Completed sandbox tool chain.",
            "the agent loop should run all five tool turns then the closing text");

        // Bonus write-through verification against the real host disk: only meaningful in SPAWN mode,
        // where config.WorkspacePath IS the directory the test itself provisioned for the gateway it
        // launched. session.HostPath is the gateway's CONTAINER-side path (e.g. "/workspace"), never a
        // host filesystem path (see SandboxInfo.WorkspaceContainerPath) — when ADOPTING a possibly-
        // remote gateway, that gateway owns workspace-directory placement entirely and never reports a
        // host path back, so the real directory is unknowable here and this check is skipped.
        if (prereq.SpawnMode)
        {
            var probeAbs = Path.Combine(Path.GetFullPath(config.WorkspacePath), probeName);
            var hostExists = File.Exists(probeAbs);
            var hostContent = hostExists ? await File.ReadAllTextAsync(probeAbs) : string.Empty;
            Logger.LogInformation(
                "Host probe file: path={ProbeAbs} exists={Exists} contentLength={Length} containsMarker={HasMarker}",
                probeAbs,
                hostExists,
                hostContent.Length,
                hostContent.Contains(marker, StringComparison.Ordinal));
            hostExists.Should().BeTrue(
                $"the model wrote {probeName} via the sandbox gateway into the host workspace {config.WorkspacePath}");
            hostContent.Should().Contain(marker);

            try
            {
                File.Delete(probeAbs);
                Logger.LogInformation("Cleaned up probe file {ProbeAbs}", probeAbs);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Best-effort cleanup of probe file {ProbeAbs} failed", probeAbs);
            }
        }

        LogTestEnd();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed record ToolCheck(string Tool, bool Invoked, bool Validated, string Detail);
}
