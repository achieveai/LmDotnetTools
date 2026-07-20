using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

public class RevisionWatermarkTests
{
    [Fact]
    public void Prefix_AdvancesContiguously_AsRevisionsCommit()
    {
        var w = new RevisionWatermark();
        var r1 = w.Allocate();
        var r2 = w.Allocate();
        var r3 = w.Allocate();

        w.Prefix.Should().Be(0);
        w.Commit(r1);
        w.Prefix.Should().Be(1);
        w.Commit(r2);
        w.Commit(r3);
        w.Prefix.Should().Be(3);
    }

    [Fact]
    public void Prefix_DoesNotAdvancePastGap_UntilEarlierRevisionCommitted()
    {
        // Revobot's required adversarial case: revision 2 becomes visible while revision 1 is delayed.
        var w = new RevisionWatermark();
        var r1 = w.Allocate();
        var r2 = w.Allocate();

        w.Commit(r2); // rev 2 committed, rev 1 still pending
        w.Prefix.Should().Be(0, "publishing N=2 without revision 1 would drop a record");

        w.Commit(r1); // gap fills
        w.Prefix.Should().Be(2);
    }

    [Fact]
    public void Commit_IsIdempotent_AndSafeOutOfOrder()
    {
        var w = new RevisionWatermark();
        var r1 = w.Allocate();
        var r2 = w.Allocate();

        w.Commit(r2);
        w.Commit(r2); // duplicate commit
        w.Prefix.Should().Be(0);

        w.Commit(r1);
        w.Commit(r1); // duplicate below prefix
        w.Prefix.Should().Be(2);
    }
}
