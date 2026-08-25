using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Gates;
using AchieveAi.LmDotnetTools.LmEval.Judges;
using AchieveAi.LmDotnetTools.LmEval.Running;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The evaluator config hash exists to refuse a comparison whose evaluator side moved. Every test
/// here is a claim about what must move it and what must not.
/// </summary>
public class EvaluatorConfigTests
{
    private static EvaluatorConfig Build(
        IReadOnlyList<IGate>? gates = null,
        IReadOnlyList<IJudge>? judges = null,
        HarnessOptions? options = null,
        string reliabilitySnapshotId = "snap-1",
        IReadOnlyList<string>? humanSignalSources = null
    ) =>
        EvaluatorConfig.Create(
            gates ?? [],
            judges ?? [new ScoringJudge("j-a", "anthropic", _ => 8.0)],
            new WeightedMeanAggregator(),
            options ?? new HarnessOptions(),
            reliabilitySnapshotId,
            humanSignalSources
        );

    [Fact]
    public void An_identical_configuration_hashes_identically()
    {
        Build().Hash.Should().Be(Build().Hash);
    }

    [Fact]
    public void Moving_only_a_gate_bound_moves_the_hash()
    {
        // The headline case. Same corpus, same rubric, same candidate output, one gate bound
        // retuned: the reported pass rate moves with nothing about the candidate having changed,
        // and without this the comparison would read that as a candidate regression.
        var tight = Build([new LengthBoundsGate(minimumLength: 10, maximumLength: 100)]);
        var loose = Build([new LengthBoundsGate(minimumLength: 10, maximumLength: 500)]);

        loose.Hash.Should().NotBe(tight.Hash);
    }

    [Fact]
    public void Reordering_the_gates_moves_the_hash()
    {
        // Gates short-circuit, so the same set in a different order rejects on a different gate and
        // records a different reason.
        var anchor = new RequiredAnchorGate(minimumAnchors: 1);
        var length = new LengthBoundsGate(minimumLength: 1, maximumLength: 100);

        Build([anchor, length]).Hash.Should().NotBe(Build([length, anchor]).Hash);
    }

    [Fact]
    public void Swapping_only_the_judge_model_moves_the_hash()
    {
        var before = Build([], [new ScoringJudge("j-a", "anthropic", _ => 8.0, modelId: "m1")]);
        var after = Build([], [new ScoringJudge("j-a", "anthropic", _ => 8.0, modelId: "m2")]);

        after.Hash.Should().NotBe(before.Hash);
    }

    [Fact]
    public void Changing_only_the_judge_prompt_template_moves_the_hash()
    {
        var before = Build([], [new ScoringJudge("j-a", "anthropic", _ => 8.0, fingerprint: "p1")]);
        var after = Build([], [new ScoringJudge("j-a", "anthropic", _ => 8.0, fingerprint: "p2")]);

        after.Hash.Should().NotBe(before.Hash);
    }

    [Fact]
    public void Adding_an_arbiter_moves_the_hash()
    {
        // The arbiter's ABSENCE is hashed as explicitly as its presence: it changes how every
        // straddle resolves.
        var withArbiter = Build(
            options: new HarnessOptions
            {
                ArbiterJudge = new ScoringJudge("arb", "openai", _ => 9.0),
            }
        );

        withArbiter.Hash.Should().NotBe(Build().Hash);
    }

    [Fact]
    public void Changing_only_the_abstain_floor_moves_the_hash()
    {
        Build(options: new HarnessOptions { AbstainFloor = 0.5 })
            .Hash.Should()
            .NotBe(Build().Hash);
    }

    [Fact]
    public void Changing_only_the_dispersion_alarm_moves_the_hash()
    {
        Build(options: new HarnessOptions { DispersionAlarm = 2.0 })
            .Hash.Should()
            .NotBe(Build().Hash);
    }

    [Fact]
    public void Changing_only_the_reliability_snapshot_moves_the_hash()
    {
        // A refit alone changes every weighted score with nothing on the candidate side moving.
        Build(reliabilitySnapshotId: "snap-2").Hash.Should().NotBe(Build().Hash);
    }

