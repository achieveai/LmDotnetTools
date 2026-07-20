using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// End-to-end proof, through the BROWSER, that the <c>Workspace Agent</c> mode can <c>git clone</c> a
/// public AND a private GitHub repository inside the sandbox — exercising the full egress path the
/// local workspace-tool tests deliberately skip: the egress proxy, the gateway's auth webhook, and the
/// GitHub credential injection.
/// </summary>
/// <remarks>
/// <para>
/// Driven by an <em>instruction chain</em> (a <see cref="ScriptedSseResponder"/> that scripts the
/// model's tool calls deterministically — the tools themselves execute for real). Two
/// <c>Bash</c> turns clone <c>octocat/Hello-World</c> (public, sanity) and
/// <c>achieveai/LmDotnetTools</c> (private — the clone that requires the host-aware Basic-auth header
/// AND the proxy's flushed/de-chunked response). Success is asserted three ways: the rendered
/// tool-call pills, the final assistant text, and — decisively — the cloned <c>.git</c> trees on the
/// host workspace.
/// </para>
/// <para>
/// Gated as a <see cref="SkippableFactAttribute"/>: it runs only when a sandbox gateway is reachable
/// (<see cref="SandboxGatewayPrerequisites"/>) AND a GitHub sign-in is persisted
/// (<see cref="GitHubClonePrerequisites"/>). Because it <em>adopts</em> the running gateway, the
/// session workspace is a unique leaf under that gateway's own <c>WORKSPACE_BASE_PATH</c> (a temp dir
/// the gateway cannot see would be rejected). In CI (no gateway, no sign-in) it skips, staying green.
/// </para>
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class SandboxGitCloneInstructionChainTests
{
    private readonly PlaywrightFixture _fixture;

    public SandboxGitCloneInstructionChainTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Workspace_agent_clones_public_and_private_repos_through_sandbox()
    {
        var gateway = SandboxGatewayPrerequisites.Detect();
        Skip.IfNot(gateway.Available, gateway.SkipReason);

        var github = GitHubClonePrerequisites.Detect();
        Skip.IfNot(github.Available, github.SkipReason);

        var workspaceBase = GitHubClonePrerequisites.ResolveGatewayWorkspaceBase();
        Skip.IfNot(
            workspaceBase is not null && Directory.Exists(workspaceBase),
            "Could not resolve the adopted gateway's workspace base. Set SANDBOX_WORKSPACE_BASE, or "
                + "ensure the sample's SandboxGateway:WorkspaceBasePath exists.");

        var id = Guid.NewGuid().ToString("N")[..8];
        // The adopted gateway resolves the workspace under its own WORKSPACE_BASE_PATH and requires
        // the leaf to already exist — create a unique one so concurrent/other sessions never collide.
        var leaf = "e2e-clone-" + id;
        var workspacePath = Path.Combine(workspaceBase!, leaf);
        Directory.CreateDirectory(workspacePath);

        var pubDir = "e2e-pub-" + id;
        var privDir = "e2e-priv-" + id;

        // Each turn is one Bash command; '&&' makes a failed clone fail the whole command. The gateway
        // is the sole MCP server here, so its tools are exposed under their natural names (Bash, not
        // sandbox-Bash — see Program.cs's ConnectHttpMcpClient(omitServerPrefix: true) for "sandbox").
        // The public repo needs no real auth; the private one exercises Basic auth + the proxy fixes.
        var pubCommand =
            $"rm -rf {pubDir} && git clone --depth 1 https://github.com/octocat/Hello-World {pubDir} && cat {pubDir}/README";
        var privCommand =
            $"rm -rf {privDir} && git clone --depth 1 --single-branch https://github.com/achieveai/LmDotnetTools {privDir} "
            + $"&& git -C {privDir} rev-parse HEAD";

        var responder = ScriptedSseResponder.New()
            .ForRole("workspace-agent", _ => true)
                .Turn(t => t.ToolCall("Bash", new { command = pubCommand }))
                .Turn(t => t.ToolCall("Bash", new { command = privCommand }))
                .Turn(t => t.Text("Cloned both repositories."))
            .Build();

        // A fixed loopback port is required so the gateway can call this host's auth webhook at a URL
        // known before the host boots.
        var port = GitHubClonePrerequisites.ReserveLoopbackPort();
        var gw = SandboxGatewayOptions.SectionName;
        var auth = AuthOptions.SectionName;
        using var env = new EnvironmentVariableScope(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Adopt the running gateway; root the session workspace under the gateway's own base.
            [$"{gw}__BaseUrl"] = gateway.BaseUrl,
            [$"{gw}__WorkspaceBasePath"] = workspaceBase,
            [$"{gw}__Workspace"] = leaf,
            [$"{gw}__AutoSpawn"] = "false",
            // Reuse the developer's signed-in token; enable the github.com allow-rule; advertise the webhook.
            [$"{auth}__TokenStoreDir"] = github.TokenStoreDir,
            [$"{auth}__Github__ClientId"] = GitHubClonePrerequisites.PlaceholderClientId,
            [$"{auth}__Webhook__PublicBaseUrl"] = $"http://127.0.0.1:{port}",
        });

        try
        {
            await using var session = await _fixture.OpenAsync(
                "test-anthropic",
                responder.HandlerFor("test-anthropic"),
                fixedPort: port);
            var page = session.Page;

            // Workspace Agent mode is what folds the gateway's sandbox tools into the agent — select it
            // before the first send (the thread locks its mode on send).
            await page.ModeSelectorButton().ClickAsync();
            await page.ModeOption(SystemChatModes.WorkspaceAgentModeId).ClickAsync();

            await page.SendMessageAsync("Clone the public and private repositories into the workspace.");
            // Real network clones, including a ~5 MB private packfile through the MITM proxy — be generous.
            await page.WaitForStreamIdleAsync(timeoutMs: 240_000);

            // (1) Browser-visible: both Bash tool calls rendered as pills.
            await page.ToolCallPills().WaitForCountAtLeastAsync(2, timeoutMs: 20_000);
            var toolNames = await page.ToolCallNamesAsync();
            toolNames.Should().Contain("Bash", "the clones run through the gateway's sandbox Bash tool");

            // (2) The instruction chain ran to completion and the closing text reached the renderer.
            await page.AssistantText().WaitForCountAtLeastAsync(1);
            var assistantText = string.Join(" ", await page.AssistantText().AllInnerTextsAsync());
            assistantText.Should().Contain("Cloned both repositories.");

            // (3) Decisive: both clones actually landed on the host workspace. A truncated packfile (the
            //     proxy bug) or a 401 (the Basic-auth bug) would leave no usable working tree.
            var privClone = Path.Combine(workspacePath, privDir);
            var listing = Directory.Exists(privClone)
                ? string.Join(", ", Directory.EnumerateFileSystemEntries(privClone).Select(Path.GetFileName))
                : "(private clone directory missing)";
            Directory.Exists(Path.Combine(workspacePath, pubDir, ".git"))
                .Should().BeTrue($"the public clone should have written {pubDir}/.git under {workspacePath}");
            Directory.Exists(Path.Combine(privClone, ".git"))
                .Should().BeTrue($"the private clone should have written {privDir}/.git under {workspacePath}");
            File.Exists(Path.Combine(privClone, "LmDotnetTools.sln"))
                .Should().BeTrue($"the private clone should have checked out the working tree; {privClone} contains: [{listing}]");

            await session.SaveSuccessScreenshotAsync("SandboxGitClone.Public_and_private_through_sandbox");
        }
        finally
        {
            // Set SANDBOX_E2E_KEEP_WORKSPACE=1 to leave the cloned workspace on disk for inspection.
            var keep = string.Equals(
                Environment.GetEnvironmentVariable("SANDBOX_E2E_KEEP_WORKSPACE"), "1", StringComparison.Ordinal);
            if (!keep)
            {
                ForceDeleteDirectory(workspacePath);
            }
        }
    }

    /// <summary>
    /// Recursively deletes <paramref name="dir"/>, first clearing the read-only attribute git sets on
    /// pack/object files (which otherwise makes <see cref="Directory.Delete(string, bool)"/> throw on
    /// Windows). Best-effort: never throws, so it is safe in a <c>finally</c>.
    /// </summary>
    private static void ForceDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // Ignore individual attribute failures; the delete below is the real attempt.
                }
            }

            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the probe workspace under the shared gateway base.
        }
    }
}
