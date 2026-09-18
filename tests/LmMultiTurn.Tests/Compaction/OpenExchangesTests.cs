using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>RC3 (eval spec §4): what is still owed or awaited at the cut.</summary>
public sealed class OpenExchangesTests
{
    private static SequencedMessage Row(long seq, IMessage message) => new(seq, $"m{seq}", "run-1", message);

    private static AgentMessage Msg(
        string id,
        AgentMessageType type,
        string from = "agent-2",
        string? body = "b",
        string? inResponseTo = null
    ) => AgentMessage.Create(id, type, from, from, body, inResponseTo, generationId: id) with { RunId = "run-1" };

    [Fact]
    public void AnInboundQuestionWithNoResponse_IsOpen()
    {
        var rows = new[]
        {
            Row(1, new TextMessage { Text = "go", Role = Role.User }),
            Row(2, Msg("q1", AgentMessageType.Question, body: "which db?")),
        };

        var open = OpenExchanges.Find(rows, cutSeq: 2);

        open.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new OpenExchangeRef
                {
                    MessageId = "q1",
                    Direction = "inbound",
                    From = "agent-2",
                    Seq = 2,
                    AskedAtRun = "run-1",
                    Summary = "which db?",
                }
            );
    }

    [Fact]
    public void AResponseAnywhereInTheThread_ClosesTheQuestion_EvenPastTheCut()
    {
        var rows = new[]
        {
            Row(1, Msg("q1", AgentMessageType.Question)),
            Row(2, new TextMessage { Text = "…", Role = Role.Assistant }),
            Row(3, Msg("r1", AgentMessageType.Response, from: "agent-1", inResponseTo: "q1")),
        };

        OpenExchanges.Find(rows, cutSeq: 2).Should().BeEmpty();
    }

    [Fact]
    public void AnInboundDelegateTask_IsOpenUntilAResponse_NotATaskUpdate()
    {
        var rows = new[]
        {
            Row(1, Msg("d1", AgentMessageType.DelegateTask, from: "agent-parent")),
            Row(2, Msg("u1", AgentMessageType.TaskUpdate, from: "agent-parent", inResponseTo: "d1")),
        };

        OpenExchanges.Find(rows, 2).Should().ContainSingle().Which.MessageId.Should().Be("d1");
    }

    [Fact]
    public void AQuestionAfterTheCut_IsNotOpenAtTheCut()
    {
        var rows = new[]
        {
            Row(1, new TextMessage { Text = "go", Role = Role.User }),
            Row(2, Msg("q1", AgentMessageType.Question)),
        };

        OpenExchanges.Find(rows, cutSeq: 1).Should().BeEmpty();
    }

    [Fact]
    public void AnOutboundQuestionReceipt_IsOpenUntilAResponseArrives()
    {
        var receipt = """
            {"status":"accepted","message_id":"out-1","to_agent_id":"agent-3","to_name":"worker","msg_type":"question"}
            """;
        var rows = new List<SequencedMessage>
        {
            Row(
                1,
                new ToolCallMessage
                {
                    ToolCallId = "c1",
                    FunctionName = "SendMessage",
                    FunctionArgs = """{"target":"agent-3","msg_type":"question","content":"ready?"}""",
                }
            ),
            Row(
                2,
                new ToolCallResultMessage
                {
                    ToolCallId = "c1",
                    ToolName = "SendMessage",
                    Result = receipt,
                }
            ),
        };

        var open = OpenExchanges.Find(rows, 2);
        open.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new OpenExchangeRef
                {
                    MessageId = "out-1",
                    Direction = "outbound",
                    From = "self",
                    To = "agent-3",
                    Seq = 2,
                    AskedAtRun = "run-1",
                    Summary = "ready?",
                }
            );

        rows.Add(Row(3, Msg("a1", AgentMessageType.Response, from: "agent-3", inResponseTo: "out-1")));
        OpenExchanges.Find(rows, 3).Should().BeEmpty();
    }

    [Fact]
    public void AnOutboundSteer_IsNeverOpen()
    {
        var rows = new[]
        {
            Row(
                1,
                new ToolCallMessage
                {
                    ToolCallId = "c1",
                    FunctionName = "SendMessage",
                    FunctionArgs = """{"target":"agent-3","msg_type":"steer","content":"stop that"}""",
                }
            ),
            Row(
                2,
                new ToolCallResultMessage
                {
                    ToolCallId = "c1",
                    ToolName = "SendMessage",
                    Result =
                        """{"status":"accepted","message_id":"out-2","to_agent_id":"agent-3","msg_type":"steer"}""",
                }
            ),
        };

        OpenExchanges.Find(rows, 2).Should().BeEmpty();
    }

    [Fact]
    public void ADescendantQuestion_IsOpenUntilItsAgentCompletes()
    {
        var rows = new List<SequencedMessage>
        {
            Row(
                1,
                new NotifyMessage
                {
                    NotifyKind = NotifyKinds.DescendantQuestion,
                    Label = "agent-4 asks",
                    Detail = "may I?",
                    SourceToolCallId = "agent-4",
                }
            ),
        };

        OpenExchanges.Find(rows, 1).Should().ContainSingle().Which.MessageId.Should().Be("descendant:agent-4:1");

        rows.Add(
            Row(
                2,
                new NotifyMessage
                {
                    NotifyKind = NotifyKinds.SubAgentCompletion,
                    Label = "done",
                    SourceToolCallId = "agent-4",
                }
            )
        );
        OpenExchanges.Find(rows, 2).Should().BeEmpty();
    }

    [Fact]
    public void Summary_IsCappedAt200Chars()
    {
        var rows = new[] { Row(1, Msg("q1", AgentMessageType.Question, body: new string('q', 500))) };

        OpenExchanges.Find(rows, 1).Single().Summary.Length.Should().Be(200);
    }
}
