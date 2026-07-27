using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="S2SReviewAgent"/> — the <c>IMultiTurnAgent</c> adapter that drives one review
/// turn against a running LmStreaming review host over the S2S REST API. Both tests construct the agent
/// directly (the factory does not forward poll intervals) with millisecond polling so the scripted
/// InProgress→terminal sequence resolves instantly. They pin the two facts the executor depends on: the
/// single finalized review <see cref="TextMessage"/> carries <c>status.Response</c> verbatim, and
/// <see cref="S2SReviewAgent.ThreadId"/> / <see cref="S2SReviewAgent.CurrentRunId"/> surface the
/// server-minted ids (the deep-link target) even when the run ends without review text.
/// </summary>
public sealed class S2SReviewAgentTests
{
    private static S2SReviewAgent NewAgent(LmStreamingS2SClient client, string? title) =>
        new(
            client,
            workspaceId: "ws-1",
            providerId: "openai",
            modeId: "workspace-agent",
            systemPrompt: "REVIEW METHODOLOGY",
            title: title,
            logger: NullLogger<S2SReviewAgent>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(1),
            pollMaxInterval: TimeSpan.FromMilliseconds(1),
            overallTimeout: TimeSpan.FromSeconds(5),
            terminalConfirmDelay: TimeSpan.FromMilliseconds(1),
            interruptedGrace: TimeSpan.FromMilliseconds(50));

    private static HttpClient NewHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    private static async Task<List<IMessage>> DriveAsync(S2SReviewAgent agent, string userText)
    {
        var collected = new List<IMessage>();
        var input = new UserInput([new TextMessage { Text = userText, Role = Role.User }]);
        await foreach (var message in agent.ExecuteRunAsync(input, CancellationToken.None))
        {
            collected.Add(message);
        }

        return collected;
    }

    [Fact]
    public async Task ExecuteRunAsync_provisions_polls_to_completion_and_yields_the_review_text_verbatim()
    {
        // Route order matters: the messages POST url (api/conversations/{id}/messages) is a superset of the
        // provision POST url (api/conversations), and the handler is first-match-wins — so the more specific
        // "/messages" POST must be registered BEFORE the "api/conversations" provision POST.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, "{\"status\":\"InProgress\",\"runId\":\"run-9\"}"),
                (HttpStatusCode.OK, "{\"status\":\"Completed\",\"runId\":\"run-9\",\"response\":{\"text\":\"LGTM, ship it.\"}}"))
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-xyz\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: "Review PR #118");

        var messages = await DriveAsync(agent, "review this PR");

        var text = messages.Should().ContainSingle().Subject.Should().BeOfType<TextMessage>().Subject;
        text.Text.Should().Be("LGTM, ship it.");
        text.Role.Should().Be(Role.Assistant);
        text.IsThinking.Should().BeFalse("the review text is a finalized assistant message, not a thinking trace");
        agent.ThreadId.Should().Be("thread-xyz", "ThreadId surfaces the minted conversation id — the deep-link target");
        agent.CurrentRunId.Should().Be("run-9");
    }

    [Fact]
    public async Task ExecuteRunAsync_hands_the_review_system_prompt_to_the_host_at_provision()
    {
        // Live regression (PR #222 bring-up, G6): the adapter sent ONLY the user turn, so the hosted run saw
        // the diff under LmStreaming's generic workspace-agent prompt and never followed the daemon's
        // methodology — most visibly, it dispatched zero code-reviewer:* sub-agents despite a fully populated
        // catalog. Provision carries no model or tool overrides, so the appendix is the only channel for it.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-sp\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: null);

        _ = await DriveAsync(agent, "review this PR");

        var provision = handler.Requests
            .Should()
            .ContainSingle(r => r.Body != null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal))
            .Subject;
        provision.Body.Should().Contain("\"systemPromptAppendix\":\"REVIEW METHODOLOGY\"");
    }

    [Fact]
    public async Task ExecuteRunAsync_yields_no_message_but_still_surfaces_the_ids_when_the_run_ends_errored()
    {
        // A non-Completed terminal (Errored) with no resolved response text yields nothing, but the minted
        // thread/run ids must still surface so the executor can deep-link the (failed) hosted conversation.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"Errored\",\"runId\":\"run-err\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-err\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        // title null → EnsureProvisioned skips the metadata call, so no /metadata route is needed.
        var agent = NewAgent(client, title: null);

        var messages = await DriveAsync(agent, "review this PR");

        messages.Should().BeEmpty("an errored run with no response text produces no review message");
        agent.ThreadId.Should().Be("thread-err");
        agent.CurrentRunId.Should().Be("run-err");
    }

    [Fact]
    public async Task ExecuteRunAsync_keeps_polling_when_the_host_interrupts_and_re_runs_the_same_input()
    {
        // Live regression (PR #222 bring-up): the FIRST poll after send read Interrupted for a run that never
        // produced a message — the host had synthesized that row for an input it had accepted but not yet
        // drained, then bound the SAME input to a real run that completed with the review text. Taking the
        // Interrupted reading at face value abandoned a review that was seconds from succeeding, so an
        // Interrupted status is only believed once it holds on the same run id through the grace window.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, "{\"status\":\"Interrupted\",\"runId\":\"run-superseded\"}"),
                (HttpStatusCode.OK, "{\"status\":\"InProgress\",\"runId\":\"run-real\"}"),
                (HttpStatusCode.OK, "{\"status\":\"InProgress\",\"runId\":\"run-real\"}"),
                (HttpStatusCode.OK, "{\"status\":\"Completed\",\"runId\":\"run-real\",\"response\":{\"text\":\"One new medium finding.\"}}"))
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-restart\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: null);

        var messages = await DriveAsync(agent, "review this PR");

        var text = messages.Should().ContainSingle().Subject.Should().BeOfType<TextMessage>().Subject;
        text.Text.Should().Be(
            "One new medium finding.",
            "the superseded Interrupted run must not end the poll — the re-run carries the review");
        agent.CurrentRunId.Should().Be("run-real");
    }

    [Fact]
    public async Task ExecuteRunAsync_accepts_an_Interrupted_run_that_holds_through_the_grace_window()
    {
        // The other half of the Interrupted contract: an input whose run really is dead (nothing ever re-binds
        // it) must not hold the review open to the overall timeout. Once the same run id keeps reading
        // Interrupted for the whole grace window it is taken as the input's final state — no text, ids intact.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"Interrupted\",\"runId\":\"run-dead\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-dead\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: null);

        var messages = await DriveAsync(agent, "review this PR");

        messages.Should().BeEmpty("an Interrupted run never carries response text");
        agent.ThreadId.Should().Be("thread-dead");
        agent.CurrentRunId.Should().Be("run-dead");
    }
}
