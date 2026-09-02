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
    /// <summary>A turn idempotency key shaped like the ones the executor derives — see
    /// <c>DaemonReviewStageExecutor.TurnIdempotencyKey</c>. Its VALUE is irrelevant here; what matters is that
    /// arming a turn always supplies one, so a repeat of the send resolves to the input already accepted.</summary>
    private const string SynthesisKey = "review-run-7-primary:synthesis";

    private static S2SReviewAgent NewAgent(
        LmStreamingS2SClient client,
        string? title,
        string? existingThreadId = null
    ) =>
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
            existingThreadId: existingThreadId
        );

    private static HttpClient NewHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    /// <summary>
    /// Everything the agent sent EXCEPT the host-capability preflight. That GET verifies the message contract
    /// before the first send and is not part of any turn, so the assertions about what a turn does filter it
    /// out rather than being loosened.
    /// </summary>
    private static IEnumerable<FakeHttpMessageHandler.RecordedRequest> TurnRequests(FakeHttpMessageHandler handler) =>
        handler.Requests.Where(r => !r.Uri.ToString().Contains("conversations/capabilities", StringComparison.Ordinal));

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
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, "{\"status\":\"InProgress\",\"runId\":\"run-9\"}"),
                (
                    HttpStatusCode.OK,
                    "{\"status\":\"Completed\",\"runId\":\"run-9\",\"response\":{\"text\":\"LGTM, ship it.\"}}"
                )
            )
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
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}"
            )
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-sp\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: null);

        _ = await DriveAsync(agent, "review this PR");

        var provision = handler
            .Requests.Should()
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
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\",\"spawningSuppressed\":true}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}"
            )
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

        var sends = handler
            .Requests.Where(r =>
                r.Method == HttpMethod.Post && r.Uri.ToString().Contains("/messages", StringComparison.Ordinal)
            )
            .Select(r => r.Body)
            .ToList();
        sends.Should().HaveCount(3);
        sends[0].Should().Contain("\"suppressSubAgentSpawning\":false", "the provisional turn must be free to fan out");
        sends[1]
            .Should()
            .Contain("\"suppressSubAgentSpawning\":true", "the synthesis turn must not start new children");
        sends[2].Should().Contain("\"suppressSubAgentSpawning\":false", "the scope is released when it is disposed");
    }

    /// <summary>
    /// Its children run in the HOST's process, so there is no in-process manager to read them from — the
    /// barrier must fall through to the executor's injected out-of-process source.
    /// </summary>
    [Fact]
    public void The_agent_exposes_no_in_process_completion_source()
    {
        using var http = NewHttp(new FakeHttpMessageHandler().OnCurrentReviewHostCapabilities());
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);

        IReviewLoopSubAgentSurface surface = agent;
        surface.CompletionSource.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteRunAsync_throws_when_the_hosted_run_ends_errored_even_with_partial_text()
    {
        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Errored\",\"runId\":\"run-err\",\"response\":{\"text\":\"partial\"}}"
            )
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
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-empty\",\"response\":{\"text\":\"  \"}}"
            )
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
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnSequence(
                HttpMethod.Get,
                "/status",
                (HttpStatusCode.OK, "{\"status\":\"Interrupted\",\"runId\":\"run-superseded\"}"),
                (HttpStatusCode.OK, "{\"status\":\"InProgress\",\"runId\":\"run-real\"}"),
                (HttpStatusCode.OK, "{\"status\":\"InProgress\",\"runId\":\"run-real\"}"),
                (
                    HttpStatusCode.OK,
                    "{\"status\":\"Completed\",\"runId\":\"run-real\",\"response\":{\"text\":\"One new medium finding.\"}}"
                )
            )
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-restart\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: null);

        var messages = await DriveAsync(agent, "review this PR");

        var text = messages.Should().ContainSingle().Subject.Should().BeOfType<TextMessage>().Subject;
        text.Text.Should()
            .Be(
                "One new medium finding.",
                "the superseded Interrupted run must not end the poll — the re-run carries the review"
            );
        agent.CurrentRunId.Should().Be("run-real");
    }

    [Fact]
    public async Task ExecuteRunAsync_rejects_an_Interrupted_run_that_holds_through_the_grace_window()
    {
        // The other half of the Interrupted contract: an input whose run really is dead (nothing ever re-binds
        // it) must not hold the review open to the overall timeout. Once the same run id keeps reading
        // Interrupted for the whole grace window it is taken as the input's final state — no text, ids intact.
        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
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
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-2\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-synth\",\"response\":{\"text\":\"## Review\\nFinal.\"}}"
            );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: "Review PR #118", existingThreadId: "thread-persisted");

        var messages = await DriveAsync(agent, "synthesize now");

        messages
            .Should()
            .ContainSingle()
            .Subject.Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be("## Review\nFinal.");
        agent.ThreadId.Should().Be("thread-persisted");
        handler
            .Requests.Should()
            .NotContain(
                r => r.Body != null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal),
                "a seeded thread is resumed, never re-provisioned"
            );
        TurnRequests(handler)
            .Should()
            .OnlyContain(
                r => r.Uri.ToString().Contains("thread-persisted", StringComparison.Ordinal),
                "every call targets the persisted conversation"
            );
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
            .OnCurrentReviewHostCapabilities()
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-resumed\",\"response\":{\"text\":\"## Review\\nResumed.\"}}"
            );
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            title: null,
            existingThreadId: "thread-persisted"
        );
        var reAccepted = new List<string>();

        IResumableReviewTurn resumable = agent;
        resumable.ArmTurnCheckpoint(SynthesisKey, "input-inflight", reAccepted.Add);
        var messages = await DriveAsync(agent, "synthesize the final review");

        messages
            .Should()
            .ContainSingle()
            .Subject.Should()
            .BeOfType<TextMessage>()
            .Which.Text.Should()
            .Be("## Review\nResumed.");
        reAccepted.Should().BeEmpty("nothing new was accepted — the checkpoint the caller supplied still stands");
        TurnRequests(handler)
            .Should()
            .OnlyContain(
                r =>
                    r.Method == HttpMethod.Get
                    && r.Uri.Query.Contains("inputId=input-inflight", StringComparison.Ordinal),
                "the resumed turn only polls the input the host already took"
            );
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
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-minted\",\"idempotencyKeyHonored\":true}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}"
            );
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            title: null,
            existingThreadId: "thread-persisted"
        );
        var accepted = new List<string>();
        var pollsBeforeCheckpoint = -1;

        IResumableReviewTurn resumable = agent;
        resumable.ArmTurnCheckpoint(
            SynthesisKey,
            acceptedInputId: null,
            inputId =>
            {
                accepted.Add(inputId);
                pollsBeforeCheckpoint = handler.CountRequests("/status");
            }
        );
        _ = await DriveAsync(agent, "synthesize the final review");

        accepted.Should().Equal("input-minted");
        pollsBeforeCheckpoint
            .Should()
            .Be(0, "the checkpoint must be durable before the minutes-long wait, not after it");
        handler
            .Requests.Single(r => r.Method == HttpMethod.Post)
            .Body.Should()
            .Contain(
                $"\"idempotencyKey\":\"{SynthesisKey}\"",
                "the key is what makes a repeat of this send resolve to the same input instead of a second turn"
            );
    }

    /// <summary>
    /// Arming is ONE SHOT. A later turn on the same loop is unarmed again, so a spent input can never be
    /// rejoined twice — which would poll an answer belonging to the previous turn and never send this one.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_arms_only_the_next_turn()
    {
        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-fresh\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}"
            );
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            title: null,
            existingThreadId: "thread-persisted"
        );

        IResumableReviewTurn resumable = agent;
        resumable.ArmTurnCheckpoint(SynthesisKey, "input-inflight", _ => { });
        _ = await DriveAsync(agent, "the rejoined turn");
        _ = await DriveAsync(agent, "a later turn");

        handler
            .Requests.Should()
            .ContainSingle(
                r => r.Method == HttpMethod.Post && r.Uri.ToString().Contains("/messages", StringComparison.Ordinal),
                "the rejoined turn sent nothing, the unarmed turn after it sent normally"
            );
    }

    /// <summary>
    /// The mint window: between the host creating the conversation and the caller recording it, a crash leaves
    /// a sub-agent tree running that nothing can find. The checkpoint therefore fires the moment the id exists
    /// — before the turn that fans out on it is even sent.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_reports_a_minted_conversation_before_the_turn_that_fans_out_on_it()
    {
        // Route order is load-bearing: the send URL is api/conversations/{id}/messages, so the narrower
        // "/messages" route has to be registered ahead of the conversation-create one to win the first match.
        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-minted\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}"
            );
        using var http = NewHttp(handler);
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);
        var minted = new List<string>();
        var sendsBeforeCheckpoint = -1;

        IResumableReviewTurn resumable = agent;
        resumable.ObserveConversationMint(threadId =>
        {
            minted.Add(threadId);
            sendsBeforeCheckpoint = handler.CountRequests("/messages");
        });
        _ = await DriveAsync(agent, "review this PR");

        minted.Should().Equal("thread-minted");
        sendsBeforeCheckpoint.Should().Be(0, "the conversation is recorded before anything is queued onto it");
    }

    /// <summary>
    /// A resumed conversation was minted by an earlier process, so there is nothing new to record — and firing
    /// the hook would rewrite a checkpoint that already describes this exact lifecycle.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_reports_no_mint_when_it_resumed_a_conversation()
    {
        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}"
            );
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            title: null,
            existingThreadId: "thread-persisted"
        );
        var minted = new List<string>();

        IResumableReviewTurn resumable = agent;
        resumable.ObserveConversationMint(minted.Add);
        _ = await DriveAsync(agent, "synthesize the final review");

        minted.Should().BeEmpty();
    }

    /// <summary>
    /// The mint checkpoint is the one hook here allowed to take the review down. Swallowing its failure would
    /// leave a hosted conversation fanning out a sub-agent tree that no restart could ever find, so the next
    /// attempt would mint a second one on top of it — two live reviews of one PR. Failing instead leaves one
    /// unrecorded conversation, which the host's retention sweep collects.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_fails_the_turn_when_the_minted_conversation_cannot_be_recorded()
    {
        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-minted\"}");
        using var http = NewHttp(handler);
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);

        IResumableReviewTurn resumable = agent;
        resumable.ObserveConversationMint(_ => throw new InvalidOperationException("checkpoint store is down"));
        Func<Task> act = async () => _ = await DriveAsync(agent, "review this PR");

        _ = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*checkpoint store is down*");
        handler
            .Requests.Should()
            .NotContain(
                r => r.Method == HttpMethod.Post && r.Uri.ToString().Contains("/messages", StringComparison.Ordinal),
                "no fan-out is started on a conversation the caller could not record"
            );
    }

    [Fact]
    public async Task ExecuteRunAsync_obeys_the_supplied_absolute_deadline_instead_of_a_fresh_per_turn_window()
    {
        // Collect → barrier → synthesize share ONE absolute budget. Without clamping, each turn would start
        // its own overallTimeout window, so a review could spend the whole budget on the provisional turn and
        // then spend it AGAIN on synthesis. With a deadline already in the past the agent must give up
        // immediately — proven by the poll never issuing a single status request.

        var handler = new FakeHttpMessageHandler()
            .OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"InProgress\",\"runId\":\"run-slow\"}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-budget\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");
        var agent = NewAgent(client, title: null);
        agent.UseDeadline(DateTimeOffset.UtcNow.AddSeconds(-1));

        Func<Task> act = async () => _ = await DriveAsync(agent, "review this PR");

        await act.Should().ThrowAsync<TimeoutException>();
        handler
            .Requests.Should()
            .NotContain(
                r => r.Uri.ToString().Contains("/status", StringComparison.Ordinal),
                "the exhausted shared budget must not open a fresh per-turn poll window"
            );
    }

    /// <summary>
    /// The point of a PREFLIGHT. A host that predates these contracts silently ignores the request fields
    /// that carry them, so a caller that only reads response acknowledgements finds out once its turn is
    /// already queued — with an unsuppressed sub-agent fan-out running on the host and a retry that would
    /// duplicate the review. Every shape of "cannot keep it" is the same failure and must reach the caller
    /// as the same governed <see cref="ReviewHostContractException"/>: the endpoint is missing (an older
    /// host), it is present and answers no (a skewed deployment), or it refuses this daemon's credential
    /// (401/403 — the capability read carries the same inbound S2S secret a send would, so a refusal there
    /// means no turn could be accepted either). The credential cases matter most: an unmapped 401 would
    /// surface as a raw transport failure that the retry governor does not charge, and the review would
    /// hammer a host that will never admit it.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{\"error\":\"not_found\"}", "does not advertise conversation capabilities")]
    [InlineData(
        HttpStatusCode.OK,
        "{\"schemaVersion\":1,\"messageIdempotency\":false,\"spawnSuppression\":true,\"rootReasoningEffort\":true}",
        "does not support messageIdempotency"
    )]
    [InlineData(
        HttpStatusCode.OK,
        "{\"schemaVersion\":1,\"messageIdempotency\":true,\"spawnSuppression\":true}",
        "does not support rootReasoningEffort"
    )]
    [InlineData(
        HttpStatusCode.Unauthorized,
        "{\"error\":\"unauthorized\",\"code\":\"s2s_auth_failed\"}",
        "rejected this daemon's credential"
    )]
    [InlineData(HttpStatusCode.Forbidden, "{\"error\":\"forbidden\"}", "rejected this daemon's credential")]
    public async Task ExecuteRunAsync_sends_nothing_to_a_host_that_cannot_keep_the_message_contracts(
        HttpStatusCode status,
        string body,
        string expectedDiagnosis
    )
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "conversations/capabilities", body, status)
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"ok\"}}"
            )
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-old-host\"}");
        using var http = NewHttp(handler);
        var agent = NewAgent(new LmStreamingS2SClient(http, "s", "id", "key"), title: null);

        Func<Task> act = async () => _ = await DriveAsync(agent, "review this PR");

        // The parked run's message is the only thing an operator gets, so each refusal must name its OWN
        // cause: telling them to upgrade a host whose version is fine, when the real fault is a stale shared
        // secret, sends them down the wrong path with the review already parked.
        _ = await act.Should().ThrowAsync<ReviewHostContractException>().WithMessage($"*{expectedDiagnosis}*");
        handler
            .Requests.Should()
            .NotContain(
                r => r.Method == HttpMethod.Post,
                "nothing may be provisioned or queued on a host that cannot keep the contract the turn depends on"
            );
    }

    /// <summary>
    /// A resumed review never provisions, so the send is its FIRST call to the host — checking the contract
    /// as part of provisioning would skip exactly the case that matters most, where a conversation with a
    /// live sub-agent tree is about to be handed another turn.
    /// </summary>
    [Fact]
    public async Task ExecuteRunAsync_verifies_the_host_contract_on_a_resumed_thread_too()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "conversations/capabilities", "{}", HttpStatusCode.NotFound)
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-2\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-r\",\"response\":{\"text\":\"ok\"}}"
            );
        using var http = NewHttp(handler);
        var agent = NewAgent(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            title: null,
            existingThreadId: "thread-persisted"
        );

        Func<Task> act = async () => _ = await DriveAsync(agent, "synthesize the final review");

        _ = await act.Should().ThrowAsync<ReviewHostContractException>();
        handler
            .Requests.Should()
            .NotContain(
                r => r.Uri.ToString().Contains("/messages", StringComparison.Ordinal),
                "the resume path must fail before it queues a second turn onto the conversation"
            );
    }
}
