namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     A tool_result must answer a tool_use of the assistant turn right before it, exactly once, under that
///     tool_use's id. Anthropic rejects any other shape with a 400 on every later turn of the conversation.
/// </summary>
public class ToolResultPairingTests
{
    /// <summary>
    ///     The history that broke a live conversation: a generation called two tools, one result was stored
    ///     after a user text message, so the aggregate holds two calls and only the second call's result, and
    ///     the first call's result follows the text on its own. Pairing by position sent call b's result
    ///     under call a's id and dropped call b; the late result then joined the same user turn, which left
    ///     two tool_result blocks for call a.
    /// </summary>
    [Fact]
    public void FromMessages_ResultsSplitAcrossAUserMessage_SendsEachResultOnceUnderItsOwnCall()
    {
        var aggregate = new ToolsCallAggregateMessage(
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "read_a",
                        FunctionArgs = "{}",
                        ToolCallId = "toolu_a",
                    },
                    new ToolCall
                    {
                        FunctionName = "read_b",
                        FunctionArgs = "{}",
                        ToolCallId = "toolu_b",
                    },
                ],
                GenerationId = "gen1",
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("toolu_b", "result b")],
                GenerationId = "gen1",
            }
        );

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Review the change" },
            aggregate,
            new TextMessage { Role = Role.User, Text = "Five minutes have passed." },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("toolu_a", "result a")],
                GenerationId = "gen1",
            },
        };

        var request = AnthropicRequest.FromMessages(messages);

        AssertEveryToolResultAnswersThePrecedingToolUseOnce(request);
        var toolUses = request.Messages.SelectMany(m => m.Content).Where(b => b.Type == "tool_use");
        Assert.Contains(toolUses, b => b.Id == "toolu_b");
        var results = request.Messages.SelectMany(m => m.Content).Where(b => b.Type == "tool_result").ToList();
        var resultB = Assert.Single(results, b => b.ToolUseId == "toolu_b");
        Assert.Equal("result b", resultB.Content);
        Assert.DoesNotContain(results, b => b.ToolUseId == "toolu_a" && b.Content == "result b");
    }

    /// <summary>
    ///     An aggregate whose results are listed in a different order from its calls still pairs each result
    ///     with its own call.
    /// </summary>
    [Fact]
    public void FromMessages_AggregateResultsOutOfCallOrder_PairsEachResultWithItsOwnCall()
    {
        var aggregate = new ToolsCallAggregateMessage(
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "read_a",
                        FunctionArgs = "{}",
                        ToolCallId = "toolu_a",
                    },
                    new ToolCall
                    {
                        FunctionName = "read_b",
                        FunctionArgs = "{}",
                        ToolCallId = "toolu_b",
                    },
                ],
            },
            new ToolsCallResultMessage
            {
                ToolCallResults =
                [
                    new ToolCallResult("toolu_b", "result b"),
                    new ToolCallResult("toolu_a", "result a"),
                ],
            }
        );

        var request = AnthropicRequest.FromMessages([new TextMessage { Role = Role.User, Text = "go" }, aggregate]);

        AssertEveryToolResultAnswersThePrecedingToolUseOnce(request);
        var toolUses = request.Messages[1].Content.Where(b => b.Type == "tool_use").Select(b => b.Id);
        Assert.Equal(["toolu_a", "toolu_b"], toolUses);
        var results = request.Messages[2].Content.ToDictionary(b => b.ToolUseId!, b => b.Content);
        Assert.Equal("result a", results["toolu_a"]);
        Assert.Equal("result b", results["toolu_b"]);
    }

    private static void AssertEveryToolResultAnswersThePrecedingToolUseOnce(AnthropicRequest request)
    {
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            if (message.Role == "assistant")
            {
                var toolUseIds = message.Content.Where(b => b.Type == "tool_use").Select(b => b.Id).ToList();
                if (toolUseIds.Count == 0)
                {
                    continue;
                }

                Assert.True(i + 1 < request.Messages.Count, "a tool_use turn must be followed by its results");
                var resultIds = request
                    .Messages[i + 1]
                    .Content.Where(b => b.Type == "tool_result")
                    .Select(b => b.ToolUseId)
                    .ToList();
                Assert.Equal(toolUseIds.Order(), resultIds.Order());
            }
            else
            {
                var previousToolUseIds =
                    i > 0 && request.Messages[i - 1].Role == "assistant"
                        ? request.Messages[i - 1].Content.Where(b => b.Type == "tool_use").Select(b => b.Id).ToHashSet()
                        : [];
                foreach (var result in message.Content.Where(b => b.Type == "tool_result"))
                {
                    Assert.Contains(result.ToolUseId, previousToolUseIds);
                }
            }
        }
    }
}
