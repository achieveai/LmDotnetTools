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
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// The experimental elapsed-time notice: an opt-in, persisted, never-published user-role message the
/// loop appends at a turn boundary once at least one interval of wall-clock time has passed since the
/// run started or the previous notice.
/// </summary>
public class ElapsedTimeNoticeTests
{
    private static readonly DateTimeOffset RunStart = new(2026, 9, 15, 14, 2, 19, TimeSpan.Zero);

    [Fact]
    public async Task NoOptions_NeverInjectsANotice()
    {
        var clock = new FakeTimeProvider(RunStart);
        var harness = new ThreeTurnHarness(clock, advanceBeforeEachTurn: TimeSpan.FromMinutes(5));

        var (requests, _, _) = await harness.RunAsync(elapsedTimeNotice: null);

        requests.Should().HaveCount(3);
        requests.SelectMany(r => r).Where(ElapsedTimeNotice.IsNotice).Should().BeEmpty();
    }

    [Fact]
    public async Task IntervalNotReached_InjectsNothing()
    {
        var clock = new FakeTimeProvider(RunStart);
        var harness = new ThreeTurnHarness(clock, advanceBeforeEachTurn: TimeSpan.FromSeconds(20));

        var (requests, _, _) = await harness.RunAsync(
            new ElapsedTimeNoticeOptions { Interval = TimeSpan.FromSeconds(60), Clock = clock }
        );

        // The clock advances inside each generation, so the boundary before turn 2 sees 20s and the
        // one before turn 3 sees 40s. Neither reaches the 60s interval.
        requests.SelectMany(r => r).Where(ElapsedTimeNotice.IsNotice).Should().BeEmpty();
    }

    [Fact]
    public async Task IntervalReached_InjectsANoticeBeforeTheNextTurn_AndPersistsIt()
    {
        var clock = new FakeTimeProvider(RunStart);
        var harness = new ThreeTurnHarness(clock, advanceBeforeEachTurn: TimeSpan.FromSeconds(61));

        var (requests, published, store) = await harness.RunAsync(
            new ElapsedTimeNoticeOptions { Interval = TimeSpan.FromSeconds(60), Clock = clock }
        );

        requests.Should().HaveCount(3);

        // Turn 1: the run just started, nothing has elapsed.
        requests[0].Where(ElapsedTimeNotice.IsNotice).Should().BeEmpty();

        // Turn 2: 61s since the run started. The notice is a user-role text at the END of the
        // request (after the tool result the previous turn appended), never system-role.
        var second = requests[1].Where(ElapsedTimeNotice.IsNotice).ToList();
        second.Should().ContainSingle();
        second[0].Role.Should().Be(Role.User);
        requests[1].Last().Should().BeSameAs(second[0]);
        var text = ((TextMessage)second[0]).Text;
        text.Should().StartWith("<elapsed-time-notice>").And.EndWith("</elapsed-time-notice>");
        text.Should().Contain("working on the current request for 1m 1s");
        text.Should().Contain("Current time: 2026-09-15 14:03:20 UTC");

        // Turn 3: 122s since run start, 61s since the last notice. A second notice, cumulative.
        var third = requests[2].Where(ElapsedTimeNotice.IsNotice).ToList();
        third.Should().HaveCount(2, "the earlier notice stays in history and a new one is appended");
        ((TextMessage)third[1]).Text.Should().Contain("for 2m 2s");

        // Persisted with the marker intact, so a reload can recognise the row.
        var rows = await store.LoadMessagesAsync(ThreeTurnHarness.ThreadId);
        var persistedNotices = rows.Where(r => r.MessageJson.Contains(ElapsedTimeNotice.MetadataKey)).ToList();
        persistedNotices.Should().HaveCount(2);
        persistedNotices.Should().OnlyContain(r => r.Role == "User" && r.MessageType == nameof(TextMessage));
        var roundTripped = MessagePersistenceConverter.FromPersistedMessagesResilient(persistedNotices);
        roundTripped.Should().OnlyContain(m => ElapsedTimeNotice.IsNotice(m));

        // Never published: the run's own stream carries no user-role notice bubble.
        published.Where(ElapsedTimeNotice.IsNotice).Should().BeEmpty();
    }

    [Fact]
    public async Task IntervalIsMeasuredFromTheLastNotice_NotEveryTurn()
    {
        var clock = new FakeTimeProvider(RunStart);
        // 61s, then 20s, then 45s: a notice at turn 2 (61s) and none at turn 3 (only 20s since it).
        var harness = new ThreeTurnHarness(clock, [TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(20)]);

        var (requests, _, _) = await harness.RunAsync(
            new ElapsedTimeNoticeOptions { Interval = TimeSpan.FromSeconds(60), Clock = clock }
        );

        requests[1].Count(ElapsedTimeNotice.IsNotice).Should().Be(1);
        requests[2].Count(ElapsedTimeNotice.IsNotice).Should().Be(1, "20s since the last notice is under the interval");
    }