    [Fact]
    public void Widening_the_human_signal_source_set_moves_the_hash()
    {
        Build(humanSignalSources: ["fixed-finding", "explicit"])
            .Hash.Should()
            .NotBe(Build(humanSignalSources: ["fixed-finding"]).Hash);
    }

    [Fact]
    public void Declaring_the_same_human_signal_sources_in_another_order_does_not_move_the_hash()
    {
        Build(humanSignalSources: ["explicit", "fixed-finding"])
            .Hash.Should()
            .Be(Build(humanSignalSources: ["fixed-finding", "explicit"]).Hash);
    }

    [Fact]
    public void A_gate_that_cannot_describe_its_configuration_is_refused()
    {
        var act = () => Build([new OpaqueGate()]);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*does not implement IConfigurationFingerprint*");
    }

    [Fact]
    public void A_judge_with_a_custom_renderer_and_no_declared_template_hash_is_refused()
    {
        // A judge handed an opaque host-supplied renderer genuinely does not know which bytes it
        // will send. Substituting a constant would hash two different prompts identically.
        var judge = new RubricJudge(
            new RubricJudgeOptions
            {
                JudgeId = "j-custom",
                ModelId = "m",
                ModelFamily = "anthropic",
                PromptRenderer = (_, _, _) => "a hand-rolled prompt",
            },
            (_, _) => Task.FromResult("SCORE: 8")
        );

        var act = () => Build([], [judge]);

        act.Should().Throw<ArgumentException>().WithMessage("*null configuration fingerprint*");
    }

    [Fact]
    public void A_judge_on_the_default_renderer_needs_no_declared_template_hash()
    {
        var judge = new RubricJudge(
            new RubricJudgeOptions
            {
                JudgeId = "j-default",
                ModelId = "m",
                ModelFamily = "anthropic",
            },
            (_, _) => Task.FromResult("SCORE: 8")
        );

        Build([], [judge]).Hash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_judge_with_a_custom_renderer_and_a_declared_template_hash_is_accepted()
    {
        static RubricJudge Judge(string templateHash) =>
            new(
                new RubricJudgeOptions
                {
                    JudgeId = "j-custom",
                    ModelId = "m",
                    ModelFamily = "anthropic",
                    PromptRenderer = (_, _, _) => "a hand-rolled prompt",
                    PromptTemplateHash = templateHash,
                },
                (_, _) => Task.FromResult("SCORE: 8")
            );

        Build([], [Judge("t2")]).Hash.Should().NotBe(Build([], [Judge("t1")]).Hash);
    }

    [Fact]
    public void An_arbiter_sharing_a_panel_judge_s_id_is_refused()
    {
        // The reduction partitions ballots into panel and arbiter by judge id alone, so a shared id
        // makes a genuine straddle record as a consensus — which silently suppresses the straddle
        // rate this runner reports as its headline judge-reliability diagnostic.
        var act = () =>
            Build(
                [],
                [new ScoringJudge("shared", "anthropic", _ => 8.0)],
                new HarnessOptions
                {
                    ArbiterJudge = new ScoringJudge("shared", "openai", _ => 9.0),
                }
            );

        act.Should().Throw<ArgumentException>().WithMessage("*is also a panel judge id*");
    }

    [Fact]
    public async Task The_gauntlet_it_builds_is_the_configuration_it_hashed()
    {
        var judge = new ScoringJudge("j-a", "anthropic", _ => 8.0);
        var config = Build([], [judge]);

        var verdict = await config
            .BuildGauntlet()
            .RunAsync(
                EvalFixtures.Item("x"),
                HarnessFixtures.Rubric(),
                new Dictionary<string, double>(),
                CancellationToken.None
            );

        verdict.Ballots.Should().ContainSingle().Which.JudgeId.Should().Be("j-a");
        judge.SeenCandidateIds.Should().Equal("x");
    }
}
