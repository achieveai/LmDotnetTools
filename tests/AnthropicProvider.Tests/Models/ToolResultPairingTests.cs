using AchieveAi.LmDotnetTools.LmCore.Models;

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
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Review the change" },
            Aggregate(["toolu_a", "toolu_b"], ("toolu_b", "result b")),
            new TextMessage { Role = Role.User, Text = "Five minutes have passed." },
            Results(("toolu_a", "result a")),
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
        var aggregate = Aggregate(["toolu_a", "toolu_b"], ("toolu_b", "result b"), ("toolu_a", "result a"));

        var request = AnthropicRequest.FromMessages([new TextMessage { Role = Role.User, Text = "go" }, aggregate]);

        AssertEveryToolResultAnswersThePrecedingToolUseOnce(request);
        var toolUses = request.Messages[1].Content.Where(b => b.Type == "tool_use").Select(b => b.Id);
        Assert.Equal(["toolu_a", "toolu_b"], toolUses);
        var results = request.Messages[2].Content.ToDictionary(b => b.ToolUseId!, b => b.Content);
        Assert.Equal("result a", results["toolu_a"]);
        Assert.Equal("result b", results["toolu_b"]);
    }

    /// <summary>
    ///     A result with no ToolCallId says nothing about which call it answers. Every producer stamps the
    ///     call's id on its result, so such a result is foreign input; guessing its call by position is how one
    ///     call's result was sent under another's id, so the request is refused instead of sent or trimmed.
    /// </summary>
    [Theory]
    [InlineData("toolu_a")]
    [InlineData(null)]
    public void FromMessages_AggregateResultWithoutToolCallId_IsRejected(string? callId)
    {
        var aggregate = Aggregate([callId], (null, "result"));

        var error = Assert.Throws<ArgumentException>(() =>
            AnthropicRequest.FromMessages([new TextMessage { Role = Role.User, Text = "go" }, aggregate])
        );

        Assert.Contains("no ToolCallId", error.Message);
    }

    /// <summary>
    ///     [user, call a, usage, result b, user]: the generation's usage row makes the aggregate a composite,
    ///     and with call a unanswered and result b unasked nothing of it is sent. No empty assistant turn may
    ///     be left between the two user turns.
    /// </summary>
    [Fact]
    public void FromMessages_CompositeOfUsageAndUnmatchedAggregate_EmitsNoEmptyAssistantTurn()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "go" },
            new CompositeMessage
            {
                Role = Role.Assistant,
                Messages = [new UsageMessage { Usage = new Usage() }, Aggregate(["toolu_a"], ("toolu_b", "result b"))],
            },
            new TextMessage { Role = Role.User, Text = "next" },
        };

        var request = AnthropicRequest.FromMessages(messages);

        AssertNoEmptyTurnAndRolesAlternate(request);
        var turn = Assert.Single(request.Messages);
        Assert.Equal(["go", "next"], turn.Content.Select(b => b.Text));
    }

    /// <summary>
    ///     An aggregate with no call matching any of its results sends no tool turn at all, rather than an empty
    ///     assistant turn and an empty user turn.
    /// </summary>
    [Fact]
    public void FromMessages_AggregateWithNoMatchingResult_EmitsNoTurn()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "go" },
            Aggregate(["toolu_a"], ("toolu_b", "result b")),
            new TextMessage { Role = Role.User, Text = "next" },
        };

        var request = AnthropicRequest.FromMessages(messages);

        AssertNoEmptyTurnAndRolesAlternate(request);
        var turn = Assert.Single(request.Messages);
        Assert.Equal(["go", "next"], turn.Content.Select(b => b.Text));
    }

    /// <summary>
    ///     A composite whose text survives but whose aggregate has no matching result keeps the assistant text
    ///     and sends no empty user turn after it.
    /// </summary>
    [Fact]
    public void FromMessages_CompositeWithTextAndUnmatchedAggregate_KeepsTheTextAndEmitsNoEmptyUserTurn()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "go" },
            new CompositeMessage
            {
                Role = Role.Assistant,
                Messages =
                [
                    new TextMessage { Role = Role.Assistant, Text = "reading" },
                    Aggregate(["toolu_a"], ("toolu_b", "result b")),
                ],
            },
        };

        var request = AnthropicRequest.FromMessages(messages);

        AssertNoEmptyTurnAndRolesAlternate(request);
        Assert.Equal(["user", "assistant"], request.Messages.Select(m => m.Role));
        Assert.Equal(["reading"], request.Messages[1].Content.Select(b => b.Text));
    }

    /// <summary>
    ///     Two results for one call in the user turn after it: the first one stays, the later one is dropped.
    /// </summary>
    [Fact]
    public void FromMessages_TwoResultsForOneCall_KeepsTheFirst()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "go" },
            Aggregate(["toolu_a"], ("toolu_a", "first")),
            Results(("toolu_a", "second")),
        };

        var request = AnthropicRequest.FromMessages(messages);

        AssertEveryToolResultAnswersThePrecedingToolUseOnce(request);
        var result = Assert.Single(request.Messages[2].Content, b => b.Type == "tool_result");
        Assert.Equal("toolu_a", result.ToolUseId);
        Assert.Equal("first", result.Content);
    }

    /// <summary>
    ///     A user turn holding nothing but a result for no call is removed, and the assistant turns on either
    ///     side of it are joined again so the roles still alternate.
    /// </summary>
    [Fact]
    public void FromMessages_UserTurnOfOnlyAnOrphanResult_IsRemovedAndItsNeighboursMerge()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "go" },
            new TextMessage { Role = Role.Assistant, Text = "first" },
            Results(("toolu_orphan", "orphan")),
            new TextMessage { Role = Role.Assistant, Text = "second" },
        };

        var request = AnthropicRequest.FromMessages(messages);

        AssertNoEmptyTurnAndRolesAlternate(request);
        Assert.Equal(["user", "assistant"], request.Messages.Select(m => m.Role));
        Assert.Equal(["first", "second"], request.Messages[1].Content.Select(b => b.Text));
    }

    private static ToolsCallAggregateMessage Aggregate(
        IEnumerable<string?> callIds,
        params (string? Id, string Result)[] results
    ) =>
        new(
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    .. callIds.Select(id => new ToolCall
                    {
                        FunctionName = "read_" + id,
                        FunctionArgs = "{}",
                        ToolCallId = id,
                    }),
                ],
                GenerationId = "gen1",
            },
            Results(results)
        );

    private static ToolsCallResultMessage Results(params (string? Id, string Result)[] results) =>
        new() { ToolCallResults = [.. results.Select(r => new ToolCallResult(r.Id, r.Result))], GenerationId = "gen1" };

    private static void AssertNoEmptyTurnAndRolesAlternate(AnthropicRequest request)
    {
        Assert.All(request.Messages, m => Assert.NotEmpty(m.Content));
        for (var i = 1; i < request.Messages.Count; i++)
        {
            Assert.NotEqual(request.Messages[i - 1].Role, request.Messages[i].Role);
        }
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
