using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// The bounded read behind <c>RecallConversation</c> (spec 679 §6.1): scope to the boundary, the
/// filters, the caps and the <c>nothing_compacted</c> answer, over a seeded store.
/// </summary>
public class RecallConversationToolTests
{
    private const string Thread = "thread-recall";

    /// <summary>
    /// run-1: human(1), Bash call(2) + result(3), reasoning(4), Bash call(5) + result(6), assistant(7);
    /// run-2: human(8).
    /// </summary>
    private static async Task<InMemoryConversationStore> SeedAsync()
    {
        var store = new InMemoryConversationStore();
        IMessage[] runOne =
        [
            new TextMessage { Text = "fix the flaky test", Role = Role.User },
            new ToolCallMessage
            {
                ToolCallId = "call-1",
                FunctionName = "Bash",
                FunctionArgs = """{"cmd":"dotnet test"}""",
                Role = Role.Assistant,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call-1",
                Result = "output of turn 1",
                Role = Role.Tool,
            },
            new ReasoningMessage { Reasoning = "thinking about port 8443", Role = Role.Assistant },
            new ToolCallMessage
            {
                ToolCallId = "call-2",
                FunctionName = "Bash",
                FunctionArgs = """{"cmd":"curl :8443"}""",
                Role = Role.Assistant,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call-2",
                Result = "output of turn 2 with port 8443",
                Role = Role.Tool,
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ];
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(runOne, Thread, "run-1")
        );
        await store.AppendMessagesAsync(
            Thread,
            MessagePersistenceConverter.ToPersistedMessages(
                [new TextMessage { Text = "next", Role = Role.User }],
                Thread,
                "run-2"
            )
        );
        return store;
    }

    private static RecallConversationToolProvider Provider(
        IConversationStore? store,
        long? boundary,
        RecallLimits? limits = null
    ) => new(Thread, store, () => boundary, limits);

    private static Task<RecallConversationToolProvider.RecallResult> ReadAsync(
        RecallConversationToolProvider provider,
        IConversationStore store,
        long boundary,
        RecallConversationToolProvider.RecallArgs args
    ) => provider.ReadAsync(store, boundary, args, CancellationToken.None);

    [Fact]
    public async Task NoActiveCheckpoint_AnswersNothingCompacted()
    {
        var store = await SeedAsync();
        var handler = Provider(store, boundary: null).GetFunctions().Single().Handler;

        var result = await handler("{}", new ToolCallContext { ToolCallId = "tc" }, CancellationToken.None);

        result.Should().BeOfType<ToolHandlerResult.Resolved>().Which.Payload.Text.Should().Contain("nothing_compacted");
    }

    [Fact]
    public async Task Handler_ReturnsSnakeCaseJson_WithTheSpecShape()
    {
        var store = await SeedAsync();
        var handler = Provider(store, boundary: 7).GetFunctions().Single().Handler;

        var result = await handler("""{"query":"port 8443"}""", new ToolCallContext(), CancellationToken.None);

        var text = result.Should().BeOfType<ToolHandlerResult.Resolved>().Which.Payload.Text;
        text.Should()
            .Contain("\"boundary_seq\":7")
            .And.Contain("\"matched\":1")
            .And.Contain("\"tool_call_id\":\"call-2\"");
        text.Should().Contain("\"seq\":6").And.Contain("\"run_id\":\"run-1\"").And.Contain("\"hint\":");
    }

    [Fact]
    public async Task Query_MatchesCaseInsensitively_AndSkipsReasoningRows()
    {
        var store = await SeedAsync();
        var provider = Provider(store, 7);

        var outputs = await ReadAsync(provider, store, 7, new() { Query = "OUTPUT" });
        var reasoning = await ReadAsync(provider, store, 7, new() { Query = "thinking" });

        outputs.Matched.Should().Be(2);
        outputs.Rows.Select(r => r.Seq).Should().Equal(3, 6);
        outputs.Truncated.Should().BeFalse();
        reasoning.Matched.Should().Be(0, "reasoning rows are not conversation content");
    }

    [Fact]
    public async Task RowsBeyondTheBoundary_AreNeverReturned()
    {
        var store = await SeedAsync();
        var provider = Provider(store, 3);

        var all = await ReadAsync(provider, store, 3, new());
        var past = await ReadAsync(provider, store, 3, new() { FromSeq = 5, ToSeq = 8 });

        all.Rows.Select(r => r.Seq).Should().Equal(1, 2, 3);
        all.BoundarySeq.Should().Be(3);
        past.Matched.Should().Be(0);
    }

    [Fact]
    public async Task ToolCallId_ReturnsTheCallAndItsResult()
    {
        var store = await SeedAsync();
        var provider = Provider(store, 7);

        var pair = await ReadAsync(provider, store, 7, new() { ToolCallId = "call-2" });

        pair.Rows.Select(r => r.Seq).Should().Equal(5, 6);
        pair.Rows[0].Text.Should().Contain("Bash(").And.Contain("curl :8443");
        pair.Rows[1].Text.Should().Be("output of turn 2 with port 8443");
        pair.Rows.Should().OnlyContain(r => r.ToolCallId == "call-2");
    }

    [Fact]
    public async Task RunId_FiltersToOneRun()
    {
        var store = await SeedAsync();
        var provider = Provider(store, 8);

        var runTwo = await ReadAsync(provider, store, 8, new() { RunId = "run-2" });

        runTwo.Rows.Select(r => r.Seq).Should().Equal(8);
        runTwo.Rows[0].Role.Should().Be("user");
        runTwo.Rows[0].Type.Should().Be(nameof(TextMessage));
    }

    [Fact]
    public async Task Limit_IsClampedToTheMaximum_AndOverflowIsFlagged()
    {
        var store = await SeedAsync();
        var provider = Provider(store, 7, new RecallLimits { MaxLimit = 3 });

        var capped = await ReadAsync(provider, store, 7, new() { Limit = 100 });

        capped.Matched.Should().Be(6, "six non-reasoning rows sit at or before the boundary");
        capped.Returned.Should().Be(3);
        capped.Truncated.Should().BeTrue();
        capped.Hint.Should().Contain("narrow");
    }

    [Fact]
    public async Task RowText_IsCutAtTheRowCap_WithTheSeqInTheMarker()
    {
        var store = await SeedAsync();
        var provider = Provider(store, 7, new RecallLimits { RowCharCap = 10 });

        var cut = await ReadAsync(provider, store, 7, new() { Query = "flaky" });

        cut.Rows.Single().Text.Should().Be("fix the fl…[truncated, seq 1]");
        cut.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task MaxChars_BoundsTheWholeAnswer()
    {
        var store = await SeedAsync();
        var provider = Provider(store, 7, new RecallLimits { MaxMaxChars = 20 });

        var bounded = await ReadAsync(provider, store, 7, new() { MaxChars = 1_000 });

        bounded.Rows.Sum(r => r.Text.Length).Should().BeLessThanOrEqualTo(20 + "…[truncated, seq N]".Length);
        bounded.Returned.Should().BeLessThan(bounded.Matched);
        bounded.Truncated.Should().BeTrue();
    }
}
