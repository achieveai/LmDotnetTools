using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P6 §4.2 — what the move onto the shared LmEval harness must NOT change, and the one thing it
/// deliberately adds.
/// <para>
/// <see cref="JudgeAgentTests"/> is the no-behaviour-change proof and stays untouched. These are the
/// facts that only exist because of the migration: an unreadable reply is now an <i>abstention</i>
/// inside the harness even though it is still persisted as <c>0</c>, and the anchored
/// <c>judge: v2.0</c> prompt must ship dark rather than silently becoming the live one.
/// </para>
/// </summary>
public sealed class JudgeHarnessAdapterTests
{
    private const string RunId = "judge-run-1";
    private const string Provider = "github";

    private static readonly IPromptReader Prompts = new PromptReader(
        typeof(DaemonAgentFactory).Assembly,
        "CodeReviewDaemon.Sample.Prompts.daemon-prompts.yaml");

    /// <summary>
    /// The characterization test §4.2 asks for. Score <c>0</c> is persisted exactly as v1 persisted
    /// it — but <c>0</c> now means two distinguishable things in the log, because the abstention is
    /// named. Without the warning an unreadable reply and a genuinely worthless review are the same
    /// row with the same score and nothing anywhere to tell them apart.
    /// </summary>
    [Fact]
    public async Task A_malformed_reply_persists_no_score_at_all_and_names_the_abstention()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);
        var logger = new CapturingLogger<JudgeAgent>();

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "I could not produce a structured verdict.",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var verdict = await new JudgeAgent(agent, store, logger).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "b", "grade"),
            CancellationToken.None
        );

        verdict.Score.Should().BeNull();
        verdict.Rationale.Should().Be("I could not produce a structured verdict.");

        var artifact = store.GetArtifacts(reviewRunId).Should().ContainSingle().Subject;
        using var recorded = JsonDocument.Parse(artifact.Payload);
        recorded
            .RootElement.GetProperty("Score")
            .ValueKind.Should()
            .Be(
                JsonValueKind.Null,
                "0 is a legitimate worst score under this rubric, so persisting one for an "
                    + "unreadable reply makes every stored verdict ambiguous after the fact"
            );

        logger.CountAtLevel(LogLevel.Warning, "no usable score").Should().Be(1);
    }

    /// <summary>
    /// The aggregator has TWO exclusion channels — <c>Abstained</c>, and a confidence below
    /// <c>HarnessOptions.AbstainFloor</c> — and only the first sets <c>Abstained</c>. A reply that
    /// parsed perfectly and carries a real score can therefore still be excluded, leaving zero
    /// counted ballots and a null <c>Verdict.Score</c> — which v1 persisted as an invented 0.
    /// Gating the warning on <c>Abstained</c> makes that case silent: the artifact recorded a
    /// genuine 7 as a 0 with no log line anywhere. The warning is gated on the condition that
    /// actually loses the score, which is the same condition v2 now persists as null.
    /// <para>
    /// The daemon's live <c>judge: v1.0</c> prompt does not ask for <c>confidence</c>, but the
    /// harness's own <c>RubricPromptRenderer</c> instructs the model to emit it, so this becomes
    /// the normal path the moment any consumer renders with the default.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_parsed_reply_excluded_on_confidence_warns_and_persists_no_score()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);
        var logger = new CapturingLogger<JudgeAgent>();

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "{\"score\": 7, \"rationale\": \"Solid.\", \"confidence\": 0.2}",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var verdict = await new JudgeAgent(agent, store, logger).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "b", "grade"),
            CancellationToken.None
        );

        verdict.Score.Should().BeNull();
        verdict.Rationale.Should().Be("Solid.");
        logger.CountAtLevel(LogLevel.Warning, "no usable score").Should().Be(1);
    }

    /// <summary>
    /// The mirror image: a reply the harness CAN read must not warn. Without this, the assertion
    /// above would still pass if the adapter warned on every verdict, which would make the warning
    /// carry no information at all.
    /// </summary>
    [Fact]
    public async Task A_readable_reply_does_not_warn()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);
        var logger = new CapturingLogger<JudgeAgent>();

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "{\"score\": 7, \"rationale\": \"Solid.\"}",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var verdict = await new JudgeAgent(agent, store, logger).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "b", "grade"),
            CancellationToken.None
        );

        verdict.Score.Should().Be(7);
        logger.CountAtLevel(LogLevel.Warning, "no usable score").Should().Be(0);
    }

    /// <summary>
    /// <c>judge: v2.0</c> ships DARK. <see cref="IPromptReader.GetPrompt"/> defaults to
    /// <c>"latest"</c> and resolves it by highest semantic version, so merely ADDING v2.0 to the
    /// YAML would have switched the live judge over to it — silently, with no test failing, because
    /// the existing profile test only asserts the prompt is non-empty. The profile therefore pins
    /// its version explicitly, and this is the assertion that keeps it pinned.
    /// </summary>
    [Fact]
    public void The_judge_profile_serves_v1_while_the_anchored_v2_prompt_ships_dark()
    {
        var served = DaemonAgentFactory.CreateJudgeProfile().SystemPrompt;

        served.Should().Be(Prompts.GetPrompt("judge", "v1.0").PromptText());
        served.Should().NotBe(Prompts.GetPrompt("judge", "v2.0").PromptText());
    }

    /// <summary>
    /// The dark prompt and the rubric it renders must stay in step: v2.0 exists precisely so the
    /// judge can see the anchors, and an anchor edited on one side only would ship a prompt that
    /// scores a scale the harness does not describe.
    /// </summary>
    [Fact]
    public void The_v2_prompt_renders_every_anchor_of_the_rubric_it_scores()
    {
        var prompt = Prompts.GetPrompt("judge", "v2.0").PromptText();
        var criterion = JudgeAgent.ReviewRubric.Criteria.Should().ContainSingle().Subject;

        prompt.Should().Contain(criterion.CriterionId);
        foreach (var anchor in criterion.Anchors.Values)
        {
            prompt.Should().Contain(anchor);
        }
    }

    private static long SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "R_node_123",
        });
        return store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Reviewed,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        }).Id;
    }

    /// <summary>
    /// The other side of the same claim, and why a nullable score is the fix rather than a flag
    /// beside it: 0 is a legitimate worst score under this rubric, whose 0 anchor reads "no finding
    /// is correct". A reader who skips a would-be <c>Unscored</c> flag still sees a 0 and cannot
    /// tell which one it is. A null cannot be misread that way.
    /// </summary>
    [Fact]
    public async Task A_zero_the_judge_actually_gave_is_persisted_as_a_zero()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);
        var logger = new CapturingLogger<JudgeAgent>();

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "{\"score\": 0, \"rationale\": \"Every finding restates the diff.\"}",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        var verdict = await new JudgeAgent(agent, store, logger).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "b", "grade"),
            CancellationToken.None
        );

        verdict.Score.Should().Be(0);
        logger.CountAtLevel(LogLevel.Warning, "no usable score").Should().Be(0);

        var artifact = store.GetArtifacts(reviewRunId).Should().ContainSingle().Subject;
        using var payload = JsonDocument.Parse(artifact.Payload);
        payload.RootElement.GetProperty("Score").ValueKind.Should().Be(JsonValueKind.Number);
        payload.RootElement.GetProperty("Score").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// §3.2: a persisted judge family measures nothing without the generator's beside it, and the
    /// self-preference axis is unmeasurable retrospectively — a row that did not record who graded
    /// it is permanently unknown-provenance. Schema v2 records both models and states the derived
    /// relation, so a reader does not have to compare two model ids to learn a grade was
    /// self-issued.
    /// </summary>
    [Theory]
    [InlineData("openai/gpt-5", true)]
    [InlineData("anthropic/claude-opus-4", false)]
    public async Task The_artifact_records_who_graded_it_and_whether_that_was_the_generator(
        string judgeModelId,
        bool selfGraded
    )
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var reviewRunId = SeedRun(store);

        var agent = new FakeMultiTurnAgent(
            RunId,
            new TextMessage
            {
                Text = "{\"score\": 8, \"rationale\": \"Solid.\"}",
                Role = Role.Assistant,
                RunId = RunId,
            }
        );

        _ = await new JudgeAgent(agent, store, new CapturingLogger<JudgeAgent>()).JudgeAsync(
            new JudgeRequest(reviewRunId, Provider, "b", "grade")
            {
                JudgeModelId = judgeModelId,
                GeneratorModelId = "openai/gpt-5",
            },
            CancellationToken.None
        );

        var artifact = store.GetArtifacts(reviewRunId).Should().ContainSingle().Subject;
        artifact.ArtifactSchemaVersion.Should().Be(2);

        using var payload = JsonDocument.Parse(artifact.Payload);
        payload.RootElement.GetProperty("JudgeModelId").GetString().Should().Be(judgeModelId);
        payload.RootElement.GetProperty("GeneratorModelId").GetString().Should().Be("openai/gpt-5");
        payload.RootElement.GetProperty("SelfGraded").GetBoolean().Should().Be(selfGraded);
    }
}
