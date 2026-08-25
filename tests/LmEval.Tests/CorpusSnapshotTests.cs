using AchieveAi.LmDotnetTools.LmEval.Corpus;
using AchieveAi.LmDotnetTools.LmEval.Tests.Infrastructure;

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
            [
                EvalFixtures.Item("a") with
                {
                    Metadata = new Dictionary<string, string> { ["prId"] = "42" },
                },
            ]
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
            CorpusSnapshot.Create(
                "corpus-1",
                [EvalFixtures.Item("a", "one"), EvalFixtures.Item("a", "two")]
            );

        act.Should().Throw<ArgumentException>().WithMessage("*repeats candidate id 'a'*");
    }

    [Fact]
    public void Mixing_task_types_is_refused()
    {
        var act = () =>
            CorpusSnapshot.Create(
                "corpus-1",
                [
                    EvalFixtures.Item("a"),
                    EvalFixtures.Item("b") with { TaskType = "summarization" },
                ]
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
}
