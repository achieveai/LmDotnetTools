using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// A turn after tool results that replies with nothing but reasoning is the model ending its turn early,
/// not the task being done. Found in the dual-layer eval: two planners ended with a thinking block only,
/// the loop read "no tool calls" as complete, and neither wrote its answer.
/// </summary>
public class EmptyReplyNudgeTests
{
    private const string ThreadId = "nudge-thread";

    private static IMessage Tool(int n) =>
        new ToolCallMessage
        {
            FunctionName = "ping",
            FunctionArgs = "{\"n\":\"1\"}",
            ToolCallId = $"call_{n}",
            Role = Role.Assistant,
        };

    private static IMessage ThinkingOnly() =>
        new ReasoningMessage { Reasoning = "I'm going through each ticket", Role = Role.Assistant };

    private static IMessage Text(string text) => new TextMessage { Text = text, Role = Role.Assistant };

    [Fact]
    public async Task A_reasoning_only_turn_after_tool_results_is_asked_to_continue_and_the_run_finishes()
    {
        var harness = new ScriptedHarness([
            [Tool(1)],
            [ThinkingOnly()],
            [Text("Done!")],
        ]);

        var (requests, published, store) = await harness.RunAsync();

        requests.Should().HaveCount(3, "the silent turn is followed by one more, not by the end of the run");
        requests[1].Where(EmptyReplyNudge.IsNudge).Should().BeEmpty();
        var nudge = requests[2].Last();
        EmptyReplyNudge.IsNudge(nudge).Should().BeTrue("the nudge is the newest message the model sees");
        nudge.Role.Should().Be(Role.User);
        published.OfType<TextMessage>().Should().Contain(t => t.Text == "Done!");

        // Persisted with its marker, so every reader can hide it; never published to a client.
        var rows = await store.LoadMessagesAsync(ThreadId);
        var persisted = MessagePersistenceConverter.FromPersistedMessagesResilient([
            .. rows.Where(r => r.MessageJson.Contains(EmptyReplyNudge.MetadataKey)),
        ]);
        persisted.Should().ContainSingle().Which.Should().Match<IMessage>(m => EmptyReplyNudge.IsNudge(m));
        published.Where(EmptyReplyNudge.IsNudge).Should().BeEmpty();
    }

    [Fact]
    public async Task A_model_that_keeps_replying_with_nothing_gets_two_nudges_and_then_the_run_ends()
    {
        var harness = new ScriptedHarness([
            [Tool(1)],
            [ThinkingOnly()],
            [ThinkingOnly()],
            [ThinkingOnly()],
        ]);

        var (requests, _, _) = await harness.RunAsync();

        requests.Should().HaveCount(4);
        requests[^1].Count(EmptyReplyNudge.IsNudge).Should().Be(MultiTurnAgentLoop.MaxEmptyReplyNudges);
    }

    [Fact]
    public async Task A_turn_with_tool_calls_resets_the_budget_so_a_later_silent_stretch_is_recovered_too()
    {
        // Round 5b: a planner went silent twice in a row, worked for four more turns, then went silent
        // again. A per-run budget was already spent and the run ended without an answer.
        var harness = new ScriptedHarness([
            [Tool(1)],
            [ThinkingOnly()],
            [Tool(2)],
            [ThinkingOnly()],
            [ThinkingOnly()],
            [Text("Done!")],
        ]);

        var (requests, published, _) = await harness.RunAsync();

        requests.Should().HaveCount(6);
        requests[^1].Count(EmptyReplyNudge.IsNudge).Should().Be(3);
        published.OfType<TextMessage>().Should().Contain(t => t.Text == "Done!");
    }

    [Theory]
    [InlineData("first turn")]
    [InlineData("text reply")]
    [InlineData("whitespace reply")]
    public async Task Only_a_silent_turn_after_tool_results_is_nudged(string shape)
    {
        List<List<IMessage>> script = shape switch
        {
            // Nothing precedes the first turn but the person's input: the pattern the nudge answers
            // (text following tool results) cannot have caused it, so an empty first reply stands.
            "first turn" =>
            [
                [ThinkingOnly()],
            ],
            "text reply" =>
            [
                [Tool(1)],
                [ThinkingOnly(), Text("Done!")],
            ],
            _ =>
            [
                [Tool(1)],
                [Text("  "), ThinkingOnly()],
                [Text("Done!")],
            ],
        };
        var harness = new ScriptedHarness(script);

        var (requests, _, _) = await harness.RunAsync();

        var expectedNudges = shape == "whitespace reply" ? 1 : 0;
        requests.SelectMany(r => r).Count(EmptyReplyNudge.IsNudge).Should().Be(expectedNudges);
        requests.Should().HaveCount(script.Count);
    }

    [Fact]
    public void Both_loop_authored_texts_are_recognised_and_a_person_s_text_is_not()
    {
        EmptyReplyNudge.IsLoopAuthored(EmptyReplyNudge.Build()).Should().BeTrue();
        EmptyReplyNudge
            .IsLoopAuthored(ElapsedTimeNotice.Build(TimeSpan.FromMinutes(1), DateTimeOffset.UnixEpoch))
            .Should()
            .BeTrue();
        EmptyReplyNudge
            .IsLoopAuthored(new TextMessage { Text = EmptyReplyNudge.Text, Role = Role.User })
            .Should()
            .BeFalse("the marker, not the wording, makes a text loop-authored");
    }

    /// <summary>Answers each turn of one run with the next scripted reply, and records every request.</summary>
    private sealed class ScriptedHarness
    {
        private readonly Mock<IStreamingAgent> _agent = new();
        private readonly FunctionRegistry _registry = new();
        private int _turn;

        public ScriptedHarness(IReadOnlyList<List<IMessage>> script)
        {
            _registry.AddFunction(
                new FunctionContract
                {
                    Name = "ping",
                    Description = "ping",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "n",
                            Description = "n",
                            ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                            IsRequired = true,
                        },
                    ],
                },
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("pong"))
            );

            _agent
                .Setup(a =>
                    a.GenerateReplyStreamingAsync(
                        It.IsAny<IEnumerable<IMessage>>(),
                        It.IsAny<GenerateReplyOptions>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                    (messages, _, _) =>
                    {
                        Requests.Add([.. messages]);
                        var reply = _turn < script.Count ? script[_turn] : [Text("unscripted")];
                        _turn++;
                        return Task.FromResult(ToAsyncEnumerable(reply));
                    }
                );
        }

        public List<List<IMessage>> Requests { get; } = [];

        public InMemoryConversationStore Store { get; } = new();

        public async Task<(
            List<List<IMessage>> Requests,
            List<IMessage> Published,
            InMemoryConversationStore Store
        )> RunAsync()
        {
            await using var loop = new MultiTurnAgentLoop(
                _agent.Object,
                _registry,
                ThreadId,
                includeAskUserQuestionTool: false,
                includeNotifyClientTool: false,
                store: Store
            );
            using var cts = new CancellationTokenSource();
            _ = loop.RunAsync(cts.Token);

            var published = new List<IMessage>();
            var input = new UserInput([new TextMessage { Text = "go", Role = Role.User }]);
            await foreach (var msg in loop.ExecuteRunAsync(input, cts.Token))
            {
                published.Add(msg);
            }

            published.OfType<RunCompletedMessage>().Should().NotBeEmpty();
            await cts.CancelAsync();
            return (Requests, published, Store);
        }

        private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
            List<IMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var msg in messages)
            {
                ct.ThrowIfCancellationRequested();
                yield return msg;
                await Task.Yield();
            }
        }
    }
}
