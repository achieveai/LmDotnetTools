using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     A tool_result must answer a tool_use of the assistant turn right before it, exactly once, under that
///     tool_use's id. Anthropic rejects any other shape with a 400 on every later turn of the conversation.
/// </summary>
public class ToolResultPairingTests(ITestOutputHelper output) : LoggingTestBase(output)
{
    private const string TestThreadId = "thread-1";

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

        var request = ToRequest(messages);

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

        var request = ToRequest([new TextMessage { Role = Role.User, Text = "go" }, aggregate]);

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
            ToRequest([new TextMessage { Role = Role.User, Text = "go" }, aggregate])
        );

        Logger.LogDebug("Rejected with {Error}", error.Message);
        Assert.Contains("no ToolCallId", error.Message);
        Assert.Contains("generation 'gen1' in thread 'thread-1'", error.Message);
        Assert.Equal("aggregateMsg", error.ParamName);
    }

    /// <summary>
    ///     An OpenAI-compatible stream can send a second call with no id; the loop stores it, then refuses to run
    ///     it, so the generation is restored as [call a, call with no id] + [result a]. The id-less call has no
    ///     result that could answer it: it is left out and logged, and call a stays paired with its result, so a
    ///     later Anthropic request of the thread still goes out.
    /// </summary>
    [Fact]
    public void FromMessages_AggregateCallWithoutToolCallId_IsDroppedAndTheRestStaysPaired()
    {
        var aggregate = Aggregate(["toolu_a", null], ("toolu_a", "result a"));
        var warnings = new CapturingLogger<AnthropicRequest>();

        var request = ToRequest([new TextMessage { Role = Role.User, Text = "go" }, aggregate], warnings);

        AssertEveryToolResultAnswersThePrecedingToolUseOnce(request);
        Assert.Equal(["user", "assistant", "user"], request.Messages.Select(m => m.Role));
        Assert.Equal(["toolu_a"], request.Messages[1].Content.Select(b => b.Id));
        var result = Assert.Single(request.Messages[2].Content);
        Assert.Equal(("toolu_a", "result a"), (result.ToolUseId, result.Content));
        var warning = Assert.Single(warnings.MessagesAtLevel(LogLevel.Warning));
        Logger.LogDebug("Logged {Warning}", warning);
        Assert.Contains("Dropping tool_use (null) (read_) of generation gen1 in thread thread-1", warning);
    }

    /// <summary>
    ///     A local call with a blank id beside a provider-server result with the same blank id: the two ids match,
    ///     and pairing them sent a tool_use with no id, which Anthropic rejects. The call is left out and logged
    ///     before any result is looked up, the server result is left out as before, and call a stays paired.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void FromMessages_BlankIdLocalCallBesideABlankIdServerResult_IsDroppedAndNotSentWithoutAnId(string? blankId)
    {
        var aggregate = Aggregate(["toolu_a", blankId], ("toolu_a", "result a"));
        aggregate = new ToolsCallAggregateMessage(
            aggregate.ToolsCallMessage,
            aggregate.ToolsCallResult with
            {
                ToolCallResults =
                [
                    .. aggregate.ToolsCallResult.ToolCallResults,
                    new ToolCallResult(blankId, "server result") { ExecutionTarget = ExecutionTarget.ProviderServer },
                ],
            }
        );
        var warnings = new CapturingLogger<AnthropicRequest>();

        var request = ToRequest([new TextMessage { Role = Role.User, Text = "go" }, aggregate], warnings);

        AssertEveryToolResultAnswersThePrecedingToolUseOnce(request);
        var blocks = request.Messages.SelectMany(m => m.Content).ToList();
        var toolUseIds = blocks.Where(b => b.Type is "tool_use" or "server_tool_use").Select(b => b.Id).ToList();
        var resultIds = blocks.Where(b => b.Type == "tool_result").Select(b => b.ToolUseId).ToList();
        var logged = warnings.MessagesAtLevel(LogLevel.Warning);
        Logger.LogDebug(
            "Blank id {BlankId}: expected tool_use ids {ExpectedToolUseIds}, actual {ActualToolUseIds}; result ids {ActualResultIds}; warnings {Warnings}",
            blankId,
            new[] { "toolu_a" },
            toolUseIds,
            resultIds,
            logged
        );
        Assert.Equal(["toolu_a"], toolUseIds);
        Assert.Equal(["toolu_a"], resultIds);
        var warning = Assert.Single(logged);
        Assert.Contains($"Dropping tool_use {blankId ?? "(null)"} (read_{blankId}) of generation gen1", warning);
        Assert.Contains("it has no ToolCallId", warning);
    }

    /// <summary>
    ///     Two local calls with the same blank id beside one provider-server result with that blank id. Neither
    ///     call may take the server result, and the second must not be sent once the first has been left out:
    ///     both are dropped with one warning each, the server result is left out silently, and call a stays
    ///     paired.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void FromMessages_TwoBlankIdLocalCallsBesideABlankIdServerResult_AreBothDroppedWithAWarningEach(
        string? blankId
    )
    {
        var aggregate = Aggregate(["toolu_a", blankId, blankId], ("toolu_a", "result a"));
        aggregate = new ToolsCallAggregateMessage(
            aggregate.ToolsCallMessage,
            aggregate.ToolsCallResult with
            {
                ToolCallResults =
                [
                    .. aggregate.ToolsCallResult.ToolCallResults,
                    new ToolCallResult(blankId, "server result") { ExecutionTarget = ExecutionTarget.ProviderServer },
                ],
            }
        );
        var warnings = new CapturingLogger<AnthropicRequest>();

        var request = ToRequest([new TextMessage { Role = Role.User, Text = "go" }, aggregate], warnings);

        AssertEveryToolResultAnswersThePrecedingToolUseOnce(request);
        var blocks = request.Messages.SelectMany(m => m.Content).ToList();
        var toolUseIds = blocks.Where(b => b.Type is "tool_use" or "server_tool_use").Select(b => b.Id).ToList();
        var resultIds = blocks.Where(b => b.Type == "tool_result").Select(b => b.ToolUseId).ToList();
        var logged = warnings.MessagesAtLevel(LogLevel.Warning);
        Logger.LogDebug(
            "Blank id {BlankId}: expected tool_use ids {ExpectedToolUseIds} and {ExpectedWarningCount} warnings, actual {ActualToolUseIds}; result ids {ActualResultIds}; warnings {Warnings}",
            blankId,
            new[] { "toolu_a" },
            2,
            toolUseIds,
            resultIds,
            logged
        );
        Assert.Equal(["toolu_a"], toolUseIds);
        Assert.Equal(["toolu_a"], resultIds);
        Assert.Equal(2, logged.Count);
        Assert.All(
            logged,
            warning =>
            {
                Assert.Contains(
                    $"Dropping tool_use {blankId ?? "(null)"} (read_{blankId}) of generation gen1",
                    warning
                );
                Assert.Contains("it has no ToolCallId", warning);
            }
        );
    }

    /// <summary>
    ///     A result dropped from a user turn, because no tool_use before it asks for it or because its tool_use
    ///     already has a result, is logged with the generation and thread it came from, so the stored rows can
    ///     be found. The result may come from a singular, a plural or a composite message.
    /// </summary>
    [Theory]
    [InlineData("singular", "orphan")]
    [InlineData("plural", "orphan")]
    [InlineData("composite", "orphan")]
    [InlineData("singular", "duplicate")]
    [InlineData("plural", "duplicate")]
    [InlineData("composite", "duplicate")]
    public void FromMessages_DroppedResults_LogTheirThreadAndGeneration(string sourceKind, string drop)
    {
        // Only the result rows carry the thread and generation, so the logged values can only come from them.
        IMessage Late(string id, string text)
        {
            var plural = new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult(id, text)],
                GenerationId = "gen_late",
                ThreadId = TestThreadId,
            };
            return sourceKind switch
            {
                "singular" => new ToolCallResultMessage
                {
                    ToolCallId = id,
                    Result = text,
                    Role = Role.User,
                    GenerationId = "gen_late",
                    ThreadId = TestThreadId,
                },
                "plural" => plural,
                _ => new CompositeMessage { Role = Role.User, Messages = [plural] },
            };
        }

        IMessage[] messages =
            drop == "orphan"
                ?
                [
                    new TextMessage { Role = Role.User, Text = "go" },
                    new TextMessage { Role = Role.Assistant, Text = "first" },
                    Late("toolu_x", "orphan"),
                ]
                :
                [
                    new TextMessage { Role = Role.User, Text = "go" },
                    new ToolsCallMessage
                    {
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                FunctionName = "read_toolu_x",
                                FunctionArgs = "{}",
                                ToolCallId = "toolu_x",
                            },
                        ],
                    },
                    Late("toolu_x", "first"),
                    Late("toolu_x", "second"),
                ];
        var warnings = new CapturingLogger<AnthropicRequest>();

        _ = ToRequest(messages, warnings);

        var warning = Assert.Single(warnings.MessagesAtLevel(LogLevel.Warning));
        Logger.LogDebug(
            "Expected generation {Generation} and thread {Thread}; logged {Warning}",
            "gen_late",
            TestThreadId,
            warning
        );
        Assert.Contains("Dropping tool_result toolu_x of generation gen_late in thread thread-1", warning);
        Assert.Contains(
            drop == "orphan"
                ? "no tool_use for it in the assistant turn before it"
                : "its tool_use already has a result",
            warning
        );
    }

    /// <summary>
    ///     The second result of a call, after the one its aggregate sent, is logged under the aggregate's
    ///     generation when the aggregate's result message has none: FunctionCallMiddleware builds its results
    ///     without a GenerationId.
    /// </summary>
    [Fact]
    public void FromMessages_DuplicateOfAnAggregateResultWithoutGeneration_LogsTheAggregatesGeneration()
    {
        var aggregate = Aggregate(["toolu_a"], ("toolu_a", "first"));
        aggregate = new ToolsCallAggregateMessage(
            aggregate.ToolsCallMessage,
            aggregate.ToolsCallResult with
            {
                GenerationId = null,
            }
        );
        var warnings = new CapturingLogger<AnthropicRequest>();

        _ = ToRequest(
            [new TextMessage { Role = Role.User, Text = "go" }, aggregate, Results(("toolu_a", "second"))],
            warnings
        );

        var warning = Assert.Single(warnings.MessagesAtLevel(LogLevel.Warning));
        Logger.LogDebug("Logged {Warning}", warning);
        Assert.Contains("Dropping tool_result toolu_a of generation gen1 in thread thread-1", warning);
    }

    /// <summary>
    ///     A call of an aggregate with no result in it, and a result with no call in it, are each left out and
    ///     logged with the aggregate's generation and thread. The result message's own generation is used when
    ///     it has one, the aggregate's otherwise.
    /// </summary>
    [Theory]
    [InlineData("gen_results", "gen_results")]
    [InlineData(null, "gen1")]
    public void FromMessages_AggregateDrops_LogTheirThreadAndGeneration(
        string? resultGeneration,
        string expectedResultGeneration
    )
    {
        var aggregate = Aggregate(["toolu_a"], ("toolu_b", "result b"));
        aggregate = new ToolsCallAggregateMessage(
            aggregate.ToolsCallMessage,
            aggregate.ToolsCallResult with
            {
                GenerationId = resultGeneration,
            }
        );
        var warnings = new CapturingLogger<AnthropicRequest>();

        _ = ToRequest([new TextMessage { Role = Role.User, Text = "go" }, aggregate], warnings);

        var logged = warnings.MessagesAtLevel(LogLevel.Warning);
        Logger.LogDebug("Logged {Warnings}", logged);
        Assert.Equal(2, logged.Count);
        Assert.Contains("Dropping tool_use toolu_a (read_toolu_a) of generation gen1 in thread thread-1", logged[0]);
        Assert.Contains(
            $"Dropping tool_result toolu_b of generation {expectedResultGeneration} in thread thread-1",
            logged[1]
        );
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

        var request = ToRequest(messages);

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

        var request = ToRequest(messages);

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

        var request = ToRequest(messages);

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

        var request = ToRequest(messages);

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

        var request = ToRequest(messages);

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
                ThreadId = TestThreadId,
            },
            Results(results)
        );

    private static ToolsCallResultMessage Results(params (string? Id, string Result)[] results) =>
        new()
        {
            ToolCallResults = [.. results.Select(r => new ToolCallResult(r.Id, r.Result))],
            GenerationId = "gen1",
            ThreadId = TestThreadId,
        };

    /// <summary>
    ///     Converts the history, logging what went in and the turns that came out.
    /// </summary>
    private AnthropicRequest ToRequest(IEnumerable<IMessage> messages, ILogger? converterLogger = null)
    {
        var history = messages.ToList();
        for (var i = 0; i < history.Count; i++)
        {
            Logger.LogDebug(
                "History[{Index}] {MessageType} role {Role} generation {GenerationId}",
                i,
                history[i].GetType().Name,
                history[i].Role,
                history[i].GenerationId
            );
        }

        var request = AnthropicRequest.FromMessages(history, logger: converterLogger);
        for (var turn = 0; turn < request.Messages.Count; turn++)
        {
            foreach (var block in request.Messages[turn].Content)
            {
                Logger.LogDebug(
                    "Turn[{Turn}] {TurnRole} block {BlockType} id {BlockId} tool_use_id {ToolUseId} text {BlockText} content {BlockContent}",
                    turn,
                    request.Messages[turn].Role,
                    block.Type,
                    block.Id,
                    block.ToolUseId,
                    block.Text,
                    block.Content
                );
            }
        }

        return request;
    }

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
