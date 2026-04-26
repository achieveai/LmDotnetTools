using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Infrastructure;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.E2E.Tests.Scenarios;

/// <summary>
/// Real-process E2E: spawn the Claude Agent SDK CLI pointed at a mock provider host bound to
/// a real TCP port, and verify the CLI received and rendered the scripted assistant turn.
///
/// These tests are skipped when the CLI is not installed on the host so CI without the SDK
/// can still run the rest of the suite.
/// </summary>
public sealed class ClaudeAgentSdkAgainstMockTests
{
    [SkippableFact]
    public async Task Cli_routes_through_mock_host_via_BaseUrl_and_AuthToken_overrides()
    {
        // Real-CLI E2E is opt-in: the protocol-level handshake the CLI performs against
        // /v1/messages depends on CLI version and is the subject of a follow-up issue.
        // Run with LMDOTNET_RUN_CLAUDE_E2E=1 once a stable scenario contract is recorded.
        Skip.If(
            Environment.GetEnvironmentVariable("LMDOTNET_RUN_CLAUDE_E2E") != "1",
            "Set LMDOTNET_RUN_CLAUDE_E2E=1 to run real-CLI E2E tests.");

        var (available, reason) = ClaudeCliPrerequisites.Detect();
        Skip.IfNot(available, reason ?? "Claude Agent SDK CLI not available.");

        // The role definition is sufficient to consume the first /v1/messages call from the
        // CLI; we treat the responder's turn queue as the ground-truth signal that the CLI
        // reached the mock host. We do not assert on the CLI's stdout decoding here — that's
        // the subject of richer E2E scenarios tracked in the follow-up issues.
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", _ => true)
                .Turn(t => t.Text("hello from the scripted parent"))
            .Build();
        await using var fixture = await RealPortHostFixture.StartAsync(responder);

        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            BaseUrl = fixture.BaseUrl + "/v1",
            AuthToken = "mock-token",
            DisableExperimentalBetas = true,
            ProcessTimeoutMs = 30000,
            DisableCheckpoints = true,
            DisableSessionPersistence = true,
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await client.StartAsync(request, cts.Token);
            await foreach (var _ in client.SendMessagesAsync([], cts.Token))
            {
                if (responder.RemainingTurns["parent"] == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Acceptable: we only need the CLI to reach the host once.
        }

        responder.RemainingTurns["parent"].Should().Be(0,
            "the CLI is expected to make at least one /v1/messages call against the mock host");
    }
}

