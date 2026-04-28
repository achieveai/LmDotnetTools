using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Scenarios;

/// <summary>
/// Real-process E2E: spawn the OpenAI Codex CLI pointed at a mock provider host bound to a
/// real TCP port, and verify the CLI made at least one OpenAI-shaped request against the host.
///
/// <para>
/// <b>Status (2026-04-28):</b> the live-assertion path is currently <i>known-broken</i> on
/// Codex CLI 0.125+, which connects to a hardcoded <c>wss://api.openai.com/v1/responses</c>
/// WebSocket endpoint and ignores <c>OPENAI_BASE_URL</c>. The skip path (default CI) is clean;
/// the opt-in path (<c>LMDOTNET_RUN_CODEX_E2E=1</c>) will fail the assertion until the
/// revised plan on issue #13 lands a workaround (custom <c>model_providers</c> entry in
/// <c>$CODEX_HOME/config.toml</c> + matching CLI selection).
/// </para>
/// <para>
/// These tests are skipped when the CLI is not installed (or below the required version) so
/// CI without the SDK can still run the rest of the suite.
/// </para>
/// </summary>
public sealed class CodexAgainstMockTests
{
    [SkippableFact]
    public async Task Cli_routes_through_mock_host_via_BaseUrl_and_ApiKey()
    {
        // Real-CLI E2E is opt-in: the protocol-level handshake the CLI performs against the
        // OpenAI endpoints depends on CLI version and Codex configuration. Run with
        // LMDOTNET_RUN_CODEX_E2E=1 once a stable scenario contract is recorded.
        Skip.If(
            Environment.GetEnvironmentVariable("LMDOTNET_RUN_CODEX_E2E") != "1",
            "Set LMDOTNET_RUN_CODEX_E2E=1 to run real-CLI E2E tests.");

        var (available, reason, cliPath) = CodexCliPrerequisites.Detect();
        Skip.IfNot(available, reason ?? "Codex CLI not available.");

        // Isolate CODEX_HOME to a fresh temp dir with API-key auth so cached ChatGPT auth on
        // the developer machine cannot bypass the OPENAI_BASE_URL / OPENAI_API_KEY env-var
        // bridge. The Codex CLI inherits CODEX_HOME from the test process via Process.Start.
        using var codexHome = new IsolatedCodexHome("mock-token");

        // The role definition is sufficient to consume the first OpenAI call from the CLI; we
        // treat the responder's turn queue as the ground-truth signal that the CLI reached the
        // mock host. We do not assert on the CLI's stdout decoding here — that's the subject of
        // richer E2E scenarios tracked in the follow-up issues.
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", _ => true)
                .Turn(t => t.Text("hello from the scripted parent"))
            .Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        var options = new CodexSdkOptions
        {
            // Process.Start with UseShellExecute=false does not walk PATHEXT on Windows, so
            // pass the exact resolved path (e.g. %APPDATA%\npm\codex.cmd) the prerequisite
            // probe found rather than the bare "codex" default.
            CodexCliPath = cliPath ?? "codex",
            BaseUrl = fixture.BaseUrl + "/v1",
            ApiKey = "mock-token",
            // Keep the CLI single-turn-friendly: short timeouts so a stalled handshake fails
            // fast inside the test's 30-second outer cancellation token rather than hanging.
            AppServerStartupTimeoutMs = 20_000,
            TurnCompletionTimeoutMs = 20_000,
        };

        await using var client = new CodexSdkClient(options);
        var initOptions = new CodexBridgeInitOptions
        {
            Model = options.Model,
            ApprovalPolicy = options.ApprovalPolicy,
            SandboxMode = options.SandboxMode,
            SkipGitRepoCheck = options.SkipGitRepoCheck,
            NetworkAccessEnabled = options.NetworkAccessEnabled,
            WebSearchMode = options.WebSearchMode,
            DisabledFeatures = options.DisabledFeatures,
            BaseUrl = options.BaseUrl,
            ApiKey = options.ApiKey,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await client.EnsureStartedAsync(initOptions, cts.Token);
            await foreach (var _ in client.RunStreamingAsync("say hello", cts.Token))
            {
                if (responder.RemainingTurns["parent"] == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 30-second timeout expired — acceptable if the CLI reached the host at least once.
        }
        catch (InvalidOperationException)
        {
            // The Codex turn-completion path may surface "Codex turn failed" when our scripted
            // single-turn response shape doesn't match what the CLI's app-server expects.
            // That's an envelope-shape concern tracked by the JSON scenario-loader follow-up;
            // for Phase-1 scaffolding we treat the responder turn-queue as the ground-truth
            // signal that the CLI reached the host.
        }

        responder.RemainingTurns["parent"].Should().Be(0,
            "the Codex CLI is expected to make at least one OpenAI-shaped call against the mock host");
    }
}
