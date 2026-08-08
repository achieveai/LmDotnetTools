using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The two-phase review run (recursive-review completion barrier, Task 5). One
/// <see cref="ReviewAgent"/> — one in-process agent, one conversation thread — drives:
/// <list type="number">
/// <item><see cref="ReviewAgent.CollectProvisionalAsync"/>: ONE collect-only turn whose answer is
///   provisional. It has no posting/enforcement parameter at all, so a collect-only turn is structurally
///   all it can ever be, whatever the run's posting configuration.</item>
/// <item><see cref="ReviewAgent.SynthesizeFinalAsync"/>: the authoritative second turn, run AFTER the
///   caller's completion barrier, with sub-agent spawning suppressed for its duration.</item>
/// </list>
/// Both phases share ONE caller-supplied absolute deadline; neither invents a window of its own. The
/// agent still touches only the <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.IMultiTurnAgent"/> seam,
/// so all of this is verifiable against a fake.
/// </summary>
public sealed class ReviewAgentTests : LoggingTestBase
{
    private const string RunId = "run-42";

    public ReviewAgentTests(ITestOutputHelper output)
        : base(output)
    {
    }

    /// <summary>A deadline far enough out that no test in this class trips it accidentally.</summary>
    private static DateTimeOffset Later => DateTimeOffset.UtcNow.AddMinutes(30);

    private ReviewAgent Create(FakeMultiTurnAgent agent, Func<IDisposable>? suppressSpawning = null) =>
        new(agent, LoggerFactory.CreateLogger<ReviewAgent>(), suppressSpawning);

    private static TextMessage Assistant(string text) =>
        new() { Text = text, Role = Role.Assistant, RunId = RunId };

    [Fact]
    public async Task CollectProvisionalAsync_fails_when_the_agent_stream_was_severed_mid_answer()
    {
        // A severed stream (this consumer was dropped from fan-out) ends the enumeration exactly like
        // a completed run does. Returning the text gathered so far would hand the daemon a silently
        // truncated review that reads as a complete one, so the drive must fail instead.
        var agent = new FakeMultiTurnAgent(
            RunId,
            Assistant("## Review\nMust: null check missing in Foo.cs:10"),
            new StreamRecoveryMessage("thread-1", RunId, "gen-1", StreamRecoveryReason.SlowConsumer));
        var sut = Create(agent);

        var collect = async () =>
            await sut.CollectProvisionalAsync("Review this diff", Later, CancellationToken.None);

        _ = await collect.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CollectProvisionalAsync_sends_the_input_as_a_single_user_turn()
    {
        var agent = new FakeMultiTurnAgent(RunId);
        var sut = Create(agent);

        _ = await sut.CollectProvisionalAsync("Review this diff:\n- changed Foo.cs", Later, CancellationToken.None);

        agent.ReceivedInputs.Should().ContainSingle();
        var sent = agent.ReceivedInputs[0].Messages.Should().ContainSingle().Subject;
        var text = sent.Should().BeOfType<TextMessage>().Subject;
        text.Role.Should().Be(Role.User);
        text.Text.Should().Contain("Review this diff");
    }

    [Fact]
    public async Task CollectProvisionalAsync_collects_the_finalized_assistant_text_and_run_id()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("## Review\nMust: null check missing in Foo.cs:10"));

        var result = await Create(agent).CollectProvisionalAsync("diff", Later, CancellationToken.None);

