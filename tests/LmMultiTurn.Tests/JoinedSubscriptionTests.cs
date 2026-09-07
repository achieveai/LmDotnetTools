using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

public class JoinedSubscriptionTests
{
    [Fact]
    public async Task LegacyAgent_DefaultOptionsForwardAndOptionalCapabilitiesThrow()
    {
        await using var legacy = new LegacyAgent();
        IMultiTurnAgent agent = legacy;
        agent.SubscribeAsync(default).Should().BeSameAs(legacy.Output);
        legacy.SubscriptionToken.Should().Be(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var stream = agent.SubscribeAsync(SubscribeOptions.Default, cts.Token);
        stream.Should().BeSameAs(legacy.Output);
        legacy.SubscriptionToken.Should().Be(cts.Token);
        Action joined = () => agent.SubscribeAsync(SubscribeOptions.Joined);
        Action history = () => agent.GetHistorySnapshot();
        joined.Should().Throw<NotSupportedException>();
        history.Should().Throw<NotSupportedException>();
    }

    private sealed class LegacyAgent : IMultiTurnAgent
    {
        public string? CurrentRunId => null;
        public string ThreadId => "legacy";
        public bool IsRunning => false;
        public IAsyncEnumerable<IMessage> Output { get; } = Stream([], CancellationToken.None);
        public CancellationToken SubscriptionToken { get; private set; }

        public ValueTask<SendReceipt> SendAsync(
            List<IMessage> messages,
            string? inputId = null,
            string? parentRunId = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<SendReceipt?> TrySendAsync(
            List<IMessage> messages,
            string? inputId = null,
            string? parentRunId = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<IMessage> ExecuteRunAsync(UserInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default)
        {
            SubscriptionToken = ct;
            return Output;
        }

        public Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Joined_text_is_finalized_once_while_default_keeps_provider_shape(bool providerFinalizes)
    {
        var providerMessages = new List<IMessage>
        {
            new TextUpdateMessage { Text = "Hel", Role = Role.Assistant },
            new TextUpdateMessage { Text = "lo", Role = Role.Assistant },
        };
        if (providerFinalizes)
        {
            providerMessages.Add(new TextMessage { Text = "Hello!", Role = Role.Assistant });
        }

        var (joined, raw, history) = await RunAsync([providerMessages]);

        joined
            .OfType<TextMessage>()
            .Should()
            .ContainSingle()
            .Which.Text.Should()
            .Be(providerFinalizes ? "Hello!" : "Hello");
        joined
            .Should()
            .NotContain(m => m is TextUpdateMessage || m is ReasoningUpdateMessage || m is ToolCallUpdateMessage);
        raw.OfType<TextUpdateMessage>().Select(m => m.Text).Should().Equal("Hel", "lo");
        raw.OfType<TextMessage>().Select(m => m.Text).Should().Equal(providerFinalizes ? ["Hello!"] : []);
        history.OfType<TextMessage>().Where(m => m.Role == Role.Assistant).Should().ContainSingle();
        joined.OfType<RunCompletedMessage>().Should().ContainSingle();
        raw.OfType<RunCompletedMessage>().Should().ContainSingle();
    }

    [Fact]
    public async Task Thinking_blocks_are_separate_and_encrypted_reasoning_remains_in_history_only()
    {
        var (joined, raw, history) = await RunAsync([
            [
                new TextUpdateMessage
                {
                    Text = "First thought",
                    IsThinking = true,
                    Role = Role.Assistant,
                },
                new TextUpdateMessage { Text = "Answer", Role = Role.Assistant },
                new ReasoningUpdateMessage { Reasoning = "Second thought", Visibility = ReasoningVisibility.Plain },
                new ReasoningUpdateMessage
                {
                    Reasoning = "opaque-signature",
                    Visibility = ReasoningVisibility.Encrypted,
                },
            ],
        ]);

        joined
            .OfType<TextMessage>()
            .Select(m => (m.Text, m.IsThinking))
            .Should()
            .Equal(("First thought", true), ("Answer", false), ("Second thought", true));
        joined.OfType<TextMessage>().Select(m => (m.GenerationId, m.MessageOrderIdx)).Should().OnlyHaveUniqueItems();
        raw.OfType<TextUpdateMessage>()
            .Select(m => (m.Text, m.IsThinking, m.MessageOrderIdx))
            .Should()
            .Equal(("First thought", true, 0), ("Answer", false, 0));
        joined.Should().NotContain(m => m is ReasoningMessage || m is ReasoningUpdateMessage);
        history
            .OfType<ReasoningMessage>()
            .Should()
            .Contain(m => m.Visibility == ReasoningVisibility.Encrypted && m.Reasoning == "opaque-signature");
    }

    [Fact]
    public async Task Deferred_resolution_preserves_default_result_index_after_canonical_normalization()
    {
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract { Name = "lookup", Description = "Look up a value" },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred())
        );
        var provider = new Mock<IStreamingAgent>();
        provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, options, ct) =>
                    Task.FromResult(
                        Stream(
                            new IMessage[]
                            {
                                new TextUpdateMessage
                                {
                                    Text = "Thought",
                                    IsThinking = true,
                                    Role = Role.Assistant,
                                },
                                new TextUpdateMessage { Text = "Checking", Role = Role.Assistant },
                                new ToolCallUpdateMessage
                                {
                                    ToolCallId = "deferred",
                                    FunctionName = "lookup",
                                    FunctionArgs = "{}",
                                },
                            }.Select(m => m.WithIds(options)),
                            ct
                        )
                    )
            );
        await using var loop = new MultiTurnAgentLoop(provider.Object, registry, "deferred-index");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = loop.RunAsync(cts.Token);
        var firstRun = DrainAsync(loop.SubscribeAsync(cts.Token), () => { }, cts.Token);
        await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }], ct: cts.Token);
        var firstMessages = await firstRun;
        firstMessages.OfType<ToolCallResultMessage>().Should().ContainSingle().Which.MessageOrderIdx.Should().Be(2);
        loop.GetHistorySnapshot()
            .OfType<ToolCallResultMessage>()
            .Should()
            .ContainSingle()
            .Which.MessageOrderIdx.Should()
            .Be(3);
        await loop.StopAsync();
        await run;
        await using var raw = loop.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        var pending = raw.MoveNextAsync().AsTask();
        await loop.ResolveToolCallAsync("deferred", "resolved", ct: cts.Token);
        (await pending).Should().BeTrue();
        raw.Current.Should().BeOfType<ToolCallResultMessage>().Which.MessageOrderIdx.Should().Be(2);
    }

    [Fact]
    public async Task Recovered_deferred_resolution_retains_raw_index_and_canonical_history_order()
    {
        var store = new InMemoryConversationStore();
        const string threadId = "restored-index";
        IMessage[] messages =
        [
            new ToolCallMessage
            {
                ToolCallId = "deferred",
                FunctionName = "lookup",
                FunctionArgs = "{}",
                GenerationId = "gen",
                MessageOrderIdx = 2,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "deferred",
                ToolName = "lookup",
                Result = "",
                IsDeferred = true,
                GenerationId = "gen",
                MessageOrderIdx = 2,
            },
        ];
        await store.AppendMessagesAsync(
            threadId,
            [.. messages.Select(m => MessagePersistenceConverter.ToPersistedMessage(m, threadId, "run"))]
        );
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = "run",
                LastUpdated = 0,
            }
        );
        await using var loop = new MultiTurnAgentLoop(
            new Mock<IStreamingAgent>().Object,
            new FunctionRegistry(),
            threadId,
            store: store
        );
        (await loop.RecoverAsync()).Should().BeTrue();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var raw = loop.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        var pending = raw.MoveNextAsync().AsTask();
        await loop.ResolveToolCallAsync("deferred", "resolved", ct: cts.Token);
        (await pending).Should().BeTrue();
        raw.Current.Should().BeOfType<ToolCallResultMessage>().Which.MessageOrderIdx.Should().Be(2);
        loop.GetHistorySnapshot()
            .OfType<ToolCallResultMessage>()
            .Should()
            .ContainSingle()
            .Which.MessageOrderIdx.Should()
            .Be(3);
    }

    [Fact]
    public async Task Identical_standalone_reasoning_blocks_are_not_collapsed()
    {
        var (joined, _, _) = await RunAsync([
            [
                new ReasoningMessage { Reasoning = "same", Visibility = ReasoningVisibility.Summary },
                new ReasoningMessage { Reasoning = "same", Visibility = ReasoningVisibility.Summary },
            ],
        ]);
        joined.OfType<TextMessage>().Where(m => m.IsThinking).Should().HaveCount(2);
    }

    [Fact]
    public async Task Finalized_reasoning_summary_is_delivered_once_with_signature_preserved()
    {
        var (joined, raw, history) = await RunAsync([
            [
                new ReasoningUpdateMessage { Reasoning = "Thinking", Visibility = ReasoningVisibility.Summary },
                new ReasoningMessage { Reasoning = "Thinking", Visibility = ReasoningVisibility.Summary },
                new ReasoningMessage { Reasoning = "Thinking", Visibility = ReasoningVisibility.Summary },
                new ReasoningMessage { Reasoning = "signature", Visibility = ReasoningVisibility.Encrypted },
                new TextMessage { Text = "Answer", Role = Role.Assistant },
            ],
        ]);
        joined
            .OfType<TextMessage>()
            .Where(m => m.IsThinking)
            .Should()
            .ContainSingle()
            .Which.Text.Should()
            .Be("Thinking");
        raw.OfType<ReasoningUpdateMessage>().Should().ContainSingle();
        raw.OfType<ReasoningMessage>().Should().HaveCount(3);
        history
            .OfType<ReasoningMessage>()
            .Should()
            .Contain(m => m.Reasoning == "signature" && m.Visibility == ReasoningVisibility.Encrypted);
    }

    [Fact]
    public async Task Canonical_block_indices_do_not_change_default_tool_result_indices()
    {
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract { Name = "lookup", Description = "Look up a value" },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("found"))
        );
        var (joined, raw, _) = await RunAsync(
            [
                [
                    new TextUpdateMessage
                    {
                        Text = "Thought",
                        IsThinking = true,
                        Role = Role.Assistant,
                    },
                    new TextUpdateMessage { Text = "Checking", Role = Role.Assistant },
                    new ToolCallUpdateMessage
                    {
                        ToolCallId = "call-1",
                        FunctionName = "lookup",
                        FunctionArgs = "{}",
                    },
                ],
                [new TextMessage { Text = "Answer", Role = Role.Assistant }],
            ],
            registry
        );
        raw.OfType<ToolCallUpdateMessage>().Should().ContainSingle().Which.MessageOrderIdx.Should().Be(1);
        raw.OfType<ToolCallResultMessage>().Should().ContainSingle().Which.MessageOrderIdx.Should().Be(2);
        joined.OfType<ToolCallMessage>().Should().ContainSingle().Which.MessageOrderIdx.Should().Be(2);
        joined.OfType<ToolCallResultMessage>().Should().ContainSingle().Which.MessageOrderIdx.Should().Be(3);
    }

    [Fact]
    public async Task Tools_usage_and_completion_follow_history_and_history_is_ready_at_completion()
    {
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract { Name = "lookup", Description = "Look up a value" },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("found"))
        );
        var (joined, raw, history) = await RunAsync(
            [
                [
                    new ToolCallUpdateMessage
                    {
                        ToolCallId = "call-1",
                        FunctionName = "lookup",
                        FunctionArgs = "{}",
                    },
                ],
                [
                    new TextUpdateMessage { Text = "Answer", Role = Role.Assistant },
                    new UsageMessage
                    {
                        Usage = new Usage { PromptTokens = 2, CompletionTokens = 3 },
                    },
                ],
            ],
            registry
        );

        joined
            .Where(m =>
                m is ToolCallMessage or ToolCallResultMessage or TextMessage or UsageMessage or RunCompletedMessage
            )
            .Select(m => m.GetType())
            .Should()
            .Equal(
                typeof(ToolCallMessage),
                typeof(ToolCallResultMessage),
                typeof(TextMessage),
                typeof(UsageMessage),
                typeof(RunCompletedMessage)
            );
        history.OfType<ToolCallMessage>().Should().ContainSingle().Which.ToolCallId.Should().Be("call-1");
        history.OfType<ToolCallResultMessage>().Should().ContainSingle().Which.Result.Should().Be("found");
        history.OfType<TextMessage>().Should().Contain(m => m.Role == Role.Assistant && m.Text == "Answer");
        history.OfType<UsageMessage>().Should().ContainSingle();
        raw.OfType<ToolCallUpdateMessage>().Should().ContainSingle();
        raw.OfType<TextUpdateMessage>().Should().ContainSingle();
    }

    [Fact]
    public async Task Joined_replay_has_canonical_shape_and_live_tail_exactly_once()
    {
        await using var agent = new HistoryAgent();
        await agent.PublishAsync(new RunAssignmentMessage { Assignment = new RunAssignment("run", "gen") });
        await agent.PublishAsync(new TextUpdateMessage { Text = "raw", GenerationId = "gen" });
        agent.Append(
            new TextMessage
            {
                Text = "canonical",
                Role = Role.Assistant,
                GenerationId = "gen",
            }
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscriber = agent.SubscribeAsync(SubscribeOptions.Joined, cts.Token).GetAsyncEnumerator();
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeOfType<RunAssignmentMessage>();
        agent.Append(
            new TextMessage
            {
                Text = "tail",
                Role = Role.Assistant,
                GenerationId = "gen",
            }
        );
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("canonical");
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("tail");
        await agent.DisposeAsync();
        (await subscriber.MoveNextAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Slow_joined_subscriber_does_not_block_publisher_and_receives_recovery()
    {
        await using var agent = new HistoryAgent(capacity: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscriber = agent.SubscribeAsync(SubscribeOptions.Joined, cts.Token).GetAsyncEnumerator();
        var first = subscriber.MoveNextAsync().AsTask();
        await agent.PublishAsync(new RunAssignmentMessage { Assignment = new RunAssignment("run", "gen") });
        (await first).Should().BeTrue();
        agent.Append(new TextMessage { Text = "one", Role = Role.Assistant });
        agent.Append(new TextMessage { Text = "two", Role = Role.Assistant });
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("one");
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber
            .Current.Should()
            .BeOfType<StreamRecoveryMessage>()
            .Which.Reason.Should()
            .Be(StreamRecoveryReason.SlowConsumer);
        (await subscriber.MoveNextAsync()).Should().BeFalse();
        agent.GetHistorySnapshot().OfType<TextMessage>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Joined_replay_truncation_advises_resync_then_continues_live()
    {
        await using var agent = new HistoryAgent(replayCapacity: 1);
        await agent.PublishAsync(new RunAssignmentMessage { Assignment = new RunAssignment("run", "gen") });
        agent.Append(new TextMessage { Text = "prefix", Role = Role.Assistant });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscriber = agent.SubscribeAsync(SubscribeOptions.Joined, cts.Token).GetAsyncEnumerator();
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber
            .Current.Should()
            .BeOfType<StreamRecoveryMessage>()
            .Which.Reason.Should()
            .Be(StreamRecoveryReason.ReplayTruncated);
        agent.Append(new TextMessage { Text = "tail", Role = Role.Assistant });
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeOfType<TextMessage>().Which.Text.Should().Be("tail");
    }

    [Fact]
    public async Task History_is_an_independent_copy_and_joined_subscription_honors_cancellation()
    {
        await using var agent = new HistoryAgent();
        agent.Append(new TextMessage { Text = "first", Role = Role.Assistant });
        var checkpoint = new CompactionCheckpointMessage
        {
            CheckpointId = "checkpoint",
            Boundary = new CheckpointBoundary { Seq = 1, MessageId = "first" },
            Trigger = CompactionTrigger.Manual,
            Manifest = new ContextManifest(),
            Narrative = "Earlier context",
        };
        agent.Append(checkpoint);
        var snapshot = agent.GetHistorySnapshot();
        agent.Append(new TextMessage { Text = "second", Role = Role.Assistant });
        snapshot.Should().HaveCount(2);
        snapshot[0].Should().BeOfType<TextMessage>().Which.Text.Should().Be("first");
        snapshot[1].Should().BeSameAs(checkpoint);
        agent
            .GetHistorySnapshot()
            .Select(m => m.GetType())
            .Should()
            .Equal(typeof(TextMessage), typeof(CompactionCheckpointMessage), typeof(TextMessage));
        using var cts = new CancellationTokenSource();
        await using var subscriber = agent.SubscribeAsync(SubscribeOptions.Joined, cts.Token).GetAsyncEnumerator();
        var pending = subscriber.MoveNextAsync().AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Public_history_excludes_sdk_streaming_fragments()
    {
        await using var agent = new HistoryAgent();
        agent.Append(new TextUpdateMessage { Text = "partial", Role = Role.Assistant });
        agent.Append(new TextMessage { Text = "final", Role = Role.Assistant });
        agent.GetHistorySnapshot().Should().ContainSingle().Which.Should().BeOfType<TextMessage>();
    }

    [Fact]
    public async Task Concrete_agent_accepts_default_literal_and_explicit_joined_options()
    {
        await using var agent = new HistoryAgent();
        agent.SubscribeAsync(default).Should().NotBeNull();
        agent.SubscribeAsync(SubscribeOptions.Joined, default).Should().NotBeNull();
        IMultiTurnAgent throughInterface = agent;
        throughInterface.SubscribeAsync(default).Should().NotBeNull();
        throughInterface.SubscribeAsync(SubscribeOptions.Joined, default).Should().NotBeNull();
    }

    [Fact]
    public async Task Committed_deferred_replacement_precedes_joined_completion()
    {
        await using var agent = new HistoryAgent();
        agent.Append(new ToolCallResultMessage { ToolCallId = "pending", Result = "pending" });
        using var written = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var raw = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        var rawCompletion = raw.MoveNextAsync().AsTask();
        var joinedDelivery = DrainAsync(agent.SubscribeAsync(SubscribeOptions.Joined, cts.Token), () => { }, cts.Token);
        var replacement = Task.Run(() =>
            agent.Replace(
                new PausingToolResult(() =>
                {
                    // This getter runs during joined publication, after the replacement is committed.
                    agent.GetHistorySnapshot().OfType<ToolCallResultMessage>().Single().Result.Should().Be("resolved");
                    written.Set();
                    release.Wait(cts.Token);
                })
                {
                    ToolCallId = "pending",
                    Result = "resolved",
                }
            )
        );
        Exception? completionError = null;
        var completionThread = new Thread(() =>
        {
            try
            {
                agent.CompleteAsync().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                completionError = error;
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
        };

        try
        {
            written.Wait(cts.Token);
            completionThread.Start();
            (await rawCompletion).Should().BeTrue();
            raw.Current.Should().BeOfType<RunCompletedMessage>();
            // Wait for completion to either return (the bug) or block on the history boundary.
            // No sleep-based race: raw delivery proves it has reached terminal publication.
            SpinWait
                .SpinUntil(
                    () => completed.IsSet || (completionThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)
                )
                .Should()
                .BeTrue();
        }
        finally
        {
            release.Set();
        }

        await replacement;
        completed.Wait(cts.Token);
        completionError.Should().BeNull();
        var joined = await joinedDelivery;
        joined.OfType<ToolCallResultMessage>().Select(m => m.Result).Should().Equal("resolved");
        joined.OfType<RunCompletedMessage>().Should().ContainSingle();
        joined.Last().Should().BeOfType<RunCompletedMessage>();
    }

    [Fact]
    public async Task Restore_history_reads_generation_ids_in_linear_work()
    {
        await using var agent = new HistoryAgent();
        var reads = 0;
        const int count = 2000;
        var messages = Enumerable
            .Range(0, count)
            .Select(i =>
                (IMessage)
                    new CountingTextMessage(() => reads++)
                    {
                        Text = "restored",
                        GenerationId = $"generation-{i}",
                        MessageOrderIdx = 0,
                    }
            )
            .ToArray();

        agent.Restore(messages);

        reads.Should().BeLessThan(count * 10, "restoring distinct generations must not scan earlier rows repeatedly");
        agent.GetHistorySnapshot().Should().HaveCount(count);
    }

    [Fact]
    public async Task Restore_history_preserves_per_generation_order_across_batches()
    {
        await using var agent = new HistoryAgent();
        agent.Append(
            new TextMessage
            {
                Text = "existing",
                GenerationId = "g",
                MessageOrderIdx = 4,
            }
        );
        agent.Restore([
            new TextMessage
            {
                Text = "other",
                GenerationId = "h",
                MessageOrderIdx = 0,
            },
            new TextMessage
            {
                Text = "first",
                GenerationId = "g",
                MessageOrderIdx = 0,
            },
            new TextUpdateMessage
            {
                Text = "delta",
                GenerationId = "g",
                MessageOrderIdx = 99,
            },
        ]);
        agent.Restore([
            new TextMessage
            {
                Text = "second",
                GenerationId = "g",
                MessageOrderIdx = 0,
            },
            new TextMessage { Text = "unordered", GenerationId = "g" },
            new ToolCallResultMessage
            {
                ToolCallId = "call",
                Result = "done",
                GenerationId = "g",
                MessageOrderIdx = 1,
            },
            new TextMessage { Text = "no generation", MessageOrderIdx = 2 },
            new TextMessage
            {
                Text = "max",
                GenerationId = "m",
                MessageOrderIdx = int.MaxValue,
            },
            new TextMessage
            {
                Text = "after max",
                GenerationId = "m",
                MessageOrderIdx = 0,
            },
            new TextMessage
            {
                Text = "next",
                GenerationId = "m",
                MessageOrderIdx = 0,
            },
        ]);

        agent
            .GetHistorySnapshot()
            .Select(m => m.MessageOrderIdx)
            .Should()
            .Equal(4, 0, 5, 6, null, 7, 2, int.MaxValue, 0, 1);
    }

    private sealed record PausingToolResult(Action BeforeDelivery) : ToolCallResultMessage, IMessage
    {
        Role IMessage.Role
        {
            get
            {
                BeforeDelivery();
                return Role;
            }
        }
    }

    private sealed record CountingTextMessage(Action OnGenerationRead) : TextMessage, IMessage
    {
        string? IMessage.GenerationId
        {
            get
            {
                OnGenerationRead();
                return GenerationId;
            }
        }
    }

    private sealed class HistoryAgent(int capacity = 100, int replayCapacity = 100)
        : MultiTurnAgentBase("history-test", outputChannelCapacity: capacity, maxReplayBufferSize: replayCapacity)
    {
        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public void Append(IMessage message) => AddToHistory(message);

        public void Restore(IReadOnlyList<IMessage> messages) => RestoreHistory(messages);

        public void Replace(ToolCallResultMessage message) =>
            UpdateToolResultByCallId(message.ToolCallId!, _ => message);

        public Task CompleteAsync() => CompleteRunAsync("run", "gen");

        public ValueTask PublishAsync(IMessage message) => PublishToAllAsync(message, CancellationToken.None);
    }

    private static async Task<(List<IMessage> Joined, List<IMessage> Raw, IReadOnlyList<IMessage> History)> RunAsync(
        IReadOnlyList<IReadOnlyList<IMessage>> turns,
        FunctionRegistry? registry = null
    )
    {
        var provider = new Mock<IStreamingAgent>();
        var turn = 0;
        provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, options, ct) =>
                    Task.FromResult(Stream(turns[turn++].Select(message => message.WithIds(options)), ct))
            );
        await using var loop = new MultiTurnAgentLoop(
            provider.Object,
            registry ?? new FunctionRegistry(),
            "joined-test"
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = loop.RunAsync(cts.Token);
        IReadOnlyList<IMessage> history = [];
        var joined = DrainAsync(
            loop.SubscribeAsync(SubscribeOptions.Joined, cts.Token),
            () => history = loop.GetHistorySnapshot(),
            cts.Token
        );
        var raw = DrainAsync(loop.SubscribeAsync(cts.Token), () => { }, cts.Token);
        await loop.SendAsync([new TextMessage { Text = "Go", Role = Role.User }], ct: cts.Token);
        await Task.WhenAll(joined, raw);
        await cts.CancelAsync();
        await run;
        return (joined.Result, raw.Result, history);
    }

    private static async Task<List<IMessage>> DrainAsync(
        IAsyncEnumerable<IMessage> source,
        Action completed,
        CancellationToken ct
    )
    {
        var result = new List<IMessage>();
        await foreach (var message in source.WithCancellation(ct))
        {
            result.Add(message);
            if (message is RunCompletedMessage)
            {
                completed();
                break;
            }
        }

        return result;
    }

    private static async IAsyncEnumerable<IMessage> Stream(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
