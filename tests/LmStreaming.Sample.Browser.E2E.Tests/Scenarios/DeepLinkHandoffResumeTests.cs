using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// AC6 — headless-daemon → human-takes-over-in-the-browser handoff, resumed live over the WebSocket
/// including a non-text (sub-agent / <c>Agent</c> tool-call) modality.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors what the <c>ConversationDaemon.Sample</c> does with the REST surface: it
/// <c>POST /api/conversations</c> to <b>provision</b> a thread (server mints the id), then
/// <c>POST /api/conversations/{threadId}/messages</c> to start a run — all headless, with NO browser
/// attached. A human then opens the conversation's deep link
/// <c>{ServerAddress}/?threadId={threadId}</c> (read from <c>window.location.search</c> at the site
/// root by <c>ChatLayout.vue</c>) <b>while the run is still in flight</b>, and must see both the prior
/// history and the in-progress sub-agent step resume and complete over the live stream.
/// </para>
/// <para>
/// Creating a reliable in-flight window WITHOUT the browser driving the initial turns: a text-only
/// assistant turn ends the multi-turn run, so the window turn must ALSO continue the loop. The parent's
/// first scripted turn is therefore a long text block <em>followed by</em> the <c>Agent</c> tool call in
/// the same turn — on the Anthropic wire these stream as two content blocks (text, then <c>tool_use</c>)
/// with <c>stop_reason=tool_use</c>, so the ~3s of streamed text keeps the run in flight while the
/// <c>tool_use</c> (turn 2's continuation) is still unstreamed. We deep-link in during that text window,
/// so the <c>Agent</c> tool-call pill is NOT yet in persisted history at connect and can only reach the
/// UI via the resumed WebSocket — making a static-reload false pass impossible.
/// </para>
/// <para>
/// The parent turns are driven by the standard E2E <see cref="ScriptedSseResponder"/> (role-matched on
/// the default mode's "helpful assistant" system prompt), exactly like
/// <c>StreamingResumeToolPillsTests</c> and <c>SubAgentLifecycleTests</c>; the daemon's POSTed message
/// text only starts the run (the scripted plan, not the prompt text, decides the turns). The sub-agent
/// is the nested-chain sub-agent from <c>PromptExamples.md</c> ("Sub-Agent Delegation"): the
/// <c>Agent</c> tool's <c>prompt</c> argument IS an instruction chain (<see cref="InnerChain"/>) that the
/// sub-agent (backed by the instruction-chain handler) consumes to call <c>calculate</c> and reply
/// "hi from agent" — which returns synchronously as the <c>Agent</c> tool result. "hi from agent" appears
/// nowhere in the parent script, so its presence in the expanded pill proves the nested chain executed
/// end to end and flowed back through the resumed UI.
/// </para>
/// <para>
/// <b>Provider choice — <c>test-anthropic</c> only (Fact, not a dual-wire Theory):</b> only the Anthropic
/// mock models "text block then <c>tool_use</c> block" as two content blocks in ONE assistant turn; the
/// OpenAI mock (<c>test</c>) encodes each plan message under its own <c>Choice.Index</c>, which does not
/// faithfully represent text-then-tool in a single turn. The Anthropic wire also streams text (a reliable
/// in-flight window), which is why the task designates it the primary provider here.
/// </para>
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class DeepLinkHandoffResumeTests
{
    /// <summary>Provider mode for the handoff — the Anthropic mock (streams text; see remarks for why not <c>test</c>).</summary>
    private const string ProviderMode = "test-anthropic";

    /// <summary>
    /// Word count for the parent's first-turn text block. At the scripted handler's 10-words / 5ms cadence
    /// this streams for ~3s, keeping the run in flight long enough to deep-link the browser in before the
    /// same turn's trailing <c>Agent</c> <c>tool_use</c> block is streamed. Matches the window size proven
    /// by <c>StreamingResumeToolPillsTests</c>.
    /// </summary>
    private const int InFlightWindowWords = 6_000;

    // The nested prompt handed to the sub-agent via the Agent tool's `prompt` argument (the
    // PromptExamples.md "Sub-Agent Delegation" chain): turn 1 calls `calculate`, turn 2 replies with the
    // text that ends the synchronous sub-run and becomes the Agent tool result.
    private const string InnerChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"sub-tool","id_message":"Sub-agent uses calculate","messages":[{"tool_call":[{"name":"calculate","args":{"a":2,"operation":"add","b":3}}]}]},{"id":"sub-text","id_message":"Sub-agent replies","messages":[{"text":"hi from agent"}]}]}<|instruction_end|>""";

    private readonly PlaywrightFixture _fixture;

    public DeepLinkHandoffResumeTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Deep_link_into_in_flight_daemon_run_resumes_sub_agent_live()
    {
        // Parent plan: (turn 1) a long text block that keeps the run in flight, immediately followed by
        // the Agent delegation in the SAME turn so the loop continues; (turn 2) the final summary. The
        // Agent tool call streams only after the ~3s text window, so a browser that connects during the
        // window sees it arrive live over the resume rather than loading it from history.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t =>
                t.TextLen(InFlightWindowWords)
                    .ToolCall("Agent", new { subagent_type = "general-purpose", prompt = InnerChain })
            )
            // Deliberately free of "hi from agent" — that phrase can only reach the UI from the
            // sub-agent's embedded chain, not from this scripted parent summary.
            .Turn(t => t.Text("Parent done: sub-agent finished."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            ProviderMode,
            responder.HandlerFor(ProviderMode),
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(loggerFactory)
        );
        var page = session.Page;

        // 1) Provision the conversation headlessly (the daemon's REST path). The browser is currently on
        //    a fresh, unrelated new chat — it is NOT subscribed to this thread's stream.
        var threadId = await ProvisionConversationAsync(page);

        // 2) Start the run headlessly by queueing the delegation message. Returns as soon as the input is
        //    accepted; the pooled agent drains and streams turn 1's long text with no client attached.
        await StartDaemonRunAsync(page, threadId);

        // 3) HANDOFF: deep-link the browser into the in-flight conversation. ChatLayout reads ?threadId=
        //    from window.location.search on mount, finds the provisioned thread, loads its history, and —
        //    because the pooled run is still live — re-opens the WebSocket to resume the stream.
        var deepLink = $"{session.Factory.ServerAddress.TrimEnd('/')}/?threadId={threadId}";
        await page.GotoAsync(deepLink);
        await page.Textarea().WaitForAsync();

        // (a) Prior message history renders: the daemon's user message is loaded from the backend. (A
        //     failed/not-found deep link would render no message group and time out here.)
        await page.UserMessageGroups().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);

        // PROOF the WebSocket resume path is genuinely exercised: the pooled run must still be in flight
        // at handoff. A finished conversation would be a static REST reload with nothing to resume, so
        // this rules out a vacuous pass. (Same run-state endpoint the client's resume path uses.)
        var runStateJson = await GetRunStateAsync(page, threadId);
        runStateJson
            .Should()
            .Contain(
                "\"isInProgress\":true",
                "the daemon's pooled run must still be live at deep-link so the WebSocket resume path fires"
            );

        // (b) The in-progress sub-agent step RESUMES live: the Agent tool-call pill (turn 2's continuation,
        //     unstreamed at connect) appears only via the resumed WebSocket, not from persisted history.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 30_000);
        var toolNames = await page.ToolCallPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-tool-name') ?? '')");
        toolNames.Should().Contain("Agent", "the delegation resumed over the live stream after the handoff");

        // The stream only goes idle once the RESUMED WebSocket delivers RunCompleted — the sub-agent ran
        // synchronously and the parent's second turn streamed. Without an active resume this times out.
        await page.WaitForStreamIdleAsync(timeoutMs: 60_000);

        // The parent's final summary arrived over the resumed stream (it was not yet produced at connect).
        await page.AssistantText().WaitForCountAtLeastAsync(1);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", assistantTexts)
            .Should()
            .Contain("sub-agent finished", "the parent's post-delegation summary streamed in after the handoff");

        // Decisive check: expand the Agent pill and confirm the sub-agent's own output is the tool result.
        // "hi from agent" appears nowhere in the parent script, so its presence proves the embedded nested
        // chain executed (calculate -> text) and flowed back through the resumed UI.
        await page.ToolCallPills().First.ClickAsync();
        var pillResult = page.Locator(".tool-call-result");
        await pillResult.First.WaitForAsync();
        var expanded = string.Join(" ", await pillResult.AllInnerTextsAsync());
        expanded
            .Should()
            .Contain(
                "hi from agent",
                "the expanded Agent tool result is the sub-agent's final text from the nested chain, resumed live"
            );

        responder.RemainingTurns["parent"]
            .Should()
            .Be(0, "the full scripted plan ran to completion server-side across the headless-start → deep-link handoff");

        await session.SaveSuccessScreenshotAsync("DeepLinkHandoffResume.sub_agent_live");
    }

    /// <summary>
    /// Provisions a conversation via <c>POST /api/conversations</c> from the page context (same-origin
    /// fetch, mirroring the daemon), and returns the server-minted thread id.
    /// </summary>
    private static async Task<string> ProvisionConversationAsync(IPage page)
    {
        return await page.EvaluateAsync<string>(
            @"async () => {
                const res = await fetch(`${location.origin}/api/conversations`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ workspaceId: 'default', providerId: 'test-anthropic', modeId: 'default' }),
                });
                if (!res.ok) throw new Error(`provision failed: ${res.status}`);
                const data = await res.json();
                return data.threadId;
            }");
    }

    /// <summary>
    /// Starts the run headlessly by queueing the delegation message via
    /// <c>POST /api/conversations/{threadId}/messages</c> (returns 202 Accepted once the input is durably
    /// recorded). The scripted responder — not this text — decides the streamed turns.
    /// </summary>
    private static async Task StartDaemonRunAsync(IPage page, string threadId)
    {
        _ = await page.EvaluateAsync(
            @"async (tid) => {
                const res = await fetch(`${location.origin}/api/conversations/${encodeURIComponent(tid)}/messages`, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ text: 'Delegate this task to a sub-agent, then summarize.' }),
                });
                if (!res.ok) throw new Error(`send failed: ${res.status}`);
            }",
            threadId);
    }

    /// <summary>
    /// Reads the backend in-memory run-state for a thread via the same REST endpoint the client's resume
    /// path uses, so the test can assert the run was genuinely in flight at the deep-link handoff.
    /// </summary>
    private static Task<string> GetRunStateAsync(IPage page, string threadId)
    {
        return page.EvaluateAsync<string>(
            "async (tid) => { const r = await fetch(`${location.origin}/api/conversations/${encodeURIComponent(tid)}/run-state`, { headers: { 'Accept': 'application/json' } }); return await r.text(); }",
            threadId);
    }

    private static SubAgentOptions BuildSubAgentOptions(ILoggerFactory loggerFactory)
    {
        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "EmbeddedChainSub",
                    // Marker only — behavior comes from the embedded chain in the prompt, not role dispatch.
                    SystemPrompt = "You are an embedded-chain sub-agent.",
                    AgentFactory = () => BuildEmbeddedChainAgent(loggerFactory),
                    MaxTurnsPerRun = 5,
                },
            },
            MaxConcurrentSubAgents = 5,
        };
    }

    // Sub-agent backed by the embedded-chain test handler (parses
    // <|instruction_start|>…<|instruction_end|> from the request), mirroring the sample's test-mode
    // agent construction. Anthropic-only, matching this test's test-anthropic provider.
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
            "MockAnthropicSub",
            anthropicClient,
            loggerFactory.CreateLogger<AnthropicAgent>());
    }
}
