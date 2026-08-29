using AchieveAi.LmDotnetTools.LmEval.Corpus;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;
using FluentAssertions.Execution;

namespace AchieveAi.LmDotnetTools.LmEval.Tests;

/// <summary>
/// The snapshot hash is what a comparison refusal is built on, so these tests pin both halves of
/// its contract: it must be blind to things that are not the corpus, and it must move for anything
/// that is.
/// </summary>
public class CorpusSnapshotTests
{
    [Fact]
    public void The_same_items_in_a_different_row_order_are_the_same_corpus()
    {
        var a = EvalFixtures.Item("a", "review A");
        var b = EvalFixtures.Item("b", "review B");

        CorpusSnapshot
            .Create("corpus-1", [a, b])
            .SnapshotHash.Should()
            .Be(CorpusSnapshot.Create("corpus-1", [b, a]).SnapshotHash);
    }

    [Fact]
    public void Changing_one_item_s_content_changes_the_hash()
    {
        var original = CorpusSnapshot.Create(
            "corpus-1",
            [EvalFixtures.Item("a", "review A"), EvalFixtures.Item("b", "review B")]
        );

        var edited = CorpusSnapshot.Create(
            "corpus-1",
            [EvalFixtures.Item("a", "review A"), EvalFixtures.Item("b", "review B EDITED")]
        );

        edited.SnapshotHash.Should().NotBe(original.SnapshotHash);
    }

    [Fact]
    public void Changing_the_task_input_changes_the_hash()
    {
        var original = EvalFixtures.Snapshot(EvalFixtures.Item("a"));
        var rephrased = CorpusSnapshot.Create(
            "corpus-1",
            [EvalFixtures.Item("a") with { TaskInput = "a different diff" }]
        );

        rephrased.SnapshotHash.Should().NotBe(original.SnapshotHash);
    }

    [Fact]
    public void Metadata_does_not_move_the_hash_because_it_cannot_move_a_score()
    {
        var plain = EvalFixtures.Snapshot(EvalFixtures.Item("a"));
        var annotated = CorpusSnapshot.Create(
            "corpus-1",
            [EvalFixtures.Item("a") with { Metadata = new Dictionary<string, string> { ["prId"] = "42" } }]
        );

        annotated.SnapshotHash.Should().Be(plain.SnapshotHash);
    }

    [Fact]
    public void A_different_corpus_id_over_identical_items_is_a_different_corpus()
    {
        CorpusSnapshot
            .Create("corpus-1", [EvalFixtures.Item("a")])
            .SnapshotHash.Should()
            .NotBe(CorpusSnapshot.Create("corpus-2", [EvalFixtures.Item("a")]).SnapshotHash);
    }

    [Fact]
    public void A_repeated_candidate_id_is_refused()
    {
        var act = () =>
            CorpusSnapshot.Create("corpus-1", [EvalFixtures.Item("a", "one"), EvalFixtures.Item("a", "two")]);

        act.Should().Throw<ArgumentException>().WithMessage("*repeats candidate id 'a'*");
    }

    [Fact]
    public void Mixing_task_types_is_refused()
    {
        var act = () =>
            CorpusSnapshot.Create(
                "corpus-1",
                [EvalFixtures.Item("a"), EvalFixtures.Item("b") with { TaskType = "summarization" }]
            );

        act.Should().Throw<ArgumentException>().WithMessage("*mixes task types*");
    }

    [Fact]
    public void An_empty_corpus_is_refused_because_its_denominator_is_undefined()
    {
        var act = () => CorpusSnapshot.Create("corpus-1", []);

        act.Should().Throw<ArgumentException>().WithMessage("*at least one item*");
    }

    [Fact]
    public void Size_is_the_item_count()
    {
        EvalFixtures
            .Snapshot(EvalFixtures.Item("a"), EvalFixtures.Item("b"), EvalFixtures.Item("c"))
            .Size.Should()
            .Be(3);
    }

    [Fact]
    public void Every_hashed_candidate_field_moves_the_hash_on_its_own()
    {
        // One row per field ComputeHash appends, each moving EXACTLY that field. Wholesale edits
        // elsewhere in this suite do not pin the individual appends: VariantId, GeneratorFamily and
        // Reference can each be replaced with string.Empty in the builder and every other test here
        // stays green. Reference and VariantId are otherwise never even set by a test in this
        // assembly, so without this table they are structurally unexercised.
        var baseItem = new Candidate
        {
            CandidateId = "a",
            TaskType = HarnessFixtures.TaskType,
            TaskInput = "Grade this code review:",
            Content = "a review",
            VariantId = "arm-a",
            ModelId = "vendor/m1",
            GeneratorFamily = "meta",
            Reference = "the recorded gold answer",
        };

        (string Field, Candidate Moved)[] rows =
        [
            ("VariantId", baseItem with { VariantId = "arm-b" }),
            ("ModelId", baseItem with { ModelId = "vendor/m2" }),
            ("GeneratorFamily", baseItem with { GeneratorFamily = "openai" }),
            ("Reference", baseItem with { Reference = "a different gold answer" }),
            ("TaskInput", baseItem with { TaskInput = "Grade this other code review:" }),
            ("Content", baseItem with { Content = "a different review" }),
        ];

        var baseHash = CorpusSnapshot.Create("corpus-1", [baseItem]).SnapshotHash;

        using var scope = new AssertionScope();
        foreach (var (field, moved) in rows)
        {
            CorpusSnapshot
                .Create("corpus-1", [moved])
                .SnapshotHash.Should()
                .NotBe(baseHash, "moving only {0} must move the corpus hash", field);
        }
    }

    /// <summary>
    /// The corpus digest concatenates unescaped candidate fields, separates them with U+001F and
    /// terminates each record with a newline. The separator stops a value forging a FIELD boundary;
    /// nothing stopped one forging a RECORD boundary, so a candidate whose last field carries a
    /// newline and a hand-built second record hashes exactly as a two-item corpus does — and the
    /// refusal that keeps a comparison over two different corpora from happening never fires.
    /// <para>
    /// Rejecting newlines is not available here: a candidate's task input and content legitimately
    /// contain them. Each field is length-prefixed instead, so its extent is stated rather than
    /// inferred from a delimiter its own content can supply.
    /// </para>
    /// </summary>
    [Fact]
    public void A_candidate_field_cannot_forge_a_second_record()
    {
        const char Sep = '\u001f';

        var honest = CorpusSnapshot.Create(
            "corpus-1",
            [
                new Candidate
                {
                    CandidateId = "c1",
                    TaskType = "code-review",
                    TaskInput = "i1",
                    Content = "x1",
                },
                new Candidate
                {
                    CandidateId = "c2",
                    TaskType = "code-review",
                    TaskInput = "i2",
                    Content = "x2",
                },
            ]
        );

        var forged = CorpusSnapshot.Create(
            "corpus-1",
            [
                new Candidate
                {
                    CandidateId = "c1",
                    TaskType = "code-review",
                    TaskInput = "i1",
                    Content = "x1",
                    Reference = $"\nc2{Sep}code-review{Sep}{Sep}{Sep}{Sep}i2{Sep}x2{Sep}",
                },
            ]
        );

        forged.Size.Should().Be(1, "the forgery is one item claiming to be two");
        forged.SnapshotHash.Should().NotBe(honest.SnapshotHash);
    }
}
