using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.0 — the collect-only review run. <see cref="ReviewAgent"/> sends the review input as a single
/// user turn and gathers the assistant's finalized prose; it ignores streaming deltas and thinking
/// text, joins multiple assistant messages, and reports the run id — all without any posting side
/// effect (it touches only the <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.IMultiTurnAgent"/> seam).
/// </summary>
public sealed class ReviewAgentTests : LoggingTestBase
{
    private const string RunId = "run-42";

    public ReviewAgentTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private ReviewAgent Create(FakeMultiTurnAgent agent) =>
        new(agent, LoggerFactory.CreateLogger<ReviewAgent>());

    [Fact]
    public async Task ReviewAsync_sends_the_input_as_a_single_user_turn()
    {
        var agent = new FakeMultiTurnAgent(RunId);
        var sut = Create(agent);

        _ = await sut.ReviewAsync("Review this diff:\n- changed Foo.cs", postEnforcementPrompt: null, CancellationToken.None);

        agent.ReceivedInputs.Should().ContainSingle();
        var sent = agent.ReceivedInputs[0].Messages.Should().ContainSingle().Subject;
        var text = sent.Should().BeOfType<TextMessage>().Subject;
        text.Role.Should().Be(Role.User);
        text.Text.Should().Contain("Review this diff");
    }

    [Fact]
    public async Task ReviewAsync_collects_the_finalized_assistant_text_and_run_id()
    {
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "## Review\nMust: null check missing in Foo.cs:10",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var result = await Create(agent).ReviewAsync("diff", postEnforcementPrompt: null, CancellationToken.None);

        result.ReviewText.Should().Be("## Review\nMust: null check missing in Foo.cs:10");
        result.RunId.Should().Be(RunId);
    }

    [Fact]
    public async Task ReviewAsync_ignores_streaming_deltas_and_thinking_text()
    {
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextUpdateMessage { Text = "partial", Role = Role.Assistant },
            new TextMessage { Text = "let me think...", Role = Role.Assistant, IsThinking = true },
            new TextMessage { Text = "The review body.", Role = Role.Assistant, RunId = RunId }
        );

        var result = await Create(agent).ReviewAsync("diff", postEnforcementPrompt: null, CancellationToken.None);

        result.ReviewText.Should().Be("The review body.");
    }

    [Fact]
    public async Task ReviewAsync_joins_multiple_assistant_messages_with_newlines()
    {
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage { Text = "First.", Role = Role.Assistant, RunId = RunId },
            new TextMessage { Text = "Second.", Role = Role.Assistant, RunId = RunId }
        );

        var result = await Create(agent).ReviewAsync("diff", postEnforcementPrompt: null, CancellationToken.None);

        result.ReviewText.Should().Be("First.\nSecond.");
    }

    [Fact]
    public async Task ReviewAsync_keeps_only_the_final_generation_dropping_inter_turn_narration()
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

        var result = await Create(agent).ReviewAsync("diff", postEnforcementPrompt: null, CancellationToken.None);

        result.ReviewText.Should().Be("## Review\nApprove with comments.");
        result.ReviewText.Should().NotContain("Let me check").And.NotContain("Sub-agents returned empty");
    }

    [Fact]
    public async Task ReviewAsync_returns_empty_text_when_the_agent_yields_no_assistant_prose()
    {
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage { Text = "let me think...", Role = Role.Assistant, IsThinking = true }
        );

        var result = await Create(agent).ReviewAsync("diff", postEnforcementPrompt: null, CancellationToken.None);

        result.ReviewText.Should().BeEmpty();
        // RunId falls back to the agent's CurrentRunId when no assistant TextMessage carried one.
        result.RunId.Should().Be(RunId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReviewAsync_rejects_blank_input(string? input)
    {
        var sut = Create(new FakeMultiTurnAgent(RunId));

        var act = () => sut.ReviewAsync(input!, postEnforcementPrompt: null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReviewAsync_drives_a_post_enforcement_turn_after_the_review_when_a_prompt_is_supplied()
    {
        // The review agent reliably writes the review but often skips POSTING it (observed live). When posting
        // is authorized the daemon supplies a post-enforcement prompt, and ReviewAgent must drive it as a
        // SECOND turn on the same conversation so the agent actually posts — while the returned artifact stays
        // the FIRST turn's review text (the enforcement turn is only for the posting side-effect).
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage { Text = "the review body", Role = Role.Assistant, RunId = RunId });

        var result = await Create(agent).ReviewAsync("review input", "You have not posted — post it NOW.", CancellationToken.None);

        agent.ReceivedInputs.Should().HaveCount(2, "the review turn, then a post-enforcement turn");
        agent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text.Should().Be("review input");
        agent.ReceivedInputs[1].Messages.OfType<TextMessage>().Single().Text.Should().Be("You have not posted — post it NOW.");
        result.ReviewText.Should().Be("the review body", "the artifact is the review from turn 1, not the enforcement reply");
    }

    [Fact]
    public async Task ReviewAsync_does_not_drive_an_enforcement_turn_when_no_prompt_is_supplied()
    {
        // Collect-only runs (posting not authorized) pass no enforcement prompt and must send exactly one turn.
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage { Text = "the review body", Role = Role.Assistant, RunId = RunId });

        _ = await Create(agent).ReviewAsync("review input", postEnforcementPrompt: null, CancellationToken.None);

        agent.ReceivedInputs.Should().ContainSingle("collect-only runs send only the review turn");
    }
}
