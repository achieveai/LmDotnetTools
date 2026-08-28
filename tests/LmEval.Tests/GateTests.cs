using AchieveAi.LmDotnetTools.LmEval.Gates;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The three starter gates (P6 spec §4.2). Every one of them is deterministic and free, which is
/// the whole reason they run before any model does.
/// </summary>
public sealed class GateTests
{
    private static async Task<GateDecision> Evaluate(IGate gate, Candidate candidate) =>
        await gate.EvaluateAsync(candidate, CancellationToken.None);

    // ---- LengthBoundsGate (§3.3 control 1) ---------------------------------------------------

    /// <summary>
    /// §3.3 control 1 — a repetitive-list attack fooled two of three frontier judges 91% of the
    /// time. A length band settles the extreme cases without asking a model anything.
    /// </summary>
    [Fact]
    public async Task LengthBoundsGate_rejects_a_candidate_longer_than_the_band()
    {
        var gate = new LengthBoundsGate(minimumLength: 10, maximumLength: 20);

        var decision = await Evaluate(gate, HarnessFixtures.Candidate(content: new string('x', 21)));

        decision.Outcome.Should().Be(GateOutcome.Reject);
        decision.Reason.Should().Contain("21").And.Contain("20");
    }

    [Fact]
    public async Task LengthBoundsGate_rejects_a_candidate_shorter_than_the_band()
    {
        var gate = new LengthBoundsGate(minimumLength: 10, maximumLength: 20);

        var decision = await Evaluate(gate, HarnessFixtures.Candidate(content: "short"));

        decision.Outcome.Should().Be(GateOutcome.Reject);
    }

    [Fact]
    public async Task LengthBoundsGate_passes_a_candidate_inside_the_band_including_its_edges()
    {
        var gate = new LengthBoundsGate(minimumLength: 10, maximumLength: 20);

        (await Evaluate(gate, HarnessFixtures.Candidate(content: new string('x', 10)))).IsPass.Should().BeTrue();
        (await Evaluate(gate, HarnessFixtures.Candidate(content: new string('x', 20)))).IsPass.Should().BeTrue();
    }

