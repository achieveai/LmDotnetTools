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
}