    [Fact]
    public async Task ANewRunStartsItsOwnClock()
    {
        var clock = new FakeTimeProvider(RunStart);
        var harness = new ThreeTurnHarness(clock, advanceBeforeEachTurn: TimeSpan.Zero);
        var options = new ElapsedTimeNoticeOptions { Interval = TimeSpan.FromSeconds(60), Clock = clock };

        await using var loop = harness.CreateLoop(options);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        await harness.DriveRunAsync(loop, cts.Token);
        // A long idle gap between runs must not produce a notice on the next run's first turn.
        clock.Advance(TimeSpan.FromHours(3));
        harness.Requests.Clear();
        await harness.DriveRunAsync(loop, cts.Token);

        harness.Requests.Should().HaveCount(3);
        harness.Requests[0].Where(ElapsedTimeNotice.IsNotice).Should().BeEmpty();
        await cts.CancelAsync();
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(61, "1m 1s")]
    [InlineData(600, "10m 0s")]
    [InlineData(3725, "1h 2m 5s")]
    public void FormatElapsed_ReadsNaturally(int seconds, string expected)
    {
        ElapsedTimeNotice.FormatElapsed(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void Options_RejectANonPositiveInterval()
    {
        var act = () => new ElapsedTimeNoticeOptions { Interval = TimeSpan.Zero };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// Drives one run of three turns: turns 1 and 2 answer with a tool call, turn 3 with text. The
    /// fake clock advances by the configured amount inside each generation, i.e. before the next
    /// turn boundary's check.
    /// </summary>
    private sealed class ThreeTurnHarness
    {
        public const string ThreadId = "elapsed-thread";

        private readonly FakeTimeProvider _clock;
        private readonly IReadOnlyList<TimeSpan> _advances;
        private readonly Mock<IStreamingAgent> _agent = new();
        private readonly FunctionRegistry _registry = new();
        private int _turnInRun;

        public List<List<IMessage>> Requests { get; } = [];

        public InMemoryConversationStore Store { get; } = new();

        public ThreeTurnHarness(FakeTimeProvider clock, TimeSpan advanceBeforeEachTurn)
            : this(clock, [advanceBeforeEachTurn, advanceBeforeEachTurn, advanceBeforeEachTurn]) { }

        public ThreeTurnHarness(FakeTimeProvider clock, IReadOnlyList<TimeSpan> advances)
        {
            _clock = clock;
            _advances = advances;

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
                        var turn = ++_turnInRun;
                        if (turn - 1 < _advances.Count)
                        {
                            _clock.Advance(_advances[turn - 1]);
                        }

                        IMessage reply =
                            turn < 3
                                ? new ToolCallMessage
                                {
                                    FunctionName = "ping",
                                    FunctionArgs = "{\"n\":\"1\"}",
                                    ToolCallId = $"call_{turn}",
                                    Role = Role.Assistant,
                                }
                                : new TextMessage { Text = "Done!", Role = Role.Assistant };
                        return Task.FromResult(ToAsyncEnumerable([reply]));
                    }
                );
        }

        public MultiTurnAgentLoop CreateLoop(ElapsedTimeNoticeOptions? elapsedTimeNotice)
        {
            return new MultiTurnAgentLoop(
                _agent.Object,
                _registry,
                ThreadId,
                includeAskUserQuestionTool: false,
                includeNotifyClientTool: false,
                store: Store,
                elapsedTimeNotice: elapsedTimeNotice
            );
        }

        public async Task<List<IMessage>> DriveRunAsync(MultiTurnAgentLoop loop, CancellationToken ct)
        {
            _turnInRun = 0;
            var published = new List<IMessage>();
            var input = new UserInput([new TextMessage { Text = "go", Role = Role.User }]);
            await foreach (var msg in loop.ExecuteRunAsync(input, ct))
            {
                published.Add(msg);
            }

            published.OfType<RunCompletedMessage>().Should().NotBeEmpty();
            return published;
        }

        public async Task<(
            List<List<IMessage>> Requests,
            List<IMessage> Published,
            InMemoryConversationStore Store
        )> RunAsync(ElapsedTimeNoticeOptions? elapsedTimeNotice)
        {
            await using var loop = CreateLoop(elapsedTimeNotice);
            using var cts = new CancellationTokenSource();
            _ = loop.RunAsync(cts.Token);

            var published = await DriveRunAsync(loop, cts.Token);
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
