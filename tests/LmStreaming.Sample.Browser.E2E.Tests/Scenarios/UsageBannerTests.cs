using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// #196 — conversation-wide token-usage banner. Drives the real Vue client against the scripted
/// <c>test-anthropic</c> backend, whose SSE emits a fixed <b>100 input / 50 output</b> tokens per
/// generation (<see cref="AnthropicSseStreamHttpContent"/>), so the banner totals are exact.
/// Covers: single-turn exact counts, additive multi-turn accumulation (never max'd), reload
/// restoration from the persisted aggregate, and sub-agent descendant usage folding into that
/// aggregate (the headline "incl. sub-agents" requirement).
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class UsageBannerTests
{
    private readonly PlaywrightFixture _fixture;

    public UsageBannerTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    // Nested chain the sub-agent runs via the Agent tool's `prompt`: a `calculate` turn then a text
    // turn. Each generation emits 100/50, so the sub-agent contributes usage above the parent's own.
    private const string InnerChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"sub-tool","id_message":"Sub-agent uses calculate","messages":[{"tool_call":[{"name":"calculate","args":{"a":2,"operation":"add","b":3}}]}]},{"id":"sub-text","id_message":"Sub-agent replies","messages":[{"text":"hi from agent"}]}]}<|instruction_end|>""";

    [Fact]
    public async Task Banner_shows_exact_tokens_accumulates_additively_and_survives_reload()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("First answer."))
            .Turn(t => t.Text("Second answer."))
            .Build();

        await using var session = await _fixture.OpenAsync("test-anthropic", responder.HandlerFor("test-anthropic"));
        var page = session.Page;

        // Turn 1: one generation = 100 in / 50 out -> Total 150.
        await page.SendMessageAsync("hello");
        await page.WaitForStreamIdleAsync();
        await page.UsageBanner().WaitForTextContainsAsync("Total: 150");
        var banner1 = await page.UsageBanner().InnerTextAsync();
        banner1.Should().Contain("In: 100").And.Contain("Out: 50");

        // Turn 2: additive across attempts -> 300 / 200 / 100 (NOT max'd like a single generation).
        await page.SendMessageAsync("again");
        await page.WaitForStreamIdleAsync();
        await page.UsageBanner().WaitForTextContainsAsync("Total: 300");
        var banner2 = await page.UsageBanner().InnerTextAsync();
        banner2.Should().Contain("In: 200").And.Contain("Out: 100");

        // Reload -> banner restored from the persisted aggregate (not recomputed from the empty stream).
        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();
        await page.UsageBanner().WaitForTextContainsAsync("Total: 300");

        await session.SaveSuccessScreenshotAsync("UsageBanner.exact_accumulate_reload");
    }

    [Fact]
    public async Task Sub_agent_usage_folds_into_the_conversation_wide_aggregate()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "general-purpose", prompt = InnerChain }))
            .Turn(t => t.Text("Parent summary: delegation finished."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            "test-anthropic",
            responder.HandlerFor("test-anthropic"),
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(loggerFactory));
        var page = session.Page;

        await page.SendMessageAsync("delegate to the sub-agent");
        // The synchronous Agent call blocks until the sub-agent runs its nested chain, so allow extra time.
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // The LIVE banner reflects the usage streamed to the client — at minimum the parent's own two
        // generations (300). (Today descendant usage is folded server-side and not streamed, so the live
        // value is exactly 300; asserting ">= 300" keeps this robust if live descendant streaming is added.)
        await page.UsageBanner().WaitForTextContainsAsync("Total:");
        var liveTotal = ParseTotal(await page.UsageBanner().InnerTextAsync());
        liveTotal.Should().BeGreaterThanOrEqualTo(300, "the parent's own generations are streamed to the banner");

        // Server-side ground truth: the persisted aggregate exceeds the parent-only 300 because the
        // sub-agent's generations were folded into the root conversation's total (#196 "incl. sub-agents").
        // Read the thread id from the conversations API (robust to sidebar rendering), then its usage.
        var aggregateTotal = await page.EvaluateAsync<long>(
            @"async () => {
                const lr = await fetch('/api/conversations');
                const body = await lr.json();
                const list = body.conversations ?? body ?? [];
                if (!list.length) return -2;
                const id = list[list.length - 1].threadId;
                const r = await fetch(`/api/conversations/${id}/usage`);
                if (!r.ok) return -1;
                return (await r.json()).totalTokens;
            }");
        aggregateTotal.Should().BeGreaterThan(
            300,
            "the sub-agent's relayed usage must fold into the conversation-wide aggregate, above the parent's own 300");

        // On reopen the banner is restored from that aggregate, so it now includes the descendant's tokens.
        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();
        await page.UsageBanner().WaitForTextContainsAsync("Total:");
        var reloadedTotal = ParseTotal(await page.UsageBanner().InnerTextAsync());
        reloadedTotal.Should().Be((int)aggregateTotal, "reopen renders the same persisted aggregate total");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync("UsageBanner.sub_agent_folds_into_aggregate");
    }

    private static int ParseTotal(string bannerText)
    {
        var match = Regex.Match(bannerText, @"Total:\s*(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    private static SubAgentOptions BuildSubAgentOptions(ILoggerFactory loggerFactory)
    {
        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "UsageSub",
                    SystemPrompt = "You are an embedded-chain sub-agent.",
                    AgentFactory = () => BuildEmbeddedChainAgent(loggerFactory),
                    MaxTurnsPerRun = 5,
                },
            },
            MaxConcurrentSubAgents = 5,
        };
    }

    // Sub-agent backed by the embedded-chain Anthropic test handler, which emits the same fixed
    // 100/50 usage per generation as the parent — so the descendant's contribution is measurable.
    private static IStreamingAgent BuildEmbeddedChainAgent(ILoggerFactory loggerFactory)
    {
        var handler = new AnthropicTestSseMessageHandler(
            loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test-mode/v1") };
        var anthropicClient = new AnthropicClient(
            httpClient,
            baseUrl: "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<AnthropicClient>());
        return new AnthropicAgent(
            "MockAnthropicUsageSub",
            anthropicClient,
            loggerFactory.CreateLogger<AnthropicAgent>());
    }
}
