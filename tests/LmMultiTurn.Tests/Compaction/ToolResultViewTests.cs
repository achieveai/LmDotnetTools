using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>The view-only transforms of a tool result: clear, trim, RC1 supersede and RC2 shell retention.</summary>
public sealed class ToolResultViewTests
{
    private static ToolCallResultMessage Result(string tool, string text) =>
        new()
        {
            ToolCallId = "c1",
            ToolName = tool,
            Result = text,
        };

    [Fact]
    public void Apply_ReplacesASupersededResult_WithAPlaceholderNamingTheNewestSeq()
    {
        var result = Result("Read", new string('x', 500));
        var options = new ToolResultViewOptions { SupersededBy = new Dictionary<long, long> { [3] = 9 } };

        var shown = (ToolCallResultMessage)ToolResultView.Apply(result, 3, options);

        shown
            .Result.Should()
            .Be(
                "[Tool result superseded by a newer read of the same resource at seq 9 (500 characters cleared, seq 3, tool_call_id c1). Read it with RecallConversation(seq=3) only if the older copy matters.]"
            );
        ToolResultView.Apply(result, 9, options).Should().BeSameAs(result);
    }

    [Fact]
    public void Apply_SupersededWins_OverTheRecencyPlaceholder()
    {
        var result = Result("Read", new string('x', 500));
        var options = new ToolResultViewOptions
        {
            ClearedThroughSeq = 10,
            SupersededBy = new Dictionary<long, long> { [3] = 9 },
        };

        ((ToolCallResultMessage)ToolResultView.Apply(result, 3, options))
            .Result.Should()
            .Contain("superseded by a newer read");
    }

    private static ToolResultViewOptions ClearedShell(long seq, int trimChars = 300) =>
        new()
        {
            ClearedThroughSeq = 10,
            ShellSeqs = new HashSet<long> { seq },
            ShellTrimChars = trimChars,
        };

    [Fact]
    public void Apply_TrimsAClearedShellResult_InsteadOfClearingIt()
    {
        var text = string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}\n"));
        var result = Result("Bash", text);

        var shown = ((ToolCallResultMessage)ToolResultView.Apply(result, 3, ClearedShell(3))).Result;

        shown.Length.Should().BeLessThanOrEqualTo(300);
        shown.Should().StartWith("line 0\n");
        shown.Should().EndWith("line 399\n");
        shown.Should().Contain("elided from this tool result");
    }

    [Fact]
    public void Apply_KeepsTwiceAsMuchOfAClearedShellError()
    {
        var result = Result("Bash", "Error: build failed\n" + new string('e', 2_000));

        var shown = ((ToolCallResultMessage)ToolResultView.Apply(result, 3, ClearedShell(3))).Result;

        shown.Length.Should().BeGreaterThan(300).And.BeLessThanOrEqualTo(600);
    }

    [Theory]
    [InlineData("Error: nope", true)]
    [InlineData("error: nope", true)]
    [InlineData("ok\nexit code: 1", true)]
    [InlineData("ok\nExit code 0", false)]
    [InlineData("all good", false)]
    public void IsErrorResult_SpotsAnErrorPrefixOrANonZeroExitCode(string text, bool expected) =>
        ToolResultViewOptions.IsErrorResult(text).Should().Be(expected);

    [Fact]
    public void Apply_ShellResultInsideTheKeepWindow_IsUntouched()
    {
        var result = Result("Bash", new string('x', 5_000));
        var options = ClearedShell(3) with { ClearedThroughSeq = 2 };

        ToolResultView.Apply(result, 3, options).Should().BeSameAs(result);
    }

    [Fact]
    public void Apply_ClearsANonShellResult_EvenWhenOtherSeqsAreShell()
    {
        var result = Result("Read", new string('x', 5_000));

        ((ToolCallResultMessage)ToolResultView.Apply(result, 3, ClearedShell(seq: 4)))
            .Result.Should()
            .StartWith("[Tool result cleared from the context");
    }
}
