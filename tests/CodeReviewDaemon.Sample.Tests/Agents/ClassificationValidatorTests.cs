using CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The boundary that makes "the model classifies, the daemon counts" a property rather than a request.
/// <para>
/// Every case here is a thing a prompt could ask the model not to do and could not stop it doing. The
/// asymmetry is the point: a prompt instruction is advisory and a validator is not, and the cost of the
/// difference lands under a named person's file where a wrong number is indistinguishable from a right one.
/// </para>
/// </summary>
public sealed class ClassificationValidatorTests
{
    private static readonly IReadOnlySet<string> Refs = new HashSet<string>(StringComparer.Ordinal) { "f1", "f2" };

    private static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "missing-null-guard-on-dto",
    };

    private static string Reply(string classifications) =>
        "{\"schemaVersion\":1,\"classifications\":[" + classifications + "]}";

    /// <summary>
    /// A well-formed new-pattern proposal. Every field is overridable so a test can spoil exactly one and
    /// leave the rest valid — otherwise a rejection could be caused by any of five things at once.
    /// </summary>
    private static string NewPattern(
        string slug,
        string title = "Unbounded retry on transient failure",
        string what = "The code retries a failing call without a ceiling.",
        string why = "A dependency outage turns into a self-inflicted overload.",
        string how = "Give every retry loop a bounded attempt count and a backoff."
    ) =>
        "{\"findingRef\":\"f1\",\"isRecurringRisk\":true,\"patternId\":null,\"newPattern\":{"
        + "\"slug\":\""
        + slug
        + "\","
        + "\"title\":\""
        + title
        + "\","
        + "\"whatItIs\":\""
        + what
        + "\","
        + "\"whyItMatters\":\""
        + why
        + "\","
        + "\"howToAvoid\":\""
        + how
        + "\"}}";

    [Fact]
    public void A_known_pattern_id_is_accepted()
    {
        // Positive control. Without it, every rejection test below could pass against a validator that
        // refuses absolutely everything.
        var outcome = ClassificationValidator.Validate(
            Reply(
                """{"findingRef":"f1","isRecurringRisk":true,"patternId":"missing-null-guard-on-dto","newPattern":null}"""
            ),
            Refs,
            Known
        );

        outcome.Rejected.Should().BeFalse(outcome.RejectionReason);
        outcome.Accepted.Should().ContainSingle().Which.PatternId.Should().Be("missing-null-guard-on-dto");
    }

    [Fact]
    public void A_well_formed_new_pattern_is_accepted()
    {
        var outcome = ClassificationValidator.Validate(
            Reply(NewPattern("unbounded-retry-on-transient-failure")),
            Refs,
            Known
        );

        outcome.Rejected.Should().BeFalse(outcome.RejectionReason);
        outcome
            .Accepted.Should()
            .ContainSingle()
            .Which.NewPattern!.Slug.Should()
            .Be("unbounded-retry-on-transient-failure");
    }

    [Fact]
    public void An_unknown_pattern_id_is_rejected_and_never_auto_created()
    {
        // Silent creation from a mistyped or hallucinated id is exactly how "missing null check", "null
        // guard absent" and "no null validation" become three patterns whose counts never accumulate.
        var outcome = ClassificationValidator.Validate(
            Reply("""{"findingRef":"f1","isRecurringRisk":true,"patternId":"null-guard-absent","newPattern":null}"""),
            Refs,
            Known
        );

        outcome.Rejected.Should().BeTrue();
        outcome.RejectionReason.Should().Contain("null-guard-absent");
        outcome.Accepted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../../.git/hooks/x")]
    [InlineData("../escape")]
    [InlineData("Has-Capitals")]
    [InlineData("has spaces")]
    [InlineData("ab")]
    [InlineData("-leading-hyphen")]
    [InlineData("dot.separated")]
    public void A_slug_outside_the_allowed_shape_is_rejected(string slug)
    {
        // The slug becomes patterns/{slug}.md. This is an allowlist, not traversal filtering: `..` is
        // unconstructible under [a-z0-9-] rather than stripped, so there is no escaping encoding to find.
        var outcome = ClassificationValidator.Validate(Reply(NewPattern(slug)), Refs, Known);

        outcome.Rejected.Should().BeTrue();
        outcome.RejectionReason.Should().Contain("legal pattern slug");
    }

    [Theory]
    [InlineData("This has happened 14 times before")]
    [InlineData("First seen 2026-08-09 in this repo")]
    [InlineData("The pattern is currently Resolved")]
    [InlineData("Three occurrences across the window")]
    [InlineData("It has a clean streak now")]
    public void A_reply_stating_a_fact_the_daemon_owns_is_rejected_whole(string prose)
    {
        // Whole-reply rejection, not per-item. A model that authored a count has misunderstood its
        // contract; the classifications it produced in the same breath are not more trustworthy for
        // happening to parse, and salvaging them makes one misunderstanding a permanent corruption.
        var outcome = ClassificationValidator.Validate(
            Reply(NewPattern("unbounded-retry-on-transient-failure", what: prose)),
            Refs,
            Known
        );

        outcome.Rejected.Should().BeTrue();
        outcome.RejectionReason.Should().Contain("a fact the daemon owns");
        outcome.Accepted.Should().BeEmpty();
    }

    [Fact]
    public void A_finding_that_is_not_a_recurring_risk_is_recorded_and_produces_no_pattern()
    {
        // Not every finding is a learning. One-off and environmental findings must be droppable or the
        // ledger fills with noise that dilutes every rate in it.
        var outcome = ClassificationValidator.Validate(
            Reply("""{"findingRef":"f1","isRecurringRisk":false,"patternId":null,"newPattern":null}"""),
            Refs,
            Known
        );

        outcome.Rejected.Should().BeFalse(outcome.RejectionReason);
        var only = outcome.Accepted.Should().ContainSingle().Subject;
        only.IsRecurringRisk.Should().BeFalse();
        only.PatternId.Should().BeNull();
        only.NewPattern.Should().BeNull();
    }

    [Fact]
    public void Naming_both_routes_or_neither_is_rejected()
    {
        ClassificationValidator
            .Validate(
                Reply(
                    """{"findingRef":"f1","isRecurringRisk":true,"patternId":"missing-null-guard-on-dto","newPattern":{"slug":"unbounded-retry-on-transient-failure","title":"t","whatItIs":"a","whyItMatters":"b","howToAvoid":"c"}}"""
                ),
                Refs,
                Known
            )
            .Rejected.Should()
            .BeTrue();

        ClassificationValidator
            .Validate(
                Reply("""{"findingRef":"f1","isRecurringRisk":true,"patternId":null,"newPattern":null}"""),
                Refs,
                Known
            )
            .Rejected.Should()
            .BeTrue();
    }

    [Fact]
    public void A_finding_ref_this_PR_never_handed_out_is_rejected()
    {
        // The refs are opaque ids the daemon minted for this PR. Anything else is a classification of
        // something that is not in front of us, and attaching it would attribute another PR's finding here.
        ClassificationValidator
            .Validate(
                Reply(
                    """{"findingRef":"f99","isRecurringRisk":true,"patternId":"missing-null-guard-on-dto","newPattern":null}"""
                ),
                Refs,
                Known
            )
            .Rejected.Should()
            .BeTrue();
    }

    [Fact]
    public void A_new_pattern_colliding_with_an_existing_one_is_rejected()
    {
        // Proposing a slug that already exists would silently re-author prose the spec says is written
        // once and never revised in v1.
        ClassificationValidator
            .Validate(Reply(NewPattern("missing-null-guard-on-dto")), Refs, Known)
            .Rejected.Should()
            .BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"schemaVersion":2,"classifications":[]}""")]
    [InlineData("""{"schemaVersion":1}""")]
    public void A_malformed_or_wrong_version_reply_is_rejected_with_a_stated_reason(string json)
    {
        var outcome = ClassificationValidator.Validate(json, Refs, Known);

        outcome.Rejected.Should().BeTrue();
        outcome
            .RejectionReason.Should()
            .NotBeNullOrWhiteSpace("a refusal with no stated reason is indistinguishable from a crash");
    }
}
