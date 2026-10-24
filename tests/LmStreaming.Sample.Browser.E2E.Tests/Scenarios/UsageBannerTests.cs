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
        await page.SelectDeveloperViewAsync();

        // Turn 1: one generation = 100 in / 50 out -> Total 150.
        // #265: SendMessageAsync is a bare fill+click with no wait, so WaitForStreamIdleAsync could
        // in principle be entered before the stop button has even rendered. WaitForStreamIdleAsync
        // itself is self-guarding against that not-yet-started/idle ambiguity (see its remarks in
        // DomAssertions.cs); no extra wait is needed here.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sub_agent_usage_folds_into_the_conversation_wide_aggregate(bool deliverInterimSnapshot)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "general-purpose", prompt = InnerChain }))
            .Turn(t => t.Text("Parent summary: delegation finished."))
            .Build();

        using var parentHandler = new FinalParentTurnGate(
            responder.HandlerFor("test-anthropic"),
            deliverInterimSnapshot
        );
        await using var session = await _fixture.OpenAsync(
            "test-anthropic",
            parentHandler,
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(loggerFactory)
        );
        var page = session.Page;
        await page.SelectDeveloperViewAsync();

        await page.SendMessageAsync("delegate to the sub-agent");
        if (deliverInterimSnapshot)
        {
            try
            {
                // Capture a real durable snapshot after both child calls but before the parent's final call.
                // Deliver it once after completion to reproduce a read racing the asynchronous usage writer.
                await parentHandler.FinalTurnEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                var interimJson = await page.EvaluateAsync<string>(
                    @"async () => {
                        const signal = AbortSignal.timeout(30000);
                        while (!signal.aborted) {
                            const body = await (await fetch('/api/conversations', { signal })).json();
                            const list = body.conversations ?? body ?? [];
                            if (list.length) {
                                const r = await fetch(`/api/conversations/${list[list.length - 1].threadId}/usage`, { signal });
                                if (r.ok) {
                                    const usage = await r.json();
                                    if (usage.totalTokens === 450) return JSON.stringify(usage);
                                }
                            }
                            await new Promise(requestAnimationFrame);
                        }
                        throw new Error('Expected the real interim usage snapshot of 450 tokens');
                    }"
                );
                var interimDelivered = 0;
                await page.RouteAsync(
                    "**/api/conversations/*/usage",
                    async route =>
                    {
                        if (
                            route.Request.Headers.ContainsKey("x-usage-banner-probe")
                            && Interlocked.Exchange(ref interimDelivered, 1) == 0
                        )
                        {
                            await route.FulfillAsync(
                                new RouteFulfillOptions
                                {
                                    Status = 200,
                                    ContentType = "application/json",
                                    Body = interimJson,
                                }
                            );
                        }
                        else
                        {
                            await route.ContinueAsync();
                        }
                    }
                );
            }
            finally
            {
                parentHandler.ReleaseFinalTurn.TrySetResult();
            }
        }
        // The synchronous Agent call blocks until the sub-agent runs its nested chain, so allow extra time.
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // The LIVE banner now reflects the folded conversation-wide total (#196, BUG 1): the sub-agent's
        // relayed usage is broadcast to the parent run's subscribers as a live ConversationUsageMessage
        // frame. Two parent calls plus two child calls, each 100 input / 50 output, total exactly 600.
        await page.UsageBanner().WaitForTextContainsAsync("Total: 600");
        var liveTotal = ParseTotal(await page.UsageBanner().InnerTextAsync());
        liveTotal.Should().Be(600, "all four provider calls fold into the LIVE banner via the usage frame");

        // UI idle is not a durability boundary: the serialized usage writer can still expose the 450
        // snapshot preceding the parent's final call. Wait for the exact complete fixture, not merely
        // any descendant contribution (>300), before using the persisted aggregate as reload ground truth.
        var aggregateTotal = await page.EvaluateAsync<long>(
            @"async () => {
                const signal = AbortSignal.timeout(30000);
                while (!signal.aborted) {
                    const lr = await fetch('/api/conversations', { signal });
                    const body = await lr.json();
                    const list = body.conversations ?? body ?? [];
                    if (list.length) {
                        const id = list[list.length - 1].threadId;
                        const r = await fetch(`/api/conversations/${id}/usage`, { signal, headers: { 'X-Usage-Banner-Probe': 'true' } });
                        if (r.ok) {
                            const total = (await r.json()).totalTokens;
                            if (total === 600) return total;
                        }
                    }
                    await new Promise(requestAnimationFrame);
                }
                throw new Error('Expected all four provider calls to persist 600 tokens');
            }"
        );
        aggregateTotal.Should().Be(600, "both parent and both child calls must be durable before reload");

        // On reopen the banner is restored from that aggregate, so it now includes the descendant's tokens.
        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();
        await page.UsageBanner().WaitForTextContainsAsync("Total: 600");
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

    private sealed class FinalParentTurnGate(HttpMessageHandler inner, bool enabled) : DelegatingHandler(inner)
    {
        private int _requests;
        public TaskCompletionSource FinalTurnEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFinalTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (enabled && Interlocked.Increment(ref _requests) == 2)
            {
                FinalTurnEntered.TrySetResult();
                await ReleaseFinalTurn.Task.WaitAsync(ct);
            }
            return await base.SendAsync(request, ct);
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseFinalTurn.TrySetResult();
            base.Dispose(disposing);
        }
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
        var handler = new AnthropicTestSseMessageHandler(loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test-mode/v1") };
        var anthropicClient = new AnthropicClient(
            httpClient,
            baseUrl: "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<AnthropicClient>()
        );
        return new AnthropicAgent(
            "MockAnthropicUsageSub",
            anthropicClient,
            loggerFactory.CreateLogger<AnthropicAgent>()
        );
    }
}
