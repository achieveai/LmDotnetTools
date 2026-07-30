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
    private static S2SReviewAgent NewAgent(
        LmStreamingS2SClient client, string? title, string? existingThreadId = null) =>
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
            interruptedGrace: TimeSpan.FromMilliseconds(50),
            existingThreadId: existingThreadId);

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

    /// <summary>
    /// The synthesis turn's "no new children" guarantee has to be REAL over S2S, where the children live in
    /// the host's process: the executor opens the agent's <c>SuppressSpawning</c> scope and every send inside
    /// it must ask the host to run that turn with spawning suppressed. The provisional turn (outside the
    /// scope) must not, or it could never fan out in the first place — and the scope is released afterwards.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_asks_the_host_to_suppress_spawning_only_inside_the_suppression_scope()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\",\"spawningSuppressed\":true}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-sup\"}");
        using var http = NewHttp(handler);
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);

        // The executor resolves the scope off the loop's declared surface; null here would mean the synthesis
        // turn only CLAIMED it could not spawn. The interface-typed local is the compile-time half of the
        // proof — the agent has to declare the surface for the executor to find it at all.
        IReviewLoopSubAgentSurface surface = agent;
        var suppress = surface.SuppressSpawning;
        suppress.Should().NotBeNull("an S2S review loop must expose a real per-turn suppression scope");

        _ = await DriveAsync(agent, "provisional review");
        using (suppress!())
        {
            _ = await DriveAsync(agent, "synthesize the final review");
        }

        _ = await DriveAsync(agent, "a later turn");

        var sends = handler.Requests
            .Where(r => r.Method == HttpMethod.Post
                && r.Uri.ToString().Contains("/messages", StringComparison.Ordinal))
            .Select(r => r.Body)
            .ToList();
        sends.Should().HaveCount(3);
        sends[0].Should().Contain("\"suppressSubAgentSpawning\":false", "the provisional turn must be free to fan out");
        sends[1].Should().Contain("\"suppressSubAgentSpawning\":true", "the synthesis turn must not start new children");
        sends[2].Should().Contain("\"suppressSubAgentSpawning\":false", "the scope is released when it is disposed");
    }

    /// <summary>
    /// Its children run in the HOST's process, so there is no in-process manager to read them from — the
    /// barrier must fall through to the executor's injected out-of-process source.
    /// </summary>
    [Fact]
    public void The_agent_exposes_no_in_process_completion_source()
    {
        using var http = NewHttp(new FakeHttpMessageHandler());
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);

        IReviewLoopSubAgentSurface surface = agent;
        surface.CompletionSource.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteRunAsync_throws_when_the_hosted_run_ends_errored_even_with_partial_text()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Errored\",\"runId\":\"run-err\",\"response\":{\"text\":\"partial\"}}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-err\"}");
        using var http = NewHttp(handler);
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);

        Func<Task> act = async () => _ = await DriveAsync(agent, "review this PR");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*run-err*Errored*");
        agent.ThreadId.Should().Be("thread-err");
        agent.CurrentRunId.Should().Be("run-err");
    }

    [Fact]
    public async Task ExecuteRunAsync_throws_when_a_completed_run_has_blank_review_text()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-empty\",\"response\":{\"text\":\"  \"}}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-empty\"}");
        using var http = NewHttp(handler);
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);

        Func<Task> act = async () => _ = await DriveAsync(agent, "review this PR");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Completed*no review text*");
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
    public async Task ExecuteRunAsync_rejects_an_Interrupted_run_that_holds_through_the_grace_window()
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

        Func<Task> act = async () => _ = await DriveAsync(agent, "review this PR");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*run-dead*Interrupted*");
        agent.ThreadId.Should().Be("thread-dead");
        agent.CurrentRunId.Should().Be("run-dead");
    }

    [Fact]
    public async Task ExecuteRunAsync_resumes_a_seeded_thread_without_provisioning_again()
    {
        // The synthesis turn of a review runs on the SAME hosted conversation the provisional turn used, and
        // a resumed review (daemon restarted between the two) must rejoin that persisted thread rather than
        // mint a second one — a fresh conversation would carry none of the review history the synthesis reads
        // and would orphan the deep-link already posted on the PR. NO provision route is registered here: the
        // fake handler answers an unrouted request with 501, so any ProvisionAsync call fails this test.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-2\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-synth\",\"response\":{\"text\":\"## Review\\nFinal.\"}}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: "Review PR #118", existingThreadId: "thread-persisted");

        var messages = await DriveAsync(agent, "synthesize now");

        messages.Should().ContainSingle().Subject.Should().BeOfType<TextMessage>()
            .Which.Text.Should().Be("## Review\nFinal.");
        agent.ThreadId.Should().Be("thread-persisted");
        handler.Requests.Should().NotContain(
            r => r.Body != null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal),
            "a seeded thread is resumed, never re-provisioned");
        handler.Requests.Should().OnlyContain(
            r => r.Uri.ToString().Contains("thread-persisted", StringComparison.Ordinal),
            "every call targets the persisted conversation");
    }

    /// <summary>
    /// A daemon restart between the send and the answer must not queue a SECOND synthesis turn: the host has
    /// already accepted the input and is producing for it, so the resumed lifecycle rejoins that exact input.
    /// No <c>POST /messages</c> route is registered here — the fake handler answers an unrouted request with
    /// 501, so any re-send fails this test outright.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_rejoins_an_armed_input_instead_of_queueing_a_second_turn()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-resumed\",\"response\":{\"text\":\"## Review\\nResumed.\"}}");
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"), title: null, existingThreadId: "thread-persisted");
        var reAccepted = new List<string>();

        IResumableReviewTurn resumable = agent;
        resumable.ArmTurnCheckpoint("input-inflight", reAccepted.Add);
        var messages = await DriveAsync(agent, "synthesize the final review");

        messages.Should().ContainSingle().Subject.Should().BeOfType<TextMessage>()
            .Which.Text.Should().Be("## Review\nResumed.");
        reAccepted.Should().BeEmpty("nothing new was accepted — the checkpoint the caller supplied still stands");
        handler.Requests.Should().OnlyContain(
            r => r.Method == HttpMethod.Get && r.Uri.Query.Contains("inputId=input-inflight", StringComparison.Ordinal),
            "the resumed turn only polls the input the host already took");
    }

    /// <summary>
    /// The other half of the checkpoint contract: an armed turn with nothing to rejoin is sent normally, and
    /// the accepted id is reported BEFORE the first poll. That ordering is the whole point — the wait it
    /// protects can run for the review's entire budget, so a checkpoint written after it would be written too
    /// late to survive the outage it exists for.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_reports_a_newly_accepted_input_id_before_it_starts_polling()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-minted\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}");
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"), title: null, existingThreadId: "thread-persisted");
        var accepted = new List<string>();
        var pollsBeforeCheckpoint = -1;

        IResumableReviewTurn resumable = agent;
        resumable.ArmTurnCheckpoint(
            resumeInputId: null,
            inputId =>
            {
                accepted.Add(inputId);
                pollsBeforeCheckpoint = handler.CountRequests("/status");
            });
        _ = await DriveAsync(agent, "synthesize the final review");

        accepted.Should().Equal("input-minted");
        pollsBeforeCheckpoint.Should().Be(0, "the checkpoint must be durable before the minutes-long wait, not after it");
    }

    /// <summary>
    /// Arming is ONE SHOT. A later turn on the same loop is unarmed again, so a spent input can never be
    /// rejoined twice — which would poll an answer belonging to the previous turn and never send this one.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_arms_only_the_next_turn()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-fresh\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}");
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"), title: null, existingThreadId: "thread-persisted");

        IResumableReviewTurn resumable = agent;
        resumable.ArmTurnCheckpoint("input-inflight", _ => { });
        _ = await DriveAsync(agent, "the rejoined turn");
        _ = await DriveAsync(agent, "a later turn");

        handler.Requests.Should().ContainSingle(
            r => r.Method == HttpMethod.Post && r.Uri.ToString().Contains("/messages", StringComparison.Ordinal),
            "the rejoined turn sent nothing, the unarmed turn after it sent normally");
    }

    [Fact]
    public async Task ExecuteRunAsync_obeys_the_supplied_absolute_deadline_instead_of_a_fresh_per_turn_window()
    {
        // Collect → barrier → synthesize share ONE absolute budget. Without clamping, each turn would start
        // its own overallTimeout window, so a review could spend the whole budget on the provisional turn and
        // then spend it AGAIN on synthesis. With a deadline already in the past the agent must give up
        // immediately — proven by the poll never issuing a single status request.

        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"InProgress\",\"runId\":\"run-slow\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-budget\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: null);
        agent.UseDeadline(DateTimeOffset.UtcNow.AddSeconds(-1));

        Func<Task> act = async () => _ = await DriveAsync(agent, "review this PR");

        await act.Should().ThrowAsync<TimeoutException>();
        handler.Requests.Should().NotContain(
            r => r.Method == HttpMethod.Get,
            "the exhausted shared budget must not open a fresh per-turn poll window");
    }
}
