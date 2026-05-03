using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Scenarios;

/// <summary>
/// Real-process E2E: spawns the Claude Agent SDK CLI pointed at a mock provider host bound to
/// a real TCP port, and verifies the CLI both reaches the host AND surfaces the scripted
/// assistant text downstream of <see cref="ClaudeAgentSdkClient.SendMessagesAsync"/>.
///
/// Skipped when the CLI is not installed on the host so CI without the SDK can still run the
/// rest of the suite.
/// </summary>
public sealed class ClaudeAgentSdkAgainstMockTests
{
    [SkippableFact]
    public async Task Cli_routes_through_mock_host_and_renders_scripted_text()
    {
        // Real-CLI E2E is opt-in: gate on a single env var so CI can shard which agents need
        // the heavy "spawn the actual SDK" path.
        Skip.If(
            Environment.GetEnvironmentVariable("LMDOTNET_RUN_CLAUDE_E2E") != "1",
            "Set LMDOTNET_RUN_CLAUDE_E2E=1 to run real-CLI E2E tests.");

        var (available, reason) = ClaudeCliPrerequisites.Detect();
        Skip.IfNot(available, reason ?? "Claude Agent SDK CLI not available.");

        // Distinctive marker so the assertion can't pass on cached/stub output. Issue #29 was
        // a "queue drained but no text rendered" failure — the only way to catch that
        // regression is to assert on the text the parser actually surfaced.
        const string markerText = "claude-mock-marker-9d2f3";
        // The Claude Agent SDK CLI may issue more than one /v1/messages call per logical turn
        // (e.g. a session-init probe followed by the user-visible reply). Enqueue a small
        // pool of identical turns so whichever request is the user-visible one carries the
        // marker. The assertion is "marker text present in rendered output", not "exactly N
        // requests served".
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", _ => true)
                .Turn(t => t.Text(markerText))
                .Turn(t => t.Text(markerText))
                .Turn(t => t.Text(markerText))
            .Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);

        // Anthropic SDK / Claude Agent SDK CLI append "/v1/messages" to ANTHROPIC_BASE_URL
        // themselves — the configured value MUST NOT end in /v1. The previous version of this
        // test used `BaseUrl + "/v1"` which produced "/v1/v1/messages" and 404'd silently.
        // CLI v0.1.55 does not recognize --no-checkpoints / --no-session-persistence; setting
        // those options would cause the CLI to fail with "unknown option" before any /v1/messages
        // call. They are intentionally omitted here.
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            BaseUrl = fixture.BaseUrl,
            AuthToken = "mock-token",
            DisableExperimentalBetas = true,
            ProcessTimeoutMs = 30000,
        };

        await using var client = new ClaudeAgentSdkClient(options);
        var request = new ClaudeAgentSdkRequest
        {
            ModelId = "claude-test",
            SystemPrompt = "You are a helpful assistant.",
            MaxTurns = 1,
            InputMessages =
            [
                new { role = "user", content = "say hello" },
            ],
        };

        await client.StartAsync(request);
        var userMessage = new TextMessage
        {
            Role = Role.User,
            Text = "say hello",
        };

        var renderedText = new System.Text.StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await foreach (var message in client.SendMessagesAsync([userMessage], cts.Token))
            {
                if (message is TextMessage tm && !string.IsNullOrEmpty(tm.Text))
                {
                    _ = renderedText.Append(tm.Text);
                }

                if (renderedText.ToString().Contains(markerText, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Fall through to the assertions — the assertions describe the exact failure mode
            // (host never reached vs. text never rendered) far better than a bare timeout.
        }

        responder.RemainingTurns["parent"].Should().BeLessThan(3,
            "the CLI is expected to make at least one /v1/messages call against the mock host");
        renderedText.ToString().Should().Contain(markerText,
            "the scripted assistant turn must round-trip through the SDK as a TextMessage so chat clients render content (issue #29)");
    }
}
