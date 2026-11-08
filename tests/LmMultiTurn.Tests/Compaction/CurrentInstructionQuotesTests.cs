using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// The bound on <c>CurrentInstruction</c> (spec 679 §2.3): rows that fit half the envelope cap are quoted whole; past it
/// the largest rows become a deterministic head and tail around a marker that names the seq to recall.
/// </summary>
public sealed class CurrentInstructionQuotesTests
{
    private static readonly Func<string?, long> Estimate = CompactionTokenEstimate.EstimateText;

    [Fact]
    public void RowsWithinTheBudget_AreQuotedWhole()
    {
        var thread = new ThreadFixture().Human("fix the flaky test").ToolTurns(1).Human("and the other one");

        var quotes = CurrentInstructionQuotes.Quote([thread.Rows[0], thread.Rows[3]], budgetTokens: 100, Estimate);

        quotes.Select(q => (q.Seq, q.Quote)).Should().Equal((1L, "fix the flaky test"), (4L, "and the other one"));
    }

    [Fact]
    public void OverTheBudget_TheLargestRowIsTrimmedFirst_ToAHeadAndTailWithinTheBudget_AndSmallRowsStayWhole()
    {
        var big = "HEAD" + new string('b', 20_000) + "TAIL";
        var thread = new ThreadFixture().Human("short one").ToolTurns(1).Human(big);

        var quotes = CurrentInstructionQuotes.Quote([thread.Rows[0], thread.Rows[3]], budgetTokens: 1_000, Estimate);

        quotes[0].Quote.Should().Be("short one");
        quotes[1].Quote.Should().StartWith("HEAD").And.EndWith("TAIL");
        quotes[1].Quote.Should().MatchRegex(@"\n\[… \d+ chars omitted; full text: RecallConversation seq 4\]\n");
        quotes.Sum(q => Estimate(q.Quote)).Should().BeLessThanOrEqualTo(1_000);
        var head = quotes[1].Quote.IndexOf("\n[…", StringComparison.Ordinal);
        var tail = quotes[1].Quote.Length - quotes[1].Quote.IndexOf("]\n", StringComparison.Ordinal) - 2;
        ((double)head / (head + tail)).Should().BeApproximately(0.7, 0.01, "a 70/30 split, like a trimmed tool result");
    }

    [Fact]
    public void Shrink_OfAQuote_KeepsAHeadAndTail_AndShrinkingATrimAgain_KeepsOneMarkerWithTheExactCount()
    {
        var text = "HEAD" + new string('b', 20_000) + "TAIL";
        var whole = new QuotedItem { Seq = 7, Quote = text };

        var once = CurrentInstructionQuotes.Shrink(whole, 2_000);
        var twice = CurrentInstructionQuotes.Shrink(once, 500);

        CurrentInstructionQuotes.VisibleChars(once).Should().Be(2_000);
        CurrentInstructionQuotes.VisibleChars(twice).Should().Be(500);
        twice.Quote.Should().StartWith("HEAD").And.EndWith("TAIL");
        System
            .Text.RegularExpressions.Regex.Matches(
                twice.Quote,
                @"\n\[… (\d+) chars omitted; full text: RecallConversation seq 7\]\n"
            )
            .Should()
            .ContainSingle()
            .Which.Groups[1]
            .Value.Should()
            .Be((text.Length - 500).ToString(System.Globalization.CultureInfo.InvariantCulture));
        CurrentInstructionQuotes.IsTrimOf(twice.Quote, 7, text).Should().BeTrue();
        CurrentInstructionQuotes.Shrink(twice, 5_000).Should().Be(twice, "a shrink never grows a quote");
        CurrentInstructionQuotes.Shrink(new QuotedItem { Seq = 7, Quote = "short" }, 500).Quote.Should().Be("short");
    }

    [Fact]
    public void Shrink_OfAWholeRowWhoseOwnTextHoldsAMarkerForItsSeq_IsAFreshTrim_NotARecountOfThatMarker()
    {
        // A row can quote a marker naming its own seq (a pasted checkpoint excerpt). Read as already trimmed, the shrink
        // added the pasted count to what it cut, and V3 rejected the whole checkpoint.
        var row =
            "HEAD"
            + new string('a', 3_000)
            + "\n[… 50 chars omitted; full text: RecallConversation seq 7]\n"
            + new string('b', 3_000)
            + "TAIL";
        var whole = new QuotedItem { Seq = 7, Quote = row };

        var shrunk = CurrentInstructionQuotes.Shrink(whole, 1_000, row);

        CurrentInstructionQuotes.VisibleChars(whole, row).Should().Be(row.Length, "a verbatim quote is not a trim");
        CurrentInstructionQuotes.VisibleChars(shrunk, row).Should().Be(1_000);
        CurrentInstructionQuotes.IsTrimOf(shrunk.Quote, 7, row).Should().BeTrue();
        shrunk.Quote.Should().Contain($"[… {row.Length - 1_000} chars omitted; full text: RecallConversation seq 7]");
    }

