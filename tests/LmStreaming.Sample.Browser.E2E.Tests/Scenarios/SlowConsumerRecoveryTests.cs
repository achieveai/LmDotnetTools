using System.Globalization;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using LmStreaming.Sample.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser-level proof that a stream dropped for slow consumption RECOVERS instead of hanging.
/// </summary>
/// <remarks>
/// <para>
/// The backend fans a run out to each subscriber through a bounded channel. A subscriber that stops
/// draining it (a browser that is paused, throttled, or simply slower than a burst of updates) fills
/// that channel; the agent then evicts it rather than blocking the live run — see
/// <c>MultiTurnAgentBase.PublishToSubscriber</c>. Before this work the eviction looked to the browser
/// exactly like a normal end-of-stream: the socket closed cleanly, the client never learned that
/// content had been skipped, and the conversation sat on a spinner that never cleared.
/// </para>
/// <para>
/// The shipped behaviour instead reserves a <c>StreamRecoveryMessage</c> on the evicted subscriber, so
/// the server emits a <c>stream_recovery</c> frame and closes with <c>resync_required</c> and NO
/// <c>done</c>; the client funnels that into a single-flight resync — discard the dropped socket, reload
/// the authoritative history over REST, then re-subscribe — and the run finishes on the replacement
/// socket. This scenario drives that whole path through the real client and asserts each observable
/// step. It is the integrated counterpart to the unit coverage in
/// <c>LmMultiTurn.Tests</c>/<c>LmStreaming.Sample.Tests</c> and <c>streamResync.test.ts</c>.
/// </para>
/// <para>
/// DETERMINISM — the hard part of this test is making "the consumer was too slow" a counting argument
/// rather than a race. Two production-inert seams do that, and there are no timing sleeps anywhere:
/// </para>
/// <list type="number">
/// <item><description>
/// <c>LmStreaming:OutputChannelCapacity</c> shrinks the per-subscriber channel to
/// <see cref="OutputChannelCapacity"/> for this host only (unset in production ⇒ 1000).
/// </description></item>
/// <item><description>
/// <c>ChatWebSocketManager.OutboundPumpGate</c> (null in production) parks the outbound pump on its
/// first frame, so the subscriber is registered but consumes nothing. Turn 1 then publishes far more
/// than <see cref="OutputChannelCapacity"/> messages, which MUST overflow the channel.
/// </description></item>
/// </list>
/// <para>
/// The provider's second turn is likewise held open, which does double duty: its arrival proves turn 1
/// finished publishing (so the drop has already happened when the pump is released), and holding it
/// keeps the run in-flight so the client's resume path has a live run to re-subscribe to — the same
/// precondition <c>StreamingResumeToolPillsTests</c> relies on.
/// </para>
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class SlowConsumerRecoveryTests
{
    /// <summary>
    /// Per-subscriber output-channel capacity for this host. Small enough that a modest scripted turn
    /// is guaranteed to overflow it while the pump is parked, large enough to still exercise the
    /// "some buffered frames are delivered, then recovery" ordering rather than an empty-channel edge.
    /// </summary>
    private const int OutputChannelCapacity = 4;

    /// <summary>
    /// Words in turn 1. Any value above <see cref="OutputChannelCapacity"/> proves the point; this is an
    /// order of magnitude above it so the overflow cannot be an accident of message batching.
    /// </summary>
    private const int FirstTurnWordCount = 40;

    /// <summary>The final full message. Distinctive so "exactly once" is countable in the DOM.</summary>
    private const string FinalAnswer = "Recovered final answer: the run survived the dropped stream.";

    private readonly PlaywrightFixture _fixture;

    public SlowConsumerRecoveryTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Primary_stream_recovers_after_a_slow_consumer_drop(string providerMode)
    {
        // Turn 1 bursts well past the channel capacity and ends in a tool call so the run continues;
        // turn 2 is the single final full message the client must end up showing exactly once.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.TextLen(FirstTurnWordCount).ToolCall("calculate", new { a = 1, operation = "add", b = 1 }))
            .Turn(t => t.Text(FinalAnswer))
            .Build();

        using var heldFinalTurn = new HeldProviderTurn(responder.HandlerFor(providerMode), turnOrdinal: 2);

        await using var session = await _fixture.OpenAsync(
            providerMode,
            heldFinalTurn,
            settings: new Dictionary<string, string?>
            {
                ["LmStreaming:OutputChannelCapacity"] =
                    OutputChannelCapacity.ToString(CultureInfo.InvariantCulture),
            });
        var page = session.Page;

        // Park the primary stream's pump. Sub-agent streams (threadId `subagent-*`) are untouched so a
        // gate armed here can never interfere with anything but the conversation under test.
        var pump = new PumpGate(threadId => !threadId.StartsWith("subagent-", StringComparison.Ordinal));
        session.Factory.AppServices.GetRequiredService<ChatWebSocketManager>().OutboundPumpGate = pump.WaitAsync;

        // Passive observer: count sockets and record server->client frames. Everything is forwarded
        // unchanged — this test asserts the real wire, it does not shape it. It samples the REST
        // counter as the replacement socket is created, which is what makes "history was reloaded
        // BEFORE re-subscribing" provable rather than merely plausible.
        var restCatchUp = new RestMessagesObserver(page);
        var sockets = new WebSocketObserver(sampleAtOpen: () => restCatchUp.Count);
        await page.RouteWebSocketAsync(url => url.Contains("/ws?", StringComparison.Ordinal), sockets.Attach);

        // RouteWebSocketAsync only wraps sockets created on a document loaded after it is registered.
        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();
        await page.NewChatButton().ClickAsync();

        // 1) Start the run. The pump registers a subscriber, pulls one message, then parks.
        await page.SendMessageAsync("summarise, run a calculation, then answer");
        await page.WaitForStreamActiveAsync();
        await pump.Parked.WaitForAsync("the outbound pump to park with a registered, non-draining subscriber");

        // 2) The provider asking for turn 2 proves turn 1 finished streaming, so all of its messages
        //    have been published against a 4-slot channel that nothing is draining: the subscriber has
        //    necessarily been evicted by now. No sleep, no polling — a counting argument.
        await heldFinalTurn.Arrived.WaitForAsync("the provider's final turn to be requested");
        var restCallsBeforeDrop = restCatchUp.Count;

        // 3) Let the pump run. It flushes the frames the channel did hold, then the stream_recovery
        //    frame, then closes `resync_required`.
        pump.Release();

        // 4) The client must react by opening a REPLACEMENT subscribe-only socket and re-subscribing.
        //    Wait for its first frame, not merely for the socket: the run is still in-flight (turn 2 is
        //    held), and releasing turn 2 before the new subscription is registered would publish the
        //    final message to nobody.
        await sockets.SecondConnection.WaitForAsync("the client to open a replacement stream socket");
        await sockets.SecondConnectionStreaming.WaitForAsync("the replacement socket to start streaming");

        // 5) Only now let the final turn stream — it can therefore only reach the DOM through the
        //    replacement socket.
        heldFinalTurn.Release();
        await page.WaitForStreamIdleAsync(timeoutMs: 60_000);

        // --- The recovery contract, as the browser saw it -------------------------------------
        AssertDroppedStreamContract(sockets.Frames(0));

        sockets.Count
            .Should()
            .Be(2, "the client replaces a dropped stream exactly once — no socket storm, no silent give-up");

        restCatchUp.Count
            .Should()
            .BeGreaterThan(
                restCallsBeforeDrop,
                "recovery reloads the authoritative history over REST, so the content that was skipped "
                    + "while the channel overflowed is not lost");

        sockets.SampleAtSecondConnection
            .Should()
            .BeGreaterThan(
                restCallsBeforeDrop,
                "the reload happens BEFORE the replacement subscription — the resync coordinator runs "
                    + "discard -> loadHistory -> resubscribe, so re-subscribing first would leave the "
                    + "skipped content unrecoverable from the live stream alone");

        // --- What the user ends up looking at --------------------------------------------------
        // The stream is already idle, so the rendered text is settled. Count OCCURRENCES rather than
        // elements: REST catch-up and the replacement stream both carry the final message, and a client
        // that appended instead of merging would show it twice inside a single bubble — which an
        // element count of 1 would happily pass.
        await page.MessageList().WaitForTextContainsAsync(FinalAnswer, timeoutMs: 20_000);
        CountOccurrences(await page.MessageList().InnerTextAsync(), FinalAnswer)
            .Should()
            .Be(1, "the recovered run shows its final message exactly once — merged, not duplicated");

        // The pre-drop turn's tool call was published into the overflowing channel, so it can only be
        // on screen if the REST catch-up actually restored the skipped canonical content.
        await Assertions
            .Expect(page.ToolCallPillByName("calculate"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });

        (await page.ErrorBanner().IsVisibleAsync())
            .Should()
            .BeFalse("a recovered drop is invisible to the user — no persistent error is left behind");

        responder.RemainingTurns["parent"]
            .Should()
            .Be(0, "the backend run completed its scripted plan despite the dropped subscriber");

        await session.SaveSuccessScreenshotAsync(
            $"SlowConsumerRecovery.Primary_stream_recovers_{providerMode}");
    }

    /// <summary>
    /// Parity for the sub-agent focus view: a focused child's stream is fanned out by the same bounded
    /// channel and pumped by the same <c>ChatWebSocketManager</c> method, so a viewer that lags must be
    /// recovered there too — a sub-agent tab stuck on a half-written transcript is the same defect.
    /// </summary>
    /// <remarks>
    /// The focus view reaches the recovery differently from the primary chat (it has no
    /// <c>onStreamRecovery</c> handler and re-focuses once on a clean close instead of running the
    /// single-flight coordinator), which is exactly why it needs its own browser proof rather than an
    /// argument from shared server code. The child's turns are held open individually so the tab can be
    /// focused while its run is live, then made to burst past its channel capacity on demand.
    /// </remarks>
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Sub_agent_focus_stream_recovers_after_a_slow_consumer_drop(string providerMode)
    {
        const string ResearcherMarker = "You are the research sub-agent";
        const string ChildFinalAnswer = "Recovered sub-agent answer: the child run survived the drop.";

        var responder = ScriptedSseResponder
            .New()
            // Turn 1 gives the focus view a transcript to replay, turn 2 is the burst that overflows the
            // child's output channel while the tab is parked, turn 3 is the final full message.
            .ForRole("researcher", ctx => ctx.SystemPromptContains(ResearcherMarker))
            .Turn(t => t.ToolCall("calculate", new { a = 1, operation = "add", b = 1 }))
            .Turn(t => t.TextLen(FirstTurnWordCount).ToolCall("calculate", new { a = 2, operation = "add", b = 1 }))
            .Turn(t => t.Text(ChildFinalAnswer))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "researcher", prompt = "Find AI papers" }))
            .Turn(t => t.Text("The researcher finished."))
            .Build();

        // Only the child's requests are counted, so the parent's turns do not shift the ordinals.
        static bool IsChildTurn(string body) => body.Contains(ResearcherMarker, StringComparison.Ordinal);

        var heldFinalTurn = new HeldProviderTurn(responder.HandlerFor(providerMode), turnOrdinal: 3, matchesBody: IsChildTurn);
        using var heldBurstTurn = new HeldProviderTurn(heldFinalTurn, turnOrdinal: 2, matchesBody: IsChildTurn);

        await using var session = await _fixture.OpenAsync(
            providerMode,
            heldBurstTurn,
            subAgentFactory: (_, providerAgentFactory) =>
                new SubAgentOptions
                {
                    Templates = new Dictionary<string, SubAgentTemplate>
                    {
                        ["researcher"] = new SubAgentTemplate
                        {
                            Name = "Researcher",
                            SystemPrompt = ResearcherMarker,
                            AgentFactory = providerAgentFactory,
                            MaxTurnsPerRun = 5,
                        },
                    },
                    MaxConcurrentSubAgents = 5,
                    OutputChannelCapacity = OutputChannelCapacity,
                });
        var page = session.Page;

        // Park the focus view's pump only; the parent conversation streams normally throughout.
        var pump = new PumpGate(threadId => threadId.StartsWith("subagent-", StringComparison.Ordinal));
        session.Factory.AppServices.GetRequiredService<ChatWebSocketManager>().OutboundPumpGate = pump.WaitAsync;

        var focusSockets = new WebSocketObserver();
        await page.RouteWebSocketAsync(url => url.Contains("/ws/subagent", StringComparison.Ordinal), focusSockets.Attach);

        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();
        await page.NewChatButton().ClickAsync();

        // Count only the FOCUS view's history reads, so the catch-up assertion cannot be satisfied by
        // the parent conversation's own traffic.
        var restCatchUp = new RestMessagesObserver(page, "/api/conversations/subagent-");

        // 1) Spawn the child and focus its tab while its run is live — its second turn is held open, so
        //    the tab cannot be opened onto an already-finished transcript.
        await page.SendMessageAsync("research AI papers for me");
        await page.SubAgentTabs().WaitForCountAtLeastAsync(1, timeoutMs: 30_000);
        await page.SubAgentTabs().First.ClickAsync();
        await page.SubAgentView().WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await pump.Parked.WaitForAsync("the sub-agent focus pump to park with a registered, non-draining subscriber");

        // 2) Burst past the child's channel capacity while nothing drains it, then wait for the final
        //    turn to be requested — proof the burst finished publishing, so the viewer is now evicted.
        heldBurstTurn.Release();
        await heldFinalTurn.Arrived.WaitForAsync("the child's final turn to be requested");
        var restCallsBeforeDrop = restCatchUp.Count;

        // 3) Release the pump: buffered frames, then stream_recovery, then a clean `resync_required`.
        pump.Release();
        await focusSockets.SecondConnection.WaitForAsync("the client to re-open the sub-agent focus socket");
        await focusSockets.SecondConnectionStreaming.WaitForAsync("the replacement focus socket to start streaming");

        // 4) Only now let the child finish, so its answer can only arrive over the replacement socket.
        //    Wait for that socket's end-of-stream sentinel before reading the DOM: this view has no
        //    spinner to settle on (MessageList only renders a typing indicator when the LAST group is a
        //    user message, and a child transcript ends with the assistant), so the `done` frame IS the
        //    focus view's loading-ended contract. Reading earlier would race a later duplicate.
        heldFinalTurn.Release();
        await focusSockets.SecondConnectionDone.WaitForAsync(
            "the replacement focus socket to reach end-of-stream (sub-agent loading finished)");
        await page.GetByTestId("subagent-transcript").WaitForTextContainsAsync(ChildFinalAnswer, timeoutMs: 30_000);

        AssertDroppedStreamContract(focusSockets.Frames(0));

        // The replacement stream, unlike the one it replaced, ends normally.
        var replacementFrames = focusSockets.Frames(1);
        replacementFrames
            .Count(f => f.Contains(WebSocketObserver.DoneFrame, StringComparison.Ordinal))
            .Should()
            .Be(1, "the recovered focus stream finishes exactly once, so the view stops waiting");
        replacementFrames
            .Should()
            .NotContain(
                f => f.Contains("\"$type\":\"stream_recovery\"", StringComparison.Ordinal),
                "the replacement viewer keeps up — a second drop would mean recovery just moved the problem");

        focusSockets.Count
            .Should()
            .Be(2, "the focus view replaces a dropped stream exactly once — it must not give up, nor reconnect in a loop");

        // Ordering note: unlike the primary chat's resync coordinator (discard -> loadHistory ->
        // resubscribe), the focus path deliberately SUBSCRIBES FIRST and buffers live frames while it
        // loads history, to close the snapshot->subscribe gap (see useSubAgentPanel.focusChild). So the
        // provable claim here is that the catch-up happened during recovery — and the restored pre-drop
        // tool calls below are what prove it carried the skipped canonical content.
        restCatchUp.Count
            .Should()
            .BeGreaterThan(
                restCallsBeforeDrop,
                "re-focusing reloads the child's persisted transcript, so the content skipped while its "
                    + "output channel overflowed is restored rather than lost");

        // Scoped to the focus transcript on purpose: a page-wide pill count would also see the parent
        // conversation's pills and could not prove WHERE the restored content landed. Both of the
        // child's tool calls must be present — the second one was published into the overflowing
        // channel — and exactly once each, so the reload merged rather than appended.
        await Assertions
            .Expect(page.GetByTestId("subagent-transcript").ToolCallPillByName("calculate"))
            .ToHaveCountAsync(2, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });

        CountOccurrences(await page.GetByTestId("subagent-transcript").InnerTextAsync(), ChildFinalAnswer)
            .Should()
            .Be(1, "the reloaded transcript and the replacement stream carry the same final message; it must merge, not duplicate");

        (await page.GetByTestId("subagent-error").IsVisibleAsync())
            .Should()
            .BeFalse("a recovered drop leaves no error on the sub-agent view");

        responder.RemainingTurns["researcher"]
            .Should()
            .Be(0, "the child run completed its scripted plan despite the dropped viewer");

        // Back on the parent conversation, the run that owns the child settles normally.
        await page.ConversationTab("main").ClickAsync();
        await page.WaitForStreamIdleAsync(timeoutMs: 60_000);

        await session.SaveSuccessScreenshotAsync(
            $"SlowConsumerRecovery.Sub_agent_focus_stream_recovers_{providerMode}");
    }

    /// <summary>
    /// What the browser must see on a stream that was dropped for slow consumption: the drop announced
    /// exactly once, attributed to the slow consumer, and NO <c>done</c> — a dropped stream that looks
    /// completed is precisely what used to leave the client waiting forever.
    /// </summary>
    private static void AssertDroppedStreamContract(IReadOnlyList<string> frames)
    {
        frames
            .Count(f => f.Contains("\"$type\":\"stream_recovery\"", StringComparison.Ordinal))
            .Should()
            .Be(1, "the evicted subscriber is told exactly once that its stream was dropped");
        frames
            .Single(f => f.Contains("\"$type\":\"stream_recovery\"", StringComparison.Ordinal))
            .Should()
            .Contain("\"reason\":\"slow_consumer\"", "the drop was caused by a full output channel");
        frames
            .Should()
            .NotContain(
                f => f.Contains(WebSocketObserver.DoneFrame, StringComparison.Ordinal),
                "a dropped stream must NOT look like a completed one, or the client would stop waiting");
    }

    /// <summary>
    /// Counts non-overlapping occurrences of <paramref name="needle"/>. Rendered text is counted rather
    /// than elements because the failure this guards against — catch-up appended to the live stream
    /// instead of merged with it — can put the same message twice inside ONE bubble.
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (
            var at = haystack.IndexOf(needle, StringComparison.Ordinal);
            at >= 0;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }
}