        result.ReviewText.Should().Be("## Review\nMust: null check missing in Foo.cs:10");
        result.RunId.Should().Be(RunId);
    }

    [Fact]
    public async Task CollectProvisionalAsync_ignores_streaming_deltas_and_thinking_text()
    {
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextUpdateMessage { Text = "partial", Role = Role.Assistant },
            new TextMessage { Text = "let me think...", Role = Role.Assistant, IsThinking = true },
            Assistant("The review body.")
        );

        var result = await Create(agent).CollectProvisionalAsync("diff", Later, CancellationToken.None);

        result.ReviewText.Should().Be("The review body.");
    }

    [Fact]
    public async Task CollectProvisionalAsync_joins_multiple_assistant_messages_with_newlines()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("First."), Assistant("Second."));

        var result = await Create(agent).CollectProvisionalAsync("diff", Later, CancellationToken.None);

        result.ReviewText.Should().Be("First.\nSecond.");
    }

    [Fact]
    public async Task CollectProvisionalAsync_keeps_only_the_final_generation_dropping_inter_turn_narration()
    {
        // A tool-using review agent narrates its process in earlier turns (each its own streaming
        // generation) and emits the finished review in the final turn. The collector must return ONLY the
        // final generation's text, so the narration never leaks into the persisted review.
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextUpdateMessage { Text = "Let me check the file.", Role = Role.Assistant, GenerationId = "g1" },
            new TextUpdateMessage { Text = "Sub-agents returned empty; proceeding.", Role = Role.Assistant, GenerationId = "g2" },
            new TextUpdateMessage { Text = "## Review\n", Role = Role.Assistant, GenerationId = "g3" },
            new TextUpdateMessage { Text = "Approve with comments.", Role = Role.Assistant, GenerationId = "g3" }
        );

        var result = await Create(agent).CollectProvisionalAsync("diff", Later, CancellationToken.None);

        result.ReviewText.Should().Be("## Review\nApprove with comments.");
        result.ReviewText.Should().NotContain("Let me check").And.NotContain("Sub-agents returned empty");
    }

    [Fact]
    public async Task CollectProvisionalAsync_returns_empty_text_when_the_agent_yields_no_assistant_prose()
    {
        // The PROVISIONAL answer is allowed to be empty: it is never posted, judged, or persisted as the
        // authoritative review — only the synthesis answer is, and that one throws when blank.
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage { Text = "let me think...", Role = Role.Assistant, IsThinking = true }
        );

        var result = await Create(agent).CollectProvisionalAsync("diff", Later, CancellationToken.None);

        result.ReviewText.Should().BeEmpty();
        // RunId falls back to the agent's CurrentRunId when no assistant TextMessage carried one.
        result.RunId.Should().Be(RunId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CollectProvisionalAsync_rejects_blank_input(string? input)
    {
        var sut = Create(new FakeMultiTurnAgent(RunId));

        var act = () => sut.CollectProvisionalAsync(input!, Later, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SynthesizeFinalAsync_runs_on_the_same_agent_and_returns_the_second_answer()
    {
        // The whole point of Task 5: ONE agent, ONE conversation, two turns. The authoritative review is
        // the SECOND answer — written after the children settled — not the provisional first one.
        var agent = new FakeMultiTurnAgent(RunId, Assistant("provisional — children still running"))
            .ThenReplies(Assistant("## Review\nAuthoritative, after settlement."));
        var sut = Create(agent);

        var provisional = await sut.CollectProvisionalAsync("review input", Later, CancellationToken.None);
        var final = await sut.SynthesizeFinalAsync("synthesize now", allowInlinePosting: true, Later, CancellationToken.None);

        agent.ReceivedInputs.Should().HaveCount(2, "the provisional turn, then the synthesis turn");
        agent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text.Should().Be("review input");
        agent.ReceivedInputs[1].Messages.OfType<TextMessage>().Single().Text.Should().Be("synthesize now");
        provisional.ReviewText.Should().Be("provisional — children still running");
        final.ReviewText.Should().Be("## Review\nAuthoritative, after settlement.");
        final.ThreadId.Should().Be(provisional.ThreadId, "synthesis runs on the SAME conversation thread");
    }

    [Fact]
    public async Task SynthesizeFinalAsync_suppresses_spawning_only_for_its_own_turn()
    {
        // The synthesis profile must not be able to start NEW children after the barrier opened; it must
        // still be able to read what the settled children delivered. ReviewAgent owns only the SCOPE (the
        // narrow SubAgentToolProvider seam supplies the behaviour), so that is what is asserted here.
        var agent = new FakeMultiTurnAgent(RunId, Assistant("provisional")).ThenReplies(Assistant("final"));
        var events = new List<string>();
        var sut = Create(
            agent,
            () =>
            {
                events.Add($"suppress@{agent.ReceivedInputs.Count}");
                return new DelegateDisposable(() => events.Add($"release@{agent.ReceivedInputs.Count}"));
            });

        _ = await sut.CollectProvisionalAsync("review input", Later, CancellationToken.None);
        events.Should().BeEmpty("the provisional turn may still spawn sub-agents");

        _ = await sut.SynthesizeFinalAsync("synthesize now", allowInlinePosting: false, Later, CancellationToken.None);

        events.Should().Equal(
            ["suppress@1", "release@2"],
            "spawning is suppressed before the synthesis turn starts and released only after it ends");
    }

    [Fact]
    public async Task SynthesizeFinalAsync_propagates_a_generation_failure()
    {
        // Provider VERIFICATION stays outside this method (Task 7 owns the fallback), but a synthesis
        // GENERATION failure has produced no authoritative review at all — it must not be swallowed.
        var boom = new InvalidOperationException("model refused");
        var agent = new FakeMultiTurnAgent(RunId, Assistant("provisional")).ThenThrows(boom);
        var sut = Create(agent);

        _ = await sut.CollectProvisionalAsync("review input", Later, CancellationToken.None);
        var act = () => sut.SynthesizeFinalAsync("synthesize now", allowInlinePosting: true, Later, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
    }

    [Fact]
    public async Task SynthesizeFinalAsync_throws_when_the_synthesis_answer_is_blank()
    {
        // A blank synthesis is indistinguishable from "no review": there is nothing authoritative to
        // persist, post or judge, so it fails loudly instead of promoting an empty artifact.
        var agent = new FakeMultiTurnAgent(RunId, Assistant("provisional"))
            .ThenReplies(new TextMessage { Text = "thinking", Role = Role.Assistant, IsThinking = true });
        var sut = Create(agent);

        _ = await sut.CollectProvisionalAsync("review input", Later, CancellationToken.None);
        var act = () => sut.SynthesizeFinalAsync("synthesize now", allowInlinePosting: true, Later, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no review text*");
    }

    [Fact]
    public async Task Both_turns_receive_the_one_supplied_absolute_deadline()
    {
        // Collect, barrier and synthesis share ONE budget. ReviewAgent pushes the caller's absolute
        // deadline into a deadline-bounded loop before EVERY turn, so the second turn cannot open a fresh
        // per-turn window with the wall clock already deep into the budget.
        var deadline = Later;
        var agent = new FakeMultiTurnAgent(RunId, Assistant("provisional")).ThenReplies(Assistant("final"));
        var sut = Create(agent);

        _ = await sut.CollectProvisionalAsync("review input", deadline, CancellationToken.None);
        _ = await sut.SynthesizeFinalAsync("synthesize now", allowInlinePosting: false, deadline, CancellationToken.None);

        agent.Deadlines.Should().Equal(deadline, deadline);
    }

    [Fact]
    public async Task A_turn_is_not_started_once_the_shared_deadline_has_passed()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("provisional"));
        var sut = Create(agent);

        var act = () => sut.CollectProvisionalAsync("review input", DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        agent.ReceivedInputs.Should().BeEmpty("an expired budget must not start a turn it cannot finish");
    }

    private sealed class DelegateDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
