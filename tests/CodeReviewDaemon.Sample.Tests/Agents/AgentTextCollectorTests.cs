using AchieveAi.LmDotnetTools.LmCore.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// Characterization tests for <see cref="AgentTextCollector"/> — the one collect-only drive shared by every
/// daemon agent (Review, Judge, Knowledge, ReviewFeedback, VariantReviewer).
/// <para>
/// <b>Most of what is pinned here cannot happen today, and that is the point.</b> The collector was written
/// for an in-process loop that streams <see cref="TextUpdateMessage"/> deltas across many generations. That
/// loop is gone: <c>Program.cs</c> throws at startup unless <c>UseS2SReviewAgent</c> is true,
/// <c>S2SReviewAgentLoopFactory</c> is the only <c>IReviewAgentLoopFactory</c> in the assembly, and
/// <c>S2SReviewAgent.ExecuteRunAsync</c> has exactly one <c>yield return</c> — a finalized assistant
/// <see cref="TextMessage"/> with no <c>GenerationId</c>. Against that single input the generation reset,
/// the newline join, the whole <see cref="TextUpdateMessage"/> branch, and the streamed arm of the final
/// selection are all unreachable.
/// </para>
/// <para>
/// So these are not assertions that a live bug is absent. They are the record of what the dormant machinery
/// DOES, written while it is still cheap to establish, so that whoever makes it reachable — by adding a
/// second yield, restoring an in-process loop, or pointing the collector at a streaming agent — finds the
/// behaviour described rather than discovering it from a review that came back short. What it does is
/// discard: on a new generation it throws away everything collected so far, with no log, no marker in the
/// text, and nothing in the returned result that distinguishes a run that lost a turn from one that did not.
/// The invariant that keeps it asleep is pinned at its source in
/// <c>S2SReviewAgentTests.ExecuteRunAsync_yields_exactly_one_message_so_the_collector_never_reconstructs</c>.
/// </para>
/// </summary>
public sealed class AgentTextCollectorTests
{
    private const string Prompt = "review this PR";

    private static TextMessage Final(string text, string? generationId = null) =>
        new()
        {
            Text = text,
            Role = Role.Assistant,
            IsThinking = false,
            RunId = "run-1",
            GenerationId = generationId,
        };

    private static TextUpdateMessage Delta(string text, string? generationId = null) =>
        new()
        {
            Text = text,
            Role = Role.Assistant,
            IsThinking = false,
            RunId = "run-1",
            GenerationId = generationId,
        };

    /// <summary>
    /// The shape the daemon actually produces today: one finalized assistant message, no generation id. It
    /// arrives verbatim — no trimming, no re-wrapping — because this text becomes <c>review.md</c> and the
    /// PR comment.
    /// </summary>
    [Fact]
    public async Task The_one_shape_the_daemon_really_produces_is_passed_through_verbatim()
    {
        var agent = new FakeMultiTurnAgent("run-1", Final("BLOCKER: the retry loop is unbounded.\n"));

        var result = await AgentTextCollector.CollectAsync(agent, Prompt, CancellationToken.None);

        result.Text.Should().Be("BLOCKER: the retry loop is unbounded.\n");
        result.RunId.Should().Be("run-1");
    }

    /// <summary>
    /// <b>The drop.</b> A second generation does not append — it erases. Everything the agent said in the
    /// earlier turn is gone from the returned text, and nothing anywhere records that it was ever there.
    /// <para>
    /// The erasure is deliberate (an agent that narrates between tool calls should not have its narration
    /// collected as the answer) but it is indiscriminate: it cannot tell narration from a finding. The text
    /// asserted missing here is a BLOCKER, which is what makes this worth a test rather than a comment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_new_generation_erases_the_previous_one_rather_than_appending_to_it()
    {
        var agent = new FakeMultiTurnAgent(
            "run-1",
            Final("BLOCKER: the retry loop is unbounded.", generationId: "gen-1"),
            Final("Done reviewing.", generationId: "gen-2"));

        var result = await AgentTextCollector.CollectAsync(agent, Prompt, CancellationToken.None);

        result.Text.Should().Be("Done reviewing.");
        result.Text.Should().NotContain(
            "BLOCKER",
            "the earlier generation is cleared, not appended — and a finding is indistinguishable from "
                + "narration to the code doing the clearing");
    }

    /// <summary>
    /// <b>The drop leaves no trace of any kind.</b> A run that discarded an entire turn returns a result
    /// byte-identical to one that discarded nothing — same text, same run id, same shape. There is no
    /// marker in the text, no log line, and (since #59) no count either.
    /// <para>
    /// The count used to exist and is gone precisely because of this test: it was reset alongside the text,
    /// so it reported the same value in both cases and could not have revealed the drop to the caller that
    /// never read it. Making a future drop visible needs a signal that survives the reset AND a consumer
    /// that acts on it. This test is what a candidate signal has to beat.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_discarded_generation_is_indistinguishable_from_never_having_happened()
    {
        var twoGenerations = new FakeMultiTurnAgent(
            "run-1",
            Final("first turn", generationId: "gen-1"),
            Final("second turn", generationId: "gen-2"));
        var oneGeneration = new FakeMultiTurnAgent("run-1", Final("second turn", generationId: "gen-2"));

        var dropped = await AgentTextCollector.CollectAsync(twoGenerations, Prompt, CancellationToken.None);
        var intact = await AgentTextCollector.CollectAsync(oneGeneration, Prompt, CancellationToken.None);

        dropped.Should().BeEquivalentTo(
            intact,
            "the run that lost a whole turn returns exactly what the run that lost nothing returns — no "
                + "field of the result distinguishes them, which is why the count that used to sit here "
                + "could never have been the thing that noticed");
    }

