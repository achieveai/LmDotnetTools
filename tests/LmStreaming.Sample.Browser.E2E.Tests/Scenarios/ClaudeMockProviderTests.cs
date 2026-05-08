using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser-level regression for issue #29: drives the real Claude CLI against the
/// in-process MockProviderHost via the <c>claude-mock</c> provider mode and asserts the
/// chat client renders non-empty assistant text. This is the Playwright counterpart to
/// the SDK-level <c>ClaudeAgentSdkAgainstMockTests</c> in
/// <c>tests/MockProviderHost.E2E.Tests/</c>: the SDK test pins the protocol contract,
/// this test pins the UI rendering of the same flow so the silent-completion failure
/// mode is caught at both layers.
/// </summary>
/// <remarks>
/// Skipped unless <c>LMDOTNET_RUN_CLAUDE_E2E=1</c> AND a <c>claude</c> CLI binary is
/// reachable on PATH (or via <c>CLAUDE_CLI_PATH</c>). Mirrors the gating of the SDK-level
/// test so machines without the CLI installed (most CI runners today) skip cleanly
/// rather than fail.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class ClaudeMockProviderTests
{
    private readonly PlaywrightFixture _fixture;

    public ClaudeMockProviderTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Claude_mock_renders_assistant_text_in_chat_ui()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable("LMDOTNET_RUN_CLAUDE_E2E"),
                "1",
                StringComparison.Ordinal),
            "Set LMDOTNET_RUN_CLAUDE_E2E=1 to run claude-mock browser E2E tests.");
        Skip.IfNot(ClaudeCliIsAvailable(), "Claude CLI not found on PATH or via CLAUDE_CLI_PATH.");

        await using var factory = new BrowserWebAppFactory("claude-mock", builder: null);
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(factory.ServerAddress);
        await page.Textarea().WaitForAsync();

        await page.SendMessageAsync("say hello");
        await page.WaitForStreamIdleAsync(timeoutMs: 60_000);

        await page.AssistantText().WaitForCountAtLeastAsync(1);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        var combined = string.Join(" ", assistantTexts).Trim();
        combined.Should().NotBeEmpty(
            "issue #29: claude-mock previously completed silently with no rendered text");
    }

    private static bool ClaudeCliIsAvailable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return true;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return false;
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var exeNames = OperatingSystem.IsWindows()
            ? new[] { "claude.exe", "claude.cmd", "claude.ps1" }
            : ["claude"];

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in exeNames)
            {
                if (File.Exists(Path.Combine(dir, name)))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
