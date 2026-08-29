using AchieveAi.LmDotnetTools.LmEval.Judges;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The default judge (P6 spec §4.2): render the rubric into a turn, send it, parse the reply.
/// </summary>
public sealed class RubricJudgeTests
{
    private static readonly Rubric Rubric = HarnessFixtures.Rubric();

    private static readonly RubricJudgeOptions Options = new()
    {
        JudgeId = "judge-a",
        ModelId = "family-a/model-1",
        ModelFamily = "family-a",
    };

    [Fact]
    public async Task It_renders_a_rubric_turn_and_parses_the_reply_into_a_ballot()
    {
        string? sent = null;
        var judge = new RubricJudge(
            Options,
            (prompt, _) =>
            {
                sent = prompt;
                return Task.FromResult("""{"reasoning":"cites its lines","scores":{"quality":8}}""");
            }
        );

        var ballot = await judge.JudgeAsync(
            HarnessFixtures.Candidate(content: "the review"),
            Rubric,
            new JudgeContext(),
            CancellationToken.None
        );

        sent.Should().Contain("the review").And.Contain("quality");
        ballot.WeightedScore.Should().Be(8.0);
        ballot.JudgeId.Should().Be("judge-a");
        ballot.ModelFamily.Should().Be("family-a");
    }

    [Fact]
    public async Task An_unreadable_reply_becomes_an_abstention_rather_than_a_zero()
    {
        var judge = new RubricJudge(Options, (_, _) => Task.FromResult("I cannot judge this."));

        var ballot = await judge.JudgeAsync(
            HarnessFixtures.Candidate(),
            Rubric,
            new JudgeContext(),
            CancellationToken.None
        );

        ballot.Abstained.Should().BeTrue();
        ballot.Reasoning.Should().Be("I cannot judge this.");
    }

    /// <summary>
    /// The host may already own a rendered prompt — the Revobot adapter does — and re-rendering it
    /// would change the bytes that reach the model.
    /// </summary>
    [Fact]
    public async Task A_host_supplied_renderer_replaces_the_rubric_prompt_verbatim()
    {
        string? sent = null;
        var judge = new RubricJudge(
            Options with
            {
                PromptRenderer = (candidate, _, _) => candidate.Content,
            },
            (prompt, _) =>
            {
                sent = prompt;
                return Task.FromResult("""{"score":7,"rationale":"ok"}""");
            }
        );

        _ = await judge.JudgeAsync(
            HarnessFixtures.Candidate(content: "Grade this code review:\n\n## Review\n..."),
            Rubric,
            new JudgeContext(),
            CancellationToken.None
        );

        sent.Should().Be("Grade this code review:\n\n## Review\n...");
    }

    [Fact]
    public async Task The_reference_reaches_the_rendered_turn()
    {
        string? sent = null;
        var judge = new RubricJudge(
            Options,
            (prompt, _) =>
            {
                sent = prompt;
                return Task.FromResult("""{"score":7,"rationale":"ok"}""");
            }
        );

        _ = await judge.JudgeAsync(
            HarnessFixtures.Candidate(),
            Rubric,
            new JudgeContext { Reference = "the accepted review" },
            CancellationToken.None
        );

        sent.Should().Contain("the accepted review");
    }

    /// <summary>
    /// The default transport, over a real <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.IMultiTurnAgent"/>.
    /// </summary>
    [Fact]
    public async Task The_agent_backed_transport_drives_one_collect_only_turn()
    {
        var agent = new ScriptedAgent("""{"reasoning":"solid","scores":{"quality":9}}""");
        var judge = RubricJudge.Over(agent, Options);

        var ballot = await judge.JudgeAsync(
            HarnessFixtures.Candidate(content: "the review"),
            Rubric,
            new JudgeContext(),
            CancellationToken.None
        );

        agent.Prompts.Should().ContainSingle().Which.Should().Contain("the review");
        ballot.WeightedScore.Should().Be(9.0);
        ballot.Reasoning.Should().Be("solid");
    }

    /// <summary>
    /// A transport failure is the judge's caller's problem to classify. Swallowing it here would
    /// turn a provider outage into an abstention, which the aggregator counts as the judge having
    /// declined — a different fact, and one that hides the outage.
    /// </summary>
    [Fact]
    public async Task A_transport_failure_propagates_rather_than_becoming_an_abstention()
    {
        var judge = new RubricJudge(
            Options,
            (_, _) => Task.FromException<string>(new HttpRequestException("provider down"))
        );

        var act = () =>
            judge.JudgeAsync(HarnessFixtures.Candidate(), Rubric, new JudgeContext(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
