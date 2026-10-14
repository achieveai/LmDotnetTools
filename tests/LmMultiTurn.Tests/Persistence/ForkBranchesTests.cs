using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins the branch switcher: which points along the viewed conversation's history split, which
/// continuation each option opens, and which conversations may be offered.
/// </summary>
public sealed class ForkBranchesTests
{
    // Original A: rows 1..4. F forked after row 2 (own rows 3). F2 forked after row 2, still empty.
    private static readonly BranchNode A = new("A", null, 4, true);
    private static readonly BranchNode F = new("F", At("A", "a2", 2), 3, true);
    private static readonly BranchNode F2 = new("F2", At("A", "a2", 2), 2, true);

    [Fact]
    public void EveryMemberOfAFamily_SeesTheSamePoint_WithItsOwnContinuationCurrent()
    {
        var family = new[] { A, F, F2 };

        Describe(ForkBranches.Compute("A", family)).Should().Equal("a2@2: A* F F2");
        Describe(ForkBranches.Compute("F", family)).Should().Equal("a2@2: A F* F2");
        Describe(ForkBranches.Compute("F2", family))
            .Should()
            .Equal(["a2@2: A F F2*"], "an empty fork is still a continuation to switch to");
    }

    [Fact]
    public void AForkOfAFork_SplitsAtBothPoints_AndItsOwnPathOpensItself()
    {
        // G forked from F after F's own row 3; H forked from F inside the shared history (row 1), so it
        // belongs to A's point at row 1, not to a point in F.
        var g = new BranchNode("G", At("F", "f3", 3), 4, true);
        var h = new BranchNode("H", At("F", "a1", 1), 1, true);
        var family = new[] { A, F with { Watermark = 5 }, g, h };

        Describe(ForkBranches.Compute("G", family)).Should().Equal("a1@1: G* H", "a2@2: A G*", "f3@3: F G*");
        Describe(ForkBranches.Compute("A", family)).Should().Equal("a1@1: A* H", "a2@2: A* F");
    }

    [Fact]
    public void Hidden_Or_Unreadable_Conversations_AreNotOffered_UnlessTheViewerIsOnThem()
    {
        var family = new[] { A with { Visible = false }, F, F2 with { Visible = false } };

        Describe(ForkBranches.Compute("F", family))
            .Should()
            .BeEmpty("with the deleted original and the unreadable sibling gone, nothing is left to switch to");

        Describe(ForkBranches.Compute("F", [A with { Visible = false }, F, F2])).Should().Equal("a2@2: F* F2");
    }

    [Fact]
    public void AnOriginalWithNothingAfterThePoint_IsStillOfferedToGoBackTo()
    {
        // Forking the last reply is the common case; the switcher must lead back at once, just as an
        // empty fork is offered before it has rows of its own.
        var family = new[] { A with { Watermark = 2 }, F };

        Describe(ForkBranches.Compute("F", family)).Should().Equal("a2@2: A F*");
        Describe(ForkBranches.Compute("A", family)).Should().Equal("a2@2: A* F");
        ForkBranches.Compute("unknown", family).Should().BeEmpty();
    }

    [Fact]
    public void AForkOfAFork_TakenInsideSharedHistory_SplitsFromTheRowsOwner_WithEachConversationOnce()
    {
        // Found by hand: F forked after A's last row 4, then B forked from F after row 2 - a row F only
        // shares. B's history is A's rows 1..2 then its own; F contributes nothing, so the split is A's.
        var a = A with
        {
            Watermark = 4,
        };
        var f = new BranchNode("F", At("A", "a4", 4), 6, true);
        var b = new BranchNode("B", At("F", "a2", 2), 3, true);
        var family = new[] { a, f, b };

        Describe(ForkBranches.Compute("B", family)).Should().Equal("a2@2: A B*");
        Describe(ForkBranches.Compute("A", family)).Should().Equal("a2@2: A* B", "a4@4: A* F");
        Describe(ForkBranches.Compute("F", family)).Should().Equal("a2@2: F* B", "a4@4: A F*");
    }

    private static ForkPoint At(string threadId, string messageId, long seq) =>
        new()
        {
            ThreadId = threadId,
            MessageId = messageId,
            Seq = seq,
        };

    private static IEnumerable<string> Describe(IReadOnlyList<BranchPoint> points) =>
        points.Select(p =>
            $"{p.AfterMessageId}@{p.AfterSeq}: {string.Join(' ', p.Options.Select(o => o.ThreadId + (o.Current ? "*" : "")))}"
        );
}
