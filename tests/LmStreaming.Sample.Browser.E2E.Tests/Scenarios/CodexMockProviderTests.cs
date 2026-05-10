using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser-level regression for the Codex mock Responses transport: embedded instruction
/// chains should drive the rendered assistant text instead of the mock host's fallback.
/// </summary>
/// <remarks>
/// Skipped unless <c>LMDOTNET_RUN_CODEX_E2E=1</c> and a <c>codex</c> CLI binary is reachable on
/// PATH or via <c>CODEX_CLI_PATH</c>. This mirrors the other CLI-backed browser E2E tests so
/// machines without the external CLI skip cleanly.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class CodexMockProviderTests
{
    private readonly PlaywrightFixture _fixture;

    public CodexMockProviderTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Codex_mock_honors_instruction_chain_in_chat_ui()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("LMDOTNET_RUN_CODEX_E2E"), "1", StringComparison.Ordinal),
            "Set LMDOTNET_RUN_CODEX_E2E=1 to run codex-mock browser E2E tests."
        );
        Skip.IfNot(CodexCliIsAvailable(), "Codex CLI not found on PATH or via CODEX_CLI_PATH.");

        await using var factory = new BrowserWebAppFactory("codex-mock", builder: null);
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(factory.ServerAddress);
        await page.Textarea().WaitForAsync();
        await page.NewChatButton().ClickAsync();

        const string marker = "BROWSER_CODEX_CHAIN_OUTPUT";
        const string instructionChain = """
            <|instruction_start|>
            {
              "instruction_chain": [
                {
                  "id": "browser-codex-chain",
                  "id_message": "browser codex instruction chain should win",
                  "messages": [
                    {
                      "text": "BROWSER_CODEX_CHAIN_OUTPUT"
                    }
                  ]
                }
              ]
            }
            <|instruction_end|>
            """;

        await page.SendMessageAsync(instructionChain);
        await page.WaitForStreamIdleAsync(timeoutMs: 90_000);

        await page.AssistantText().WaitForCountAtLeastAsync(1, timeoutMs: 30_000);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", assistantTexts).Should().Contain(marker);
    }

    [SkippableFact]
    public async Task Codex_mock_discovers_sample_tools_and_renders_function_call_pills()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("LMDOTNET_RUN_CODEX_E2E"), "1", StringComparison.Ordinal),
            "Set LMDOTNET_RUN_CODEX_E2E=1 to run codex-mock browser E2E tests."
        );
        Skip.IfNot(CodexCliIsAvailable(), "Codex CLI not found on PATH or via CODEX_CLI_PATH.");

        await using var factory = new BrowserWebAppFactory("codex-mock", builder: null);
        await using var context = await _fixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(factory.ServerAddress);
        await page.Textarea().WaitForAsync();
        await page.NewChatButton().ClickAsync();

        const string marker = "BROWSER_CODEX_TOOL_FLOW_COMPLETE";
        const string discoveryMarker = "BROWSER_CODEX_TOOLS_DISCOVERED";
        const string instructionChain = """
            <|instruction_start|>
            {
              "instruction_chain": [
                {
                  "id": "browser-codex-tool-chain",
                  "id_message": "browser codex tool chain",
                  "messages": [
                    {
                      "text": "BROWSER_CODEX_TOOLS_DISCOVERED"
                    },
                    {
                      "tools_list": {}
                    },
                    {
                      "tool_call": [
                        {
                          "name": "calculate",
                          "args": {
                            "a": 7,
                            "operation": "multiply",
                            "b": 6
                          }
                        }
                      ]
                    },
                    {
                      "tool_call": [
                        {
                          "name": "get_weather",
                          "args": {
                            "location": "Seattle"
                          }
                        }
                      ]
                    },
                    {
                      "text": "BROWSER_CODEX_TOOL_FLOW_COMPLETE"
                    }
                  ]
                }
              ]
            }
            <|instruction_end|>
            """;

        await page.SendMessageAsync(instructionChain);
        await page.WaitForStreamIdleAsync(timeoutMs: 120_000);

        await page.ToolCallPills().WaitForCountAtLeastAsync(2, timeoutMs: 45_000);
        var toolNames = await page.ToolCallNamesAsync();
        toolNames.Should().Contain(name => name.Contains("calculate", StringComparison.OrdinalIgnoreCase));
        toolNames.Should().Contain(name => name.Contains("get_weather", StringComparison.OrdinalIgnoreCase));

        await page.AssistantText().WaitForCountAtLeastAsync(1, timeoutMs: 30_000);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        var combined = string.Join(" ", assistantTexts);
        combined.Should().Contain(discoveryMarker);
        combined.Should().Contain("calculate");
        combined.Should().Contain("get_weather");
        combined.Should().Contain(marker);
    }

    private static bool CodexCliIsAvailable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
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
        var executableNames = OperatingSystem.IsWindows() ? new[] { "codex.exe", "codex.cmd", "codex.ps1" } : ["codex"];

        foreach (var directory in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var executableName in executableNames)
            {
                if (File.Exists(Path.Combine(directory, executableName)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