    [Fact]
    public void TheSameRowsAndBudget_AlwaysGiveTheSameQuotes()
    {
        var thread = new ThreadFixture().Human(new string('a', 9_000)).ToolTurns(1).Human(new string('c', 9_000));
        var rows = new[] { thread.Rows[0], thread.Rows[3] };

        CurrentInstructionQuotes
            .Quote(rows, 1_500, Estimate)
            .Should()
            .Equal(CurrentInstructionQuotes.Quote(rows, 1_500, Estimate));
    }

    private static IReadOnlyList<SequencedMessage> LongRows(int count)
    {
        var thread = new ThreadFixture();
        foreach (var n in Enumerable.Range(1, count))
        {
            _ = thread.Human($"ROW-{n} ".PadRight(2_000, 'x'));
        }

        return thread.Rows;
    }

    private static IReadOnlyList<SequencedMessage> FiveLongRows() => LongRows(5);

    [Fact]
    public void CollapseOlder_FoldsTheFewestOldestRowsThatFit_AndOneFewerWouldBeOverTheBudget()
    {
        const long budget = 200;
        var rows = LongRows(10);

        var quotes = CurrentInstructionQuotes.Quote(rows, budget, Estimate, collapseOlder: true);

        var range = CurrentInstructionQuotes.CollapsedRange(quotes[0]);
        range.Should().NotBeNull();
        var folded = rows.Count(r => r.Seq >= range!.Value.FromSeq && r.Seq <= range.Value.ToSeq);
        folded.Should().BeInRange(3, 8, "this budget needs some rows folded, and not every older one");
        quotes.Should().HaveCount(rows.Count - folded + 1);
        quotes.Sum(q => Estimate(q.Quote)).Should().BeLessThanOrEqualTo(budget);

        var last = rows[folded - 1];
        var oneFewer =
            quotes.Skip(1).Sum(q => Estimate(q.Quote))
            + Estimate(CurrentInstructionQuotes.Collapse(folded - 1, rows[0].Seq, rows[folded - 2].Seq))
            + Estimate(CurrentInstructionQuotes.Bound(last.Text!, last.Seq, 0));
        oneFewer.Should().BeGreaterThan(budget, "folding one row fewer must not have fit");
    }

    [Fact]
    public void CollapseOlder_WhenTheOlderRowsFitAsMarkers_LeavesEachAsItsOwnMarker_WithNoCountedRange()
    {
        var rows = FiveLongRows();

        var quotes = CurrentInstructionQuotes.Quote(rows, budgetTokens: 200, Estimate, collapseOlder: true);

        quotes.Select(q => q.Seq).Should().Equal(rows.Select(r => r.Seq));
        quotes.Should().OnlyContain(q => CurrentInstructionQuotes.CollapsedRange(q) == null);
        foreach (var older in quotes.SkipLast(1))
        {
            older.Quote.Trim().Should().Be($"[… 2000 chars omitted; full text: RecallConversation seq {older.Seq}]");
        }

        CurrentInstructionQuotes.IsTrimOf(quotes[^1].Quote, rows[^1].Seq, rows[^1].Text!).Should().BeTrue();
        quotes[^1].Quote.Should().StartWith("ROW-5 ", "the newest row keeps its own quote");
        quotes.Sum(q => Estimate(q.Quote)).Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public void CollapseOlder_WhenNoCollapseFits_FoldsEveryOlderRowIntoOneRange_AndTheNewestKeepsItsQuote()
    {
        var rows = FiveLongRows();

        var quotes = CurrentInstructionQuotes.Quote(rows, budgetTokens: 1, Estimate, collapseOlder: true);

        quotes.Should().HaveCount(2);
        CurrentInstructionQuotes.CollapsedRange(quotes[0]).Should().Be((rows[0].Seq, rows[3].Seq));
        quotes[0].Quote.Should().StartWith("[… 4 earlier rows of this instruction omitted");
        quotes[1].Seq.Should().Be(rows[4].Seq);
        quotes[1].Quote.Should().StartWith("ROW-5 ");
        quotes.Sum(q => Estimate(q.Quote)).Should().BeGreaterThan(1, "the result stands even over the budget");
    }
}
