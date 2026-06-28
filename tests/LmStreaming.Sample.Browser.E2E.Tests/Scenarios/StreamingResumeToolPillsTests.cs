using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser-level regression for the streaming-resume "stuck / duplicated tool pills" bug
/// (user repro: a provider that makes MANY tool calls, switch away mid-stream, switch back →
/// the tool-call pills duplicate and the tool count never settles; a full page reload "fixes" it).
/// </summary>
/// <remarks>
/// <para>
/// Root cause (client live-resume render path, WebSocket transport only): on the wire, finalized
/// <c>tool_call</c> / <c>tool_call_update</c> messages from the real Anthropic provider arrive WITHOUT
/// a <c>runId</c> (verified across recorded traffic — <c>tool_call</c> carried one in 0 of 267 frames),
/// so <c>getMergeKey</c> keys them to <c>'default'</c>. The PERSISTED copy of the same message is
/// rehydrated by <c>loadMessagesFromBackend</c> (REST) stamped with the run's REAL id. After a
/// switch-away/back, <c>resumeStreamIfActive</c> re-opens the WebSocket and replays the in-flight run;
/// the runId-less replayed tool calls (key <c>'default'</c>) fail to merge with their rehydrated twins
/// (key = real runId) and render as duplicate, never-resolving pills. The fix
/// (<c>useChat.handleMessage</c>) stamps the active run id — learned from the <c>run_assignment</c>
/// frame, which the replay buffer delivers first — onto runId-less live content so the keys align.
/// </para>
/// <para>
/// This drives the REAL chat client over its default <b>WebSocket</b> transport (so the WS-only resume
/// path is exercised), against the InstructionChain scripted provider which emits 12 sequential tool
/// calls (each with a result) and then a long final text turn that keeps the backend run in-flight
/// while we switch conversations. The pooled agent keeps running after the socket is torn down, so
/// switching back re-subscribes and replays the in-flight run — the exact condition that triggered the
/// bug.
/// </para>
/// <para>
/// IMPORTANT — reproducing the real wire shape: the scripted test providers (unlike real Anthropic)
/// stamp a <c>runId</c> on every tool-call frame, so on their own they cannot reproduce the
/// runId-divergence the fix addresses. This test therefore strips <c>runId</c> from the server→client
/// tool-call frames (via Playwright WebSocket routing) so the browser sees exactly the documented
/// real-provider wire (runId-less tool calls, runId-bearing <c>run_assignment</c>). With this, the
/// fix is necessary and sufficient: GREEN (fix present) settles to 12 pills; RED (fix reverted) shows
/// the duplicated/never-settling count.
/// </para>
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class StreamingResumeToolPillsTests
{
    /// <summary>Number of tool calls the scripted run emits before its final text turn (user repro: 10-15).</summary>
    private const int ToolCallCount = 12;

    private readonly PlaywrightFixture _fixture;

    public StreamingResumeToolPillsTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test-anthropic")]
    [InlineData("test")]
    public async Task Many_tool_calls_resolve_after_switch_away_and_back(string providerMode)
    {
        // A single role (the default-mode parent agent) serves every turn: twelve tool-call turns,
        // each executed by a real local tool (calculate) so the multi-turn loop produces a result and
        // advances, followed by a deliberately long final text turn. The scripted SSE handler streams
        // in small delayed chunks, so a multi-thousand-word tail keeps the backend run in-flight for
        // several seconds — the window during which we switch away and back.
        var role = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"));

        for (var i = 0; i < ToolCallCount; i++)
        {
            var n = i;
            role = role.Turn(t => t.ToolCall("calculate", new { a = n, operation = "add", b = 1 }));
        }

        var responder = role
            .Turn(t => t.TextLen(6_000))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        // Reproduce the real-provider wire: strip runId from server→client tool-call frames (the
        // scripted providers stamp it; real Anthropic does not). run_assignment keeps its runId so the
        // client still learns the active run id (which the fix stamps onto the runId-less tool calls).
        await page.RouteWebSocketAsync(
            url => url.Contains("/ws?", StringComparison.Ordinal),
            ws =>
            {
                var server = ws.ConnectToServer();
                // server → client: strip runId from tool-call frames (text); pass binary through.
                server.OnMessage(frame =>
                {
                    if (frame.Text is { } serverText)
                    {
                        ws.Send(StripToolCallRunId(serverText));
                    }
                    else
                    {
                        ws.Send(frame.Binary ?? []);
                    }
                });
                // client → server: pass through unchanged.
                ws.OnMessage(frame =>
                {
                    if (frame.Text is { } clientText)
                    {
                        server.Send(clientText);
                    }
                    else
                    {
                        server.Send(frame.Binary ?? []);
                    }
                });
            });

        // RouteWebSocketAsync only intercepts WebSockets created on a document loaded AFTER it is
        // registered (it installs a page-init wrapper around WebSocket). OpenAsync already navigated,
        // so reload now to activate the interception before the SPA opens its socket on first send.
        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();

        // Open a fresh conversation so it registers a current thread id — without this the first
        // message streams on a thread that is never added to the sidebar (nothing to switch back to).
        // Mirrors how the real app and the other scenarios open a conversation.
        await page.NewChatButton().ClickAsync();

        // 1) Start the streaming run in conversation A.
        await page.SendMessageAsync("run twelve calculations, then write a long summary");
        await page.WaitForStreamActiveAsync();

        // 2) Wait until all 12 tool-call pills have streamed live. The run is now on its final, long
        //    text turn — still in-flight. Live streaming renders exactly one pill per call (the bug
        //    only manifests on the resume path), so the count is exactly 12 here.
        await page.ToolCallPills().WaitForCountAtLeastAsync(ToolCallCount, timeoutMs: 30_000);
        (await page.ToolCallPills().CountAsync())
            .Should()
            .Be(ToolCallCount, "live streaming renders exactly one pill per tool call (no duplication before any switch)");

        // Conversation A is added to the sidebar on its first send.
        await page.ConversationItems().WaitForCountAtLeastAsync(1);
        var threadIdA = await page.ConversationItems().First.GetAttributeAsync("data-thread-id");
        threadIdA.Should().NotBeNullOrEmpty();

        // 3) Switch AWAY to a brand-new chat. This tears down conversation A's WebSocket; its backend
        //    run keeps streaming the long final turn (the pooled agent is not bound to the socket).
        await page.NewChatButton().ClickAsync();
        await Assertions.Expect(page.ToolCallPills()).ToHaveCountAsync(0);

        // 4) Switch BACK to conversation A (the only started conversation in the sidebar). This loads
        //    persisted history (rehydrated tool calls carry the run's REAL id) and, because the run is
        //    still in-flight, re-opens the WebSocket to RESUME — replaying the runId-less tool calls.
        await page.ConversationItems().First.ClickAsync();

        // 5) PROOF the WebSocket resume path is actually exercised: the pooled backend run is still
        //    in-flight at switch-back, so resumeStreamIfActive (which only proceeds on the WebSocket
        //    transport) re-subscribes and replays. If the run had already finished there would be
        //    nothing to resume — making a vacuous pass impossible.
        var runStateJson = await GetRunStateAsync(page, threadIdA!);
        runStateJson
            .Should()
            .Contain("\"isInProgress\":true", "the pooled run must still be live at switch-back so the WebSocket resume path fires");

        // 6) The stream only goes idle if the RESUMED WebSocket delivered RunCompleted. Without an
        //    active resume the spinner stays stuck and this times out.
        await page.WaitForStreamIdleAsync(timeoutMs: 60_000);

        // The final summary text only arrives over the live resume (it was not yet persisted when we
        // switched away), so its presence confirms the resumed stream actually delivered new content.
        (await page.AssistantText().CountAsync())
            .Should()
            .BeGreaterThanOrEqualTo(1, "the resumed live stream delivered the final summary text after the replayed tool calls");

        // 7) THE ASSERTION: each tool call renders as exactly ONE pill after the resume. Without the
        //    runId-stamp fix, the WS-replayed (runId-less → 'default') tool calls fail to merge with
        //    the REST-rehydrated (real-runId) copies, so every pill duplicates and the tool count
        //    never settles (24, not 12) — the user-reported symptom.
        (await page.ToolCallPills().CountAsync())
            .Should()
            .Be(
                ToolCallCount,
                "the resumed run must MERGE replayed tool calls with rehydrated history, not duplicate them — "
                    + "a count above 12 is the stuck/duplicated-pill bug");

        responder.RemainingTurns["parent"]
            .Should()
            .Be(0, "the full scripted plan ran to completion server-side regardless of the client switch");

        await session.SaveSuccessScreenshotAsync(
            $"StreamingResume.Many_tool_calls_resolve_after_switch_away_and_back_{providerMode}");
    }

    /// <summary>
    /// Removes the <c>runId</c> property from tool-call WS frames (finalized, update, or result) to
    /// mirror the real Anthropic wire, where those messages arrive runId-less. Lifecycle frames
    /// (notably <c>run_assignment</c>, which carries the id the fix relies on), text, and reasoning are
    /// left untouched. <c>runId</c> is never the first property and <c>parentRunId</c> is not matched.
    /// </summary>
    private static string StripToolCallRunId(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var isToolFrame =
            text.Contains("\"$type\":\"tool_call\"", StringComparison.Ordinal)
            || text.Contains("\"$type\":\"tool_call_update\"", StringComparison.Ordinal)
            || text.Contains("\"$type\":\"tool_call_result\"", StringComparison.Ordinal)
            || text.Contains("\"$type\":\"tools_call\"", StringComparison.Ordinal)
            || text.Contains("\"$type\":\"tools_call_update\"", StringComparison.Ordinal);

        return isToolFrame
            ? Regex.Replace(text, ",\"[Rr]unId\":\"[^\"]*\"", string.Empty)
            : text;
    }

    /// <summary>
    /// Reads the backend in-memory run-state for a thread via the same REST endpoint the client's
    /// resume path uses, so the test can assert the run was genuinely in-flight at switch-back.
    /// </summary>
    private static Task<string> GetRunStateAsync(IPage page, string threadId)
    {
        return page.EvaluateAsync<string>(
            "async (tid) => { const r = await fetch(`${location.origin}/api/conversations/${encodeURIComponent(tid)}/run-state`, { headers: { 'Accept': 'application/json' } }); return await r.text(); }",
            threadId);
    }
}