    [Fact]
    public void LengthBoundsGate_refuses_an_inverted_band()
    {
        var construct = () => new LengthBoundsGate(minimumLength: 30, maximumLength: 20);

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>The reason reaches persistence, so it carries lengths, never the candidate itself.</summary>
    [Fact]
    public async Task LengthBoundsGate_never_quotes_the_candidate_in_its_reason()
    {
        var gate = new LengthBoundsGate(minimumLength: 1000, maximumLength: 2000);

        var decision = await Evaluate(gate, HarnessFixtures.Candidate(content: "a secret token: sk-abcdef"));

        decision.Reason.Should().NotContain("sk-abcdef");
    }

    // ---- JsonSchemaGate -----------------------------------------------------------------------

    [Fact]
    public async Task JsonSchemaGate_passes_a_candidate_whose_content_is_a_json_object()
    {
        var decision = await Evaluate(
            new JsonSchemaGate(),
            HarnessFixtures.Candidate(content: """{"finding":"one"}""")
        );

        decision.IsPass.Should().BeTrue();
    }

    [Fact]
    public async Task JsonSchemaGate_rejects_content_that_is_not_json()
    {
        var decision = await Evaluate(
            new JsonSchemaGate(),
            HarnessFixtures.Candidate(content: "## Review\nLooks fine.")
        );

        decision.Outcome.Should().Be(GateOutcome.Reject);
    }

    [Fact]
    public async Task JsonSchemaGate_rejects_content_missing_a_required_property()
    {
        var gate = new JsonSchemaGate(requiredProperties: ["findings", "summary"]);

        var decision = await Evaluate(gate, HarnessFixtures.Candidate(content: """{"findings":[]}"""));

        decision.Outcome.Should().Be(GateOutcome.Reject);
        decision.Reason.Should().Contain("summary");
    }

    [Fact]
    public async Task JsonSchemaGate_passes_content_carrying_every_required_property()
    {
        var gate = new JsonSchemaGate(requiredProperties: ["findings", "summary"]);

        var decision = await Evaluate(gate, HarnessFixtures.Candidate(content: """{"findings":[],"summary":"none"}"""));

        decision.IsPass.Should().BeTrue();
    }

    // ---- RequiredAnchorGate -------------------------------------------------------------------

    /// <summary>
    /// §4.3(2) — the shallow version of a per-finding check: citations present and well-formed. A
    /// real finding parser is #320's, where the eval runner needs one anyway.
    /// </summary>
    [Fact]
    public async Task RequiredAnchorGate_passes_content_citing_a_file_and_line()
    {
        var decision = await Evaluate(
            new RequiredAnchorGate(),
            HarnessFixtures.Candidate(content: "The null deref is at src/Foo/Bar.cs:42.")
        );

        decision.IsPass.Should().BeTrue();
    }

    [Fact]
    public async Task RequiredAnchorGate_rejects_content_with_no_citation_at_all()
    {
        var decision = await Evaluate(
            new RequiredAnchorGate(),
            HarnessFixtures.Candidate(content: "This review is thorough but cites nothing.")
        );

        decision.Outcome.Should().Be(GateOutcome.Reject);
    }

    [Fact]
    public async Task RequiredAnchorGate_rejects_a_file_named_without_a_line()
    {
        var decision = await Evaluate(
            new RequiredAnchorGate(),
            HarnessFixtures.Candidate(content: "Something is wrong in src/Foo/Bar.cs somewhere.")
        );

        decision.Outcome.Should().Be(GateOutcome.Reject);
    }

    [Fact]
    public async Task RequiredAnchorGate_can_require_more_than_one_citation()
    {
        var gate = new RequiredAnchorGate(minimumAnchors: 2);

        var one = await Evaluate(gate, HarnessFixtures.Candidate(content: "see a/b.cs:1"));
        var two = await Evaluate(gate, HarnessFixtures.Candidate(content: "see a/b.cs:1 and c/d.cs:9"));

        one.Outcome.Should().Be(GateOutcome.Reject);
        two.IsPass.Should().BeTrue();
    }

    [Fact]
    public async Task RequiredAnchorGate_never_quotes_the_candidate_in_its_reason()
    {
        var decision = await Evaluate(
            new RequiredAnchorGate(),
            HarnessFixtures.Candidate(content: "a secret token: sk-abcdef")
        );

        decision.Reason.Should().NotContain("sk-abcdef");
    }

    /// <summary>
    /// §3.3 — the gate is the structural half of the anti-verbosity rule, and a repetitive-list
    /// attack that restates ONE finding N times is precisely the shape it must not credit. The
    /// parameter is documented as "how many DISTINCT citations", so occurrence counting is the bug.
    /// </summary>
    [Fact]
    public async Task RequiredAnchorGate_does_not_credit_one_citation_repeated()
    {
        var gate = new RequiredAnchorGate(minimumAnchors: 3);

        var decision = await Evaluate(
            gate,
            HarnessFixtures.Candidate(content: "src/a.cs:1 bad. src/a.cs:1 bad. src/a.cs:1 bad.")
        );

        decision.Outcome.Should().Be(GateOutcome.Reject);
    }

    /// <summary>
    /// Distinctness is ORDINAL: a path that differs only in case is a different citation on a
    /// case-sensitive filesystem, and collapsing the two would let a repetition attack pass by
    /// re-casing.
    /// </summary>
    [Fact]
    public async Task RequiredAnchorGate_counts_case_differing_paths_as_distinct_citations()
    {
        var gate = new RequiredAnchorGate(minimumAnchors: 2);

        var decision = await Evaluate(gate, HarnessFixtures.Candidate(content: "src/a.cs:1 and src/A.cs:1"));

        decision.IsPass.Should().BeTrue();
    }

    [Fact]
    public async Task RequiredAnchorGate_still_counts_genuinely_distinct_citations()
    {
        var gate = new RequiredAnchorGate(minimumAnchors: 2);

        var decision = await Evaluate(
            gate,
            HarnessFixtures.Candidate(content: "src/a.cs:1 and src/a.cs:2 and src/a.cs:1")
        );

        decision.IsPass.Should().BeTrue();
    }

    // ---- applicability ------------------------------------------------------------------------

    [Fact]
    public void A_gate_with_no_declared_task_types_applies_to_all_of_them()
    {
        new LengthBoundsGate(1, 2).AppliesTo.Should().BeEmpty();
        new JsonSchemaGate().AppliesTo.Should().BeEmpty();
        new RequiredAnchorGate().AppliesTo.Should().BeEmpty();
    }

    [Fact]
    public void A_gate_can_declare_the_task_types_it_applies_to()
    {
        new LengthBoundsGate(1, 2, appliesTo: ["code-review"]).AppliesTo.Should().BeEquivalentTo(["code-review"]);
    }
}