    /// <summary>
    /// Bounds the reset in the other direction, so a future fix cannot over-correct into "keep only the last
    /// message". Two messages in the SAME generation are joined with a newline, not dropped — that is a
    /// single turn split across messages, and losing half of it would be the very failure the reset exists to
    /// avoid, in miniature.
    /// </summary>
    [Fact]
    public async Task Two_messages_of_one_generation_are_joined_not_reduced_to_the_last()
    {
        var agent = new FakeMultiTurnAgent(
            "run-1",
            Final("BLOCKER: unbounded retry.", generationId: "gen-1"),
            Final("NIT: rename the flag.", generationId: "gen-1"));

        var result = await AgentTextCollector.CollectAsync(agent, Prompt, CancellationToken.None);

        result.Text.Should().Be("BLOCKER: unbounded retry.\nNIT: rename the flag.");
    }

    /// <summary>
    /// <b>The selection has no notion of which generation is later.</b> A finalized message wins over
    /// streamed deltas unconditionally, so an agent that finalizes an early turn but only streams its final
    /// one returns the EARLY turn — the collector's two accumulators track generations independently and
    /// nothing reconciles them.
    /// <para>
    /// Whether a real provider produces that interleaving is unproven and deliberately not claimed here.
    /// What is proven is that the code has no rule that would save it if one did: the outcome depends on
    /// which accumulator happens to be non-empty, not on which turn the agent meant as its answer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_finalized_early_turn_outranks_a_streamed_later_one_because_nothing_compares_them()
    {
        var agent = new FakeMultiTurnAgent(
            "run-1",
            Final("Looking at the retry loop now...", generationId: "gen-1"),
            Delta("BLOCKER: ", generationId: "gen-2"),
            Delta("the retry loop is unbounded.", generationId: "gen-2"));

        var result = await AgentTextCollector.CollectAsync(agent, Prompt, CancellationToken.None);

        result.Text.Should().Be("Looking at the retry loop now...");
        result.Text.Should().NotContain(
            "BLOCKER",
            "the later generation was streamed rather than finalized, and the selection prefers finalized "
                + "text without ever asking which generation it belongs to");
    }

    /// <summary>
    /// The delta path, when it is the only path: deltas of one generation concatenate with no separator, and
    /// the result is reported as a single assistant message.
    /// </summary>
    [Fact]
    public async Task Deltas_alone_concatenate_into_one_answer()
    {
        var agent = new FakeMultiTurnAgent(
            "run-1",
            Delta("BLOCKER: ", generationId: "gen-1"),
            Delta("the retry loop is unbounded.", generationId: "gen-1"));

        var result = await AgentTextCollector.CollectAsync(agent, Prompt, CancellationToken.None);

        result.Text.Should().Be("BLOCKER: the retry loop is unbounded.");
    }

    /// <summary>
    /// Thinking text is scratch work, not output, on BOTH paths. This is the same hazard the review host's
    /// status resolver has to defend against: a thinking message is assistant-role and carries real text, so
    /// a collector that only checked the role would publish the model's private deliberation as the review.
    /// </summary>
    [Fact]
    public async Task Thinking_text_is_collected_from_neither_path()
    {
        var agent = new FakeMultiTurnAgent(
            "run-1",
            Final("I should probably just approve this.") with { IsThinking = true },
            Delta("and move on.") with { IsThinking = true },
            Final("BLOCKER: the retry loop is unbounded."));

        var result = await AgentTextCollector.CollectAsync(agent, Prompt, CancellationToken.None);

        result.Text.Should().Be("BLOCKER: the retry loop is unbounded.");
    }

    /// <summary>
    /// A run that produced no assistant text at all returns empty rather than throwing, and reports a count
    /// of zero. Callers are the ones that decide what emptiness means — <c>KnowledgeAgent</c> treats it as a
    /// decline, <c>S2SReviewAgent</c> never lets it happen — so the collector must not decide for them.
    /// </summary>
    [Fact]
    public async Task A_run_with_no_assistant_text_returns_empty_and_a_zero_count()
    {
        var agent = new FakeMultiTurnAgent("run-1", new TextMessage { Text = Prompt, Role = Role.User });

        var result = await AgentTextCollector.CollectAsync(agent, Prompt, CancellationToken.None);

        result.Text.Should().BeEmpty();
        result.RunId.Should().Be("run-1");
    }
}
