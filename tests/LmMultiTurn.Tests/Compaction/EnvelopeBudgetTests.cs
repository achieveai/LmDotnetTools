using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// The envelope budget's drop order (spec 679 §3.4) measured in characters, so which item goes is exact: the pipeline
/// tests prove the same order holds against the real renderer and V9.
/// </summary>
public sealed class EnvelopeBudgetTests
{
    private static long InstructionChars(ContextManifest manifest, string narrative) =>
        manifest.Instructions.Sum(q => (long)q.Quote.Length);

    [Fact]
    public void DropInstructions_AStandingQuoteOfACurrentInstructionRow_DropsFirst_AndIsNeverTheProtectedNewestUserInstruction()
    {
        // A model quote of the untrimmed current instruction repeats a row CurrentInstruction already carries whole. It
        // used to count as the newest user instruction, so the real earlier instruction dropped and the duplicate stayed.
        var earlier = "USER-OLD " + new string('o', 300);
        var current = "USER-CURRENT " + new string('c', 300);
        var manifest = new ContextManifest
        {
            CurrentInstruction = [new QuotedItem { Seq = 5, Quote = current }],
            Instructions = [new QuotedItem { Seq = 1, Quote = earlier }, new QuotedItem { Seq = 5, Quote = current }],
        };

        var (fitted, _, fit) = EnvelopeBudget.Fit(
            manifest,
            string.Empty,
            previous: null,
            InstructionChars,
            cap: 500,
            _ => InstructionRank.User
        );

        fit.Fits.Should().BeTrue();
        fitted.Instructions.Should().Equal(new QuotedItem { Seq = 1, Quote = earlier });
        fitted.CurrentInstruction.Should().Equal(manifest.CurrentInstruction);
        fit.DroppedInstructionSeqs.Should().ContainKey(InstructionRank.User).WhoseValue.Should().Equal(5L);
    }

    [Fact]
    public void DropInstructions_AStandingQuoteOfACurrentInstructionRow_DropsBeforeALowerRankedDirective()
    {
        // The rank order drops directives before user instructions; a repeat of a CurrentInstruction row loses nothing, so
        // it goes ahead of both.
        var directive = "STEER " + new string('s', 300);
        var current = "USER-CURRENT " + new string('c', 300);
        var manifest = new ContextManifest
        {
            CurrentInstruction = [new QuotedItem { Seq = 5, Quote = current }],
            Instructions = [new QuotedItem { Seq = 2, Quote = directive }, new QuotedItem { Seq = 5, Quote = current }],
        };

        var (fitted, _, _) = EnvelopeBudget.Fit(
            manifest,
            string.Empty,
            previous: null,
            InstructionChars,
            cap: 500,
            seq => seq == 2 ? InstructionRank.Directive : InstructionRank.User
        );

        fitted.Instructions.Should().Equal(new QuotedItem { Seq = 2, Quote = directive });
    }
}
