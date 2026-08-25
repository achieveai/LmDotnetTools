using AchieveAi.LmDotnetTools.LmEval.Aggregation;
using AchieveAi.LmDotnetTools.LmEval.Gates;
using AchieveAi.LmDotnetTools.LmEval.Judges;
using AchieveAi.LmDotnetTools.LmEval.Running;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;
using FluentAssertions.Execution;

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
        IReadOnlyList<string>? humanSignalSources = null,
        IReadOnlyDictionary<string, double>? reliabilityWeights = null
    ) =>
        EvaluatorConfig.Create(
            gates ?? [],
            judges ?? [new ScoringJudge("j-a", "anthropic", _ => 8.0)],
            new WeightedMeanAggregator(),
            options ?? new HarnessOptions(),
            reliabilitySnapshotId,
            reliabilityWeights ?? new Dictionary<string, double>(),
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
                TransportFingerprint = "temperature=0",
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
                    TransportFingerprint = "temperature=0",
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

    [Fact]
    public void Every_hashed_evaluator_field_moves_the_hash_on_its_own()
    {
        // A table with one row per field the hash builder appends, each row moving EXACTLY that
        // field and holding every other one fixed. That is what this buys over the wholesale
        // "swap a judge model" tests above: each of these four appends can be replaced with
        // string.Empty and the rest of the suite stays green, because no other test varies the
        // field alone. The comparability refusal is only as good as the weakest of them.
        var arbiter = new ScoringJudge("arb", "anthropic", _ => 8.0, modelId: "vendor/arb-1");

        (string Field, EvaluatorConfig Baseline, EvaluatorConfig Moved)[] rows =
        [
            (
                "gate.AppliesTo",
                Build([new MarkerGate("m")]),
                Build([new MarkerGate("m", appliesTo: [HarnessFixtures.TaskType])])
            ),
            (
                "judge.ModelFamily",
                Build(judges: [new ScoringJudge("j-a", "anthropic", _ => 8.0, modelId: "vendor/m")]),
                Build(judges: [new ScoringJudge("j-a", "google", _ => 8.0, modelId: "vendor/m")])
            ),
            (
                "arbiter.ModelId",
                Build(options: new HarnessOptions { ArbiterJudge = arbiter }),
                Build(
                    options: new HarnessOptions
                    {
                        ArbiterJudge = new ScoringJudge(
                            "arb",
                            "anthropic",
                            _ => 8.0,
                            modelId: "vendor/arb-2"
                        ),
                    }
                )
            ),
            (
                "arbiter.ModelFamily",
                Build(options: new HarnessOptions { ArbiterJudge = arbiter }),
                Build(
                    options: new HarnessOptions
                    {
                        ArbiterJudge = new ScoringJudge(
                            "arb",
                            "google",
                            _ => 8.0,
                            modelId: "vendor/arb-1"
                        ),
                    }
                )
            ),
        ];

        using var scope = new AssertionScope();
        foreach (var (field, baseline, moved) in rows)
        {
            moved
                .Hash.Should()
                .NotBe(baseline.Hash, "moving only {0} must move the evaluator hash", field);
        }
    }

    // ---- the hash must move when the evaluator does (#379) -----------------------------------

    /// <summary>
    /// The weights themselves never entered the digest — only a caller-supplied snapshot id. A
    /// caller who refits and reuses the id, trivially one like "latest" or one derived from a date
    /// that has not rolled, hashes two materially different weightings identically, and the one
    /// refusal that exists to stop a refit reading as a candidate regression does not fire.
    /// </summary>
    [Fact]
    public void Refitting_the_weights_moves_the_hash_even_when_the_snapshot_id_does_not()
    {
        var before = Build(
            reliabilitySnapshotId: "latest",
            reliabilityWeights: new Dictionary<string, double> { ["j-a"] = 0.9 }
        );
        var after = Build(
            reliabilitySnapshotId: "latest",
            reliabilityWeights: new Dictionary<string, double> { ["j-a"] = 0.4 }
        );

        after.Hash.Should().NotBe(before.Hash);
    }

    /// <summary>The same weights in a different enumeration order are the same weights.</summary>
    [Fact]
    public void The_declared_weights_hash_by_content_and_not_by_enumeration_order()
    {
        var forward = Build(
            reliabilityWeights: new Dictionary<string, double> { ["j-a"] = 0.9, ["j-b"] = 0.4 }
        );
        var reverse = Build(
            reliabilityWeights: new Dictionary<string, double> { ["j-b"] = 0.4, ["j-a"] = 0.9 }
        );

        forward.Hash.Should().Be(reverse.Hash);
    }

    /// <summary>
    /// The weights are validated where the configuration is frozen, not once per corpus item: a
    /// misfitted refit is a fact about the run, and left to the per-item path it came back as a
    /// corpus that scored nothing.
    /// </summary>
    [Fact]
    public void A_weight_off_its_scale_is_refused_when_the_configuration_is_frozen()
    {
        var act = () =>
            Build(reliabilityWeights: new Dictionary<string, double> { ["j-a"] = 1.5 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Two judges over agents at different temperatures — or different sampling settings, or a
    /// different deployment behind the same ModelId — produce different ballots and used to produce
    /// the same fingerprint, because the transport is an opaque delegate holding no field the judge
    /// could report.
    /// </summary>
    [Fact]
    public void Two_judges_whose_transports_are_configured_differently_do_not_hash_the_same()
    {
        static RubricJudge Judge(string transport) =>
            new(
                new RubricJudgeOptions
                {
                    JudgeId = "j-1",
                    ModelId = "m",
                    ModelFamily = "anthropic",
                    TransportFingerprint = transport,
                },
                (_, _) => Task.FromResult("SCORE: 8")
            );

        Build([], [Judge("temperature=1.0")])
            .Hash.Should()
            .NotBe(Build([], [Judge("temperature=0.0")]).Hash);
    }

    /// <summary>
    /// And a judge that cannot describe its transport at all is refused rather than hashed under a
    /// constant, for the same reason an undeclared custom renderer is.
    /// </summary>
    [Fact]
    public void A_judge_that_cannot_describe_its_transport_is_refused()
    {
        var judge = new RubricJudge(
            new RubricJudgeOptions
            {
                JudgeId = "j-1",
                ModelId = "m",
                ModelFamily = "anthropic",
            },
            (_, _) => Task.FromResult("SCORE: 8")
        );

        var act = () => Build([], [judge]);

        act.Should().Throw<ArgumentException>().WithMessage("*null configuration fingerprint*");
    }

    /// <summary>
    /// The unit separator stops a value forging a FIELD boundary. Records are newline-delimited and
    /// nothing was escaped, so a judge id or a host-supplied fingerprint carrying a newline could
    /// still forge a record boundary and make two different configurations hash the same. Refused
    /// at the point the configuration is frozen, which is the only place that can see it.
    /// </summary>
    [Theory]
    [InlineData("j\na")]
    [InlineData("j\u001fa")]
    public void An_id_that_could_forge_a_hash_boundary_is_refused(string forged)
    {
        var act = () => Build([], [new ScoringJudge(forged, "anthropic", _ => 8.0)]);

        act.Should().Throw<ArgumentException>().WithMessage("*boundary*");
    }

    [Theory]
    [InlineData("v\n1")]
    [InlineData("v\u001f1")]
    public void A_fingerprint_that_could_forge_a_hash_boundary_is_refused(string forged)
    {
        var act = () =>
            Build([], [new ScoringJudge("j-a", "anthropic", _ => 8.0, fingerprint: forged)]);

        act.Should().Throw<ArgumentException>().WithMessage("*boundary*");
    }
}
