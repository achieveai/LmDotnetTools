using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// End-to-end coverage of the FULL presentation-only sub-agent focus flow (WI #194, Task 9) against
/// the REAL runtime (a genuine <c>SubAgentManager</c> on a pooled <c>MultiTurnAgentLoop</c>), driven
/// by the scripted-SSE E2E harness. A single scenario chains every seam the switch/focus feature
/// depends on:
/// <list type="number">
///   <item><b>Spawn.</b> The parent's first turn issues an <c>Agent</c> tool call with
///   <c>run_in_background: true</c>, so a real background child is created and its GUID
///   <c>agent_id</c> is parsed from the spawn receipt (the parent <c>/ws</c> stays OPEN so the
///   pooled parent — and its <c>SubAgentManager</c> — remains resolvable for the REST + focus calls).</item>
///   <item><b>REST list.</b> <c>GET /api/conversations/{threadId}/subagents</c> projects
///   <c>SubAgentManager.ListAgents()</c> into <c>SubAgentSummary</c> DTOs, so the child appears with
///   its <c>agentId</c>, <c>template</c> (<c>bg_worker</c>), child <c>threadId</c>
///   (<c>subagent-{agentId}</c>) and a lifecycle <c>status</c>.</item>
///   <item><b>Persistence / replay source.</b> <c>GET /api/conversations/subagent-{agentId}/messages</c>
///   returns the child's persisted transcript (the wired default sub-agent store) — the history a
///   focused view replays from.</item>
///   <item><b>Focus + relay + restart streaming.</b> Connecting <c>/ws/subagent</c> to the (now
///   finished) child and sending one follow-up frame relays through
///   <c>SubAgentManager.SendMessageAsync</c> in background mode, which RESTARTS the finished child;
///   the child's second turn streams back over the focused socket and ends with the <c>done</c>
///   sentinel.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why focus-then-send (not focus-a-still-running child):</b> a scripted background child completes
/// near-instantly, so racing a fresh <c>/ws/subagent</c> connection against the child's FIRST run is
/// inherently flaky. This scenario instead lets the first run finish (a subscriber joining a completed
/// run gets no replay and therefore no premature <c>done</c>), then relays a follow-up that restarts
/// the child — deterministically exercising focus subscribe + relay-send + restart streaming without a
/// race. Determinism is preserved throughout by awaiting frames and polling REST by CONDITION (bounded
/// by the harness 30s timeout); there are no fixed sleeps.
/// </para>
/// <para>
/// <b>Why the panel UI is covered by vitest, not browser E2E:</b> the <c>Browser.E2E</c> scripted
/// backend cannot drive a real <c>SubAgentManager</c> (its GUID <c>agent_id</c>s are minted at runtime
/// and its children run out-of-band), so a browser scenario cannot deterministically observe a live
/// focus/switch against real child lifetimes. The browser-observable panel UX (list rendering,
/// selection, transcript load, focus toggle) is therefore covered by the Vue component/composable
/// vitest suites — <c>SubAgentListPanel.test.ts</c>, <c>useSubAgentPanel.test.ts</c>, and
/// <c>subAgentsApi.test.ts</c> — while THIS test proves the server-side runtime contract those
/// component tests mock.
/// </para>
/// </remarks>
public sealed class SubAgentFocusFlowTests
{
    private const string WorkerMarker = "You are the focus-flow background worker sub-agent";
    private const string FirstAnswer = "First background answer.";
    private const string SecondAnswer = "Second answer after follow-up.";

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Focus_flow_lists_persists_and_streams_restarted_subagent(string providerMode)
    {
        // 1) Scripted plan: a two-turn background worker (turn 1 = first answer, turn 2 = the
        //    restart answer) plus a parent that spawns it in the background then acknowledges. Both
        //    child runs share the ScriptedBuilder's single plan queue, so the restart consumes turn 2.
        var responder = ScriptedSseResponder.New()
            .ForRole("bg-worker", ctx => ctx.SystemPromptContains(WorkerMarker))
                .Turn(t => t.Text(FirstAnswer))
                .Turn(t => t.Text(SecondAnswer))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "bg_worker",
                        prompt = "do work",
                        name = "bg1",
                        run_in_background = true,
                    }))
                .Turn(t => t.Text("Parent kicked off the background worker."))
            .Build();

        var builder = new ScriptedBuilder(
            responder,
            subAgentFactory: (_, providerAgentFactory) => new SubAgentOptions
            {
                Templates = new Dictionary<string, SubAgentTemplate>
                {
                    // AgentFactory = providerAgentFactory: a restart recreates the child provider from
                    // the SAME scripted responder, so its next scripted turn (turn 2) is served.
                    ["bg_worker"] = new SubAgentTemplate
                    {
                        Name = "BackgroundWorker",
                        SystemPrompt = WorkerMarker,
                        AgentFactory = providerAgentFactory,
                        MaxTurnsPerRun = 5,
                    },
                },
                MaxConcurrentSubAgents = 5,
            });

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"subagent-focusflow-{providerMode}-{Guid.NewGuid():N}";

        // 2) Parent /ws: send a message that spawns the background child, parse its agent_id from the
        //    spawn receipt. Keep the parent connection OPEN across the REST + /ws/subagent calls so the
        //    pooled parent (and its SubAgentManager) stays unambiguously resolvable.
        var parentSocket = await factory.ConnectWebSocketAsync(threadId);
        await using var parentClient = new WebSocketTestClient(parentSocket);

        await parentClient.SendUserMessageAsync("spawn a background worker");

        string agentId;
        using (var parentFrames = await parentClient.CollectUntilDoneAsync(TimeSpan.FromSeconds(30)))
        {
            parentFrames.ToolCallNames().Should().Contain("Agent");

            var receipt = parentFrames.ToolCallResults()
                .FirstOrDefault(r => r.Contains("agent_id", StringComparison.Ordinal));
            receipt.Should().NotBeNull("a background spawn returns a JSON receipt carrying the agent id");

            using var receiptDoc = JsonDocument.Parse(receipt!);
            agentId = receiptDoc.RootElement.GetProperty("agent_id").GetString()!;
            agentId.Should().NotBeNullOrWhiteSpace();
        }

        using var http = factory.CreateClient();

        // 3) REST list: deterministically wait for the background child's first run to finish + persist
        //    (poll by condition, bounded by the harness timeout — no sleeps), then assert its projected
        //    SubAgentSummary shape. Proves ListSubAgents surfaces the real spawned child.
        var summary = await WaitForSubAgentAsync(
            http, threadId, agentId, status => status == "completed", TimeSpan.FromSeconds(30));

        summary.GetProperty("agentId").GetString().Should().Be(agentId);
        summary.GetProperty("template").GetString().Should().Be("bg_worker");
        summary.GetProperty("threadId").GetString().Should().Be($"subagent-{agentId}");
        summary.GetProperty("status").GetString().Should().NotBeNullOrWhiteSpace();

        // 4) Persistence / replay source: the child's own thread has a non-empty persisted transcript
        //    (the history a focused view replays from). Proves the wired default sub-agent store captured
        //    the child's run under subagent-{agentId}.
        var childThreadId = $"subagent-{agentId}";
        using var messagesResponse = await http.GetAsync($"/api/conversations/{childThreadId}/messages");
        messagesResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var messagesBody = await messagesResponse.Content.ReadAsStringAsync();
        using (var messagesDoc = JsonDocument.Parse(messagesBody))
        {
            messagesDoc.RootElement.ValueKind.Should().Be(
                JsonValueKind.Array, "the child transcript endpoint returns a JSON array");

            // Exact substring matching over the normalized/persisted content is brittle across wire
            // formats, so assert the durable fact (a non-empty transcript) and surface the body on failure.
            messagesDoc.RootElement.GetArrayLength().Should().BeGreaterThan(
                0,
                "the child's persisted transcript is the focus replay source; response body was: {0}",
                messagesBody);
        }

        // 5) Focus + relayed follow-up: connect /ws/subagent to the FINISHED child and send one message.
        //    The relay restarts the child through SubAgentManager.SendMessageAsync (background), and the
        //    child's second scripted turn streams back over the focused socket, ending with `done`.
        var childSocket = await factory.ConnectSubAgentWebSocketAsync(threadId, agentId);
        await using var childClient = new WebSocketTestClient(childSocket);

        await childClient.SendUserMessageAsync("continue please");
        using (var childFrames = await childClient.CollectUntilDoneAsync(TimeSpan.FromSeconds(30)))
        {
            childFrames.ConcatText().Should().Contain(
                SecondAnswer,
                "focusing the finished child and relaying a follow-up must restart it and stream its next turn");

            childFrames.OfMessageType("done").Should().NotBeEmpty(
                "the restarted child's run completes, so the focused stream must end with a done sentinel");
        }

        // 6) Cleanup is deterministic via the compiler-ordered disposals: childClient (await using) first,
        //    then parentClient (await using), then factory (using) — child socket closed before the parent
        //    connection, and both before the host is torn down.
    }

    /// <summary>
    /// Polls <c>GET /api/conversations/{threadId}/subagents</c> until the sub-agent identified by
    /// <paramref name="agentId"/> is present AND its <c>status</c> satisfies
    /// <paramref name="statusPredicate"/>, then returns a detached clone of that entry. Condition-based
    /// (not time-based): each attempt is a real awaited HTTP round-trip through the test server, and the
    /// loop is bounded only by <paramref name="timeout"/> as a safety net — there are no fixed sleeps.
    /// </summary>
    private static async Task<JsonElement> WaitForSubAgentAsync(
        HttpClient http,
        string threadId,
        string agentId,
        Func<string?, bool> statusPredicate,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var lastBody = "<none>";

        while (!cts.IsCancellationRequested)
        {
            using var response = await http.GetAsync(
                $"/api/conversations/{threadId}/subagents", cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            lastBody = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(lastBody);

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("agentId", out var idProp)
                    && string.Equals(idProp.GetString(), agentId, StringComparison.Ordinal)
                    && entry.TryGetProperty("status", out var statusProp)
                    && statusPredicate(statusProp.GetString()))
                {
                    return entry.Clone();
                }
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            $"Sub-agent '{agentId}' did not reach the expected status within {timeout}. "
            + $"Last subagents list body: {lastBody}");
    }
}
