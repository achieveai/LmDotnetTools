using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Scenarios;

/// <summary>
/// Skipped scaffold for the Copilot CLI investigation tracked in issue #14. Exercises the
/// BYOK env-var redirect contract (<c>COPILOT_PROVIDER_*</c>) by spawning <c>copilot</c>
/// directly against an <see cref="EphemeralHostFixture"/>-bound <see cref="MockProviderHostBuilder"/>.
///
/// <para>
/// Intentionally bypasses <c>CopilotAcpTransport</c> — that transport currently sets only
/// <c>COPILOT_BASE_URL</c> / <c>COPILOT_API_KEY</c>, not the full BYOK suite
/// (<c>COPILOT_PROVIDER_TYPE</c>, <c>COPILOT_PROVIDER_WIRE_API</c>, <c>COPILOT_PROVIDER_API_KEY</c>, ...).
/// Wiring those into <c>CopilotSdkOptions</c> + transport is the follow-up E2E issue's job;
/// this scaffold answers the empirical question of <em>whether</em> BYOK works at all on the
/// CLI version under test.
/// </para>
///
/// <para>
/// See <c>samples/MockProviderHost/README.md#copilot-cli-integration-investigation-issue-14</c>
/// for the full probe matrix this test instantiates.
/// </para>
/// </summary>
public sealed class CopilotSdkAgainstMockTests
{
    [SkippableFact]
    public async Task Copilot_cli_routes_through_mock_host_via_byok_env_overrides()
    {
        // Investigation-only: the CLI invocation shape (`--print` vs interactive vs `--acp`)
        // and the BYOK bypass behavior are the subject of issue #14. Run with
        // LMDOTNET_RUN_COPILOT_E2E=1 once a stable probe contract is recorded in the README.
        Skip.If(
            Environment.GetEnvironmentVariable("LMDOTNET_RUN_COPILOT_E2E") != "1",
            "Set LMDOTNET_RUN_COPILOT_E2E=1 to run the Copilot CLI investigation probe.");

        var (available, reason) = CopilotCliPrerequisites.Detect();
        Skip.IfNot(available, reason ?? "Copilot CLI not available.");

        // OpenAI-shaped responder: matches `COPILOT_PROVIDER_TYPE=openai` +
        // `COPILOT_PROVIDER_WIRE_API=completions`. The responder draining its parent turn
        // is the ground-truth signal that the CLI actually reached /v1/chat/completions
        // (and therefore that BYOK bypassed GitHub OAuth).
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", _ => true)
                .Turn(t => t.Text("hello from the scripted parent"))
            .Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        // BYOK env-var contract (see README probe matrix — Run A). All values are
        // documented at https://docs.github.com/en/copilot/github-copilot-cli (env section).
        var env = new Dictionary<string, string>
        {
            ["COPILOT_PROVIDER_BASE_URL"] = fixture.BaseUrl + "/v1",
            ["COPILOT_PROVIDER_TYPE"] = "openai",
            ["COPILOT_PROVIDER_API_KEY"] = "mock-token",
            ["COPILOT_PROVIDER_WIRE_API"] = "completions",
            ["COPILOT_PROVIDER_MODEL_ID"] = "gpt-4o-mini",
            // Assert no egress to api.githubcopilot.com — if the CLI ignores BYOK and falls
            // back to the public endpoint, OFFLINE=1 should make the call fail loudly rather
            // than silently succeeding against the wrong host.
            ["COPILOT_OFFLINE"] = "1",
        };

        // The non-interactive invocation shape is itself part of the investigation. The probe
        // matrix in the README documents the candidate args; the first contributor to land on
        // a CLI-equipped machine fills the actual invocation in here.
        // Until then this scaffold sets up everything else (host, env, gating, assertion) so
        // only the ProcessStartInfo.Arguments line below needs editing.
        var psi = new ProcessStartInfo
        {
            FileName = "copilot",
            Arguments = "--print \"say hello\"", // PROBE TODO: confirm the CLI's non-interactive arg shape against `copilot help`.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            Skip.If(proc is null, "Failed to start copilot process — investigation cannot proceed on this machine.");

            // Poll the responder; success is the parent turn draining (the CLI hit the mock
            // and consumed the scripted turn). Time-out is the red-light verdict for the
            // README probe matrix.
            while (!cts.IsCancellationRequested)
            {
                if (responder.RemainingTurns["parent"] == 0)
                {
                    break;
                }
                if (proc.HasExited)
                {
                    break;
                }
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Timeout is a real failure here — the test purpose is to verify BYOK reaches
            // the mock. Surfacing it via the assertion below gives a clearer message than
            // letting OCE propagate.
        }
        finally
        {
            if (proc is { HasExited: false })
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
            proc?.Dispose();
        }

        responder.RemainingTurns["parent"].Should().Be(0,
            "the Copilot CLI under test should reach the mock host's /v1/chat/completions when "
            + "BYOK env vars are set (COPILOT_PROVIDER_BASE_URL/_TYPE/_API_KEY/_WIRE_API). "
            + "If this assertion fails, see the probe matrix in samples/MockProviderHost/README.md "
            + "to determine whether to investigate the CLI invocation shape, the env-var contract, "
            + "or to record a RED verdict for the follow-up E2E issue.");
    }
}
