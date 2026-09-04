using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The discussion-only turn (design §5.4). Unlike <see cref="ReviewAgent"/> there is no collect phase
/// and no fan-out phase: the round runs ONE turn with sub-agent spawning suppressed for the whole of
/// it, because the full specialist fleet is unavailable in this intent. Everything here runs against
/// <see cref="FakeMultiTurnAgent"/>, so no test can reach a provider.
/// </summary>
public sealed class DiscussionAgentTests : LoggingTestBase
{
    private const string RunId = "round-9";

    public DiscussionAgentTests(ITestOutputHelper output)
        : base(output) { }

    private static DateTimeOffset Later => DateTimeOffset.UtcNow.AddMinutes(10);

    private static readonly AuditSourceReference Source = new("11", "sha-11");

    private static DiscussionRoundInput Input =>
        new(
            RoundId: 9,
            HeadSha: "abc1234",
            NewComments: [new DiscussionComment("c-1", "octocat", "Why in memory?", Source)],
            OpenQuestions: [],
            Candidates: []
        );

    private static TextMessage Assistant(string text) =>
        new()
        {
            Text = text,
            Role = Role.Assistant,
            RunId = RunId,
        };

    private DiscussionAgent Create(FakeMultiTurnAgent agent, Func<IDisposable>? suppressSpawning = null) =>
        new(agent, LoggerFactory.CreateLogger<DiscussionAgent>(), suppressSpawning ?? agent.SuppressSpawning!);

    [Fact]
    public void A_loop_that_cannot_prove_spawn_suppression_is_refused()
    {
        // ReviewAgent tolerates a null scope because the diff-only path provably has no spawn surface.
        // A discussion round must not: its entire premise is that no fleet runs, and an unproven scope is
        // UNKNOWN, not safe.
        var act = () =>
            new DiscussionAgent(new FakeMultiTurnAgent(RunId), LoggerFactory.CreateLogger<DiscussionAgent>(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Spawning_is_suppressed_for_the_whole_turn()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("nothing to add"));
        var events = new List<string>();
        var sut = Create(
            agent,
            () =>
            {
                events.Add($"suppress@{agent.ReceivedInputs.Count}");
                return new DelegateDisposable(() => events.Add($"release@{agent.ReceivedInputs.Count}"));
            }
        );

        _ = await sut.InterpretAsync(Input, Later, CancellationToken.None);

        events
            .Should()
            .Equal(
                ["suppress@0", "release@1"],
                "the scope opens BEFORE the only turn starts and closes only after it ends"
            );
    }

    [Fact]
    public async Task The_turn_it_sends_is_the_discussion_follow_up_prompt()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("nothing to add"));

        _ = await Create(agent).InterpretAsync(Input, Later, CancellationToken.None);

        var sent = agent.ReceivedInputs.Should().ContainSingle().Subject;
        var text = sent.Messages.OfType<TextMessage>().Single();
        text.Role.Should().Be(Role.User);
        text.Text.Should().StartWith(DiscussionFollowUpPrompt.Banner);
    }

    [Fact]
    public async Task Exactly_one_turn_runs_because_there_is_no_completion_barrier_to_wait_on()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("nothing to add"));

        _ = await Create(agent).InterpretAsync(Input, Later, CancellationToken.None);

        agent.ReceivedInputs.Should().ContainSingle();
    }

    [Fact]
    public async Task The_decision_is_parsed_but_fenced_publication_is_refused()
    {
        var agent = new FakeMultiTurnAgent(
            RunId,
            Assistant(
                "I can answer that.\n\n```discussion-decision\nrelevant: true\nactions:\n  - kind: reply\n    body: per-run\n    ref: c-1\n```"
            )
        );

        var decision = await Create(agent).InterpretAsync(Input, Later, CancellationToken.None);

        decision.IsRelevant.Should().BeTrue();
        decision.Actions.Should().BeEmpty("publication authority belongs only to typed parent operations");
        decision
            .Observations.Should()
            .ContainSingle(observation =>
                observation.Kind == ObservationKind.Gap
                && observation.Summary.Contains("parent-only typed publication tools", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task A_blank_answer_is_a_no_op_decision_rather_than_a_failure()
    {
        // Unlike the synthesis turn there is no authoritative artifact to promote, so an empty answer is
        // a legitimate "I have nothing to add" — the outcome §5.4 explicitly wants to be cheap.
        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "thinking",
                Role = Role.Assistant,
                IsThinking = true,
            }
        );

        var decision = await Create(agent).InterpretAsync(Input, Later, CancellationToken.None);

        decision.IsRelevant.Should().BeFalse();
        decision.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_turn_receives_the_callers_absolute_deadline()
    {
        var deadline = Later;
        var agent = new FakeMultiTurnAgent(RunId, Assistant("x"));

        _ = await Create(agent).InterpretAsync(Input, deadline, CancellationToken.None);

        agent.Deadlines.Should().Equal([deadline]);
    }

    [Fact]
    public async Task A_turn_is_not_started_once_the_deadline_has_passed()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("x"));

        var act = () =>
            Create(agent).InterpretAsync(Input, DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        agent.ReceivedInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_before_the_turn_propagates_and_starts_nothing()
    {
        var agent = new FakeMultiTurnAgent(RunId, Assistant("x"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Create(agent).InterpretAsync(Input, Later, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        agent.ReceivedInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_generation_failure_propagates()
    {
        var boom = new InvalidOperationException("model refused");
        var agent = FakeMultiTurnAgent.Throwing(RunId, boom);

        var act = () => Create(agent).InterpretAsync(Input, Later, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
    }

    [Fact]
    public async Task The_suppression_scope_is_released_even_when_the_turn_throws()
    {
        var agent = FakeMultiTurnAgent.Throwing(RunId, new InvalidOperationException("boom"));
        var events = new List<string>();
        var sut = Create(
            agent,
            () =>
            {
                events.Add("suppress");
                return new DelegateDisposable(() => events.Add("release"));
            }
        );

        var act = () => sut.InterpretAsync(Input, Later, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        events.Should().Equal(["suppress", "release"]);
    }

    private sealed class DelegateDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
