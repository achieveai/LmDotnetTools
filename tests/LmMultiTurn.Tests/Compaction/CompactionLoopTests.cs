using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// The just-in-time policy inside <see cref="MultiTurnAgentLoop"/> (#684, spec 679 §5): evaluated
/// immediately before each provider call and nowhere else, in every mode, on the reactive path and
/// under the kill switch. Sizes are chosen so the arithmetic is visible: every tool result is
/// <see cref="ResultTokens"/> tokens, the window is <see cref="Window"/>, the reserve is the loop's
/// <c>MaxToken</c> (100) with no margin, so the usable window is 2300, warn is 1610, compact is 1840
/// and the hard row fires at 2060 request tokens.
/// </summary>
public class CompactionLoopTests
{
    private const string Thread = "thread-compaction";
    private const string Model = "test-model";
    private const long Window = 2_400;
    private const long Usable = Window - 100;
    private const long ResultTokens = 312; // 1200 chars / 4 + 12 overhead
    private static readonly string Padding = new('x', 1_200);

    private sealed class ScriptedAgent(Func<int, IReadOnlyList<IMessage>> script) : IStreamingAgent
    {
        public List<IReadOnlyList<IMessage>> Requests { get; } = [];

        /// <summary>The tool names offered on each call, in call order.</summary>
        public List<IReadOnlyList<string>> FunctionNames { get; } = [];

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var request = messages.ToList();
            Requests.Add(request);
            FunctionNames.Add([.. (options?.Functions ?? []).Select(f => f.Name)]);
            var reply = script(Requests.Count).Select(m => m.WithIds(options)).ToList();
            return Task.FromResult(Stream(reply));
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        private static async IAsyncEnumerable<IMessage> Stream(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken ct = default
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

    /// <summary>Quotes the current instruction whole and headlines every run: always passes V1–V9.</summary>
    private sealed class EchoSummarizer : ICheckpointSummarizer
    {
        public List<CheckpointSummaryRequest> Requests { get; } = [];

        public Func<CheckpointSummaryRequest, Exception?> Fail { get; set; } = _ => null;

        public Task<CheckpointSummaryResponse> SummarizeAsync(
            CheckpointSummaryRequest request,
            CancellationToken ct = default
        )
        {
            Requests.Add(request);
            if (Fail(request) is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(
                new CheckpointSummaryResponse(
                    new CheckpointSummary
                    {
                        Instructions =
                        [
                            .. request.CurrentInstruction.Select(r => new QuotedItem
                            {
                                Seq = r.Seq,
                                Quote = r.Text ?? string.Empty,
                            }),
                        ],
                        Headlines = request.RunIds.ToDictionary(id => id, _ => "ran the tool", StringComparer.Ordinal),
                        Narrative = $"Summarised {request.Rows.Count} rows.",
                    },
                    new UsageMessage
                    {
                        Usage = new Usage
                        {
                            PromptTokens = 50,
                            CompletionTokens = 10,
                            TotalTokens = 60,
                        },
                    }
                )
            );
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Task _runTask;

        public Harness(
            Func<int, IReadOnlyList<IMessage>> script,
            CompactionOptions options,
            Func<string?, long?>? window = null,
            string? killSwitch = null,
            InMemoryConversationStore? store = null,
            SubAgentOptions? subAgentOptions = null
        )
        {
            Agent = new ScriptedAgent(script);
            Store = store ?? new InMemoryConversationStore();
            var registry = new FunctionRegistry().AddFunction(
                new FunctionContract
                {
                    Name = "Echo",
                    Description = "Returns padding",
                    Parameters = [],
                },
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(Padding))
            );
            Setup = new CompactionSetup
            {
                Options = options,
                Summarizer = Summarizer,
                ResolveWindowTokens = window,
                Clock = Clock,
                ReadEnvironment = _ => KillSwitch,
                IsContextOverflow = ex => ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest },
            };
            KillSwitch = killSwitch;
            Loop = new MultiTurnAgentLoop(
                Agent,
                registry,
                Thread,
                includeAskUserQuestionTool: false,
                includeNotifyClientTool: false,
                defaultOptions: new GenerateReplyOptions { ModelId = Model, MaxToken = 100 },
                store: Store,
                lifecycleServices: new MultiTurnLifecycleServices { Publisher = Publisher },
                subAgentOptions: subAgentOptions,
                compaction: Setup
            );
            _runTask = Loop.RunAsync(_cts.Token);
        }

        public ScriptedAgent Agent { get; }

        public InMemoryConversationStore Store { get; }

        public EchoSummarizer Summarizer { get; } = new();

        public RecordingLifecyclePublisher Publisher { get; } = new();

        public FixedClock Clock { get; } = new();

        public CompactionSetup Setup { get; }

        public MultiTurnAgentLoop Loop { get; }

        public string? KillSwitch { get; set; }

        public IReadOnlyList<CompactionPayload> Decisions =>
            Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionDecided);

        public async Task<RunCompletedMessage> RunAsync(string text)
        {
            RunCompletedMessage? completed = null;
            await foreach (
                var message in Loop.ExecuteRunAsync(
                    new UserInput([new TextMessage { Text = text, Role = Role.User }]),
                    _cts.Token
                )
            )
            {
                if (message is RunCompletedMessage done)
                {
                    completed = done;
                }
            }

            return completed ?? throw new InvalidOperationException("run never completed");
        }

        public async Task<IReadOnlyList<IMessage>> StoredRowsAsync() =>
            MessagePersistenceConverter.FromPersistedMessagesResilient(await Store.LoadMessagesAsync(Thread));

        public async Task<CompactionState?> StateAsync() => await CompactionStateProjection.LoadAsync(Store, Thread);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException) { }

            await Loop.DisposeAsync();
            _cts.Dispose();
        }
    }

    private static CompactionOptions Options(CompactionMode mode) =>
        new()
        {
            Mode = mode,
            ReserveMarginTokens = 0,
            MinTailTokens = 100,
            CooldownGenerations = 0,
            CooldownNewTokens = 0,
            CacheTtl = TimeSpan.Zero,
        };

    /// <summary>Calls Echo for the first <paramref name="toolCalls"/> requests, then answers with text.</summary>
    private static Func<int, IReadOnlyList<IMessage>> EchoThenDone(int toolCalls, string done = "done") =>
        call =>
            call <= toolCalls
                ?
                [
                    new ToolCallMessage
                    {
                        ToolCallId = $"tc-{call}",
                        FunctionName = "Echo",
                        FunctionArgs = "{}",
                        Role = Role.Assistant,
                    },
                ]
                : [new TextMessage { Text = done, Role = Role.Assistant }];

    /// <summary>
    /// The provider sees tool turns joined into <see cref="ToolsCallAggregateMessage"/> by the loop's
    /// middleware; the estimator measures the split rows the history holds, so expand them first.
    /// </summary>
    private static long Estimate(IReadOnlyList<IMessage> request) =>
        CompactionRuntime.EstimateTokens([
            .. request.SelectMany<IMessage, IMessage>(m =>
                m is ToolsCallAggregateMessage agg ? [agg.ToolsCallMessage, agg.ToolsCallResult] : [m]
            ),
        ]);

    private static bool HasEnvelope(IReadOnlyList<IMessage> request) =>
        request.Any(m =>
            m is TextMessage { Role: Role.User } t && t.Text.Contains("RecallConversation", StringComparison.Ordinal)
        );

    [Fact]
    public async Task WarnMode_RecordsDecisions_AndNeverChangesTheProviderInput()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Warn), _ => Window);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().BeEmpty();
        h.Agent.Requests.Should().HaveCount(8);
        h.Agent.Requests.Select(r => r.Count).Should().BeInAscendingOrder("the raw history is what the provider sees");
        h.Agent.Requests.Should().OnlyContain(r => !HasEnvelope(r));
        h.Decisions.Should().HaveCount(8, "one decision per provider call");
        h.Decisions.Select(d => d.Decision).Should().Contain(CompactionDecisionKinds.Warn);
        h.Decisions.Last().Utilization.Should().BeGreaterThan(0.8);
        h.Decisions.Last().Decision.Should().Be(CompactionDecisionKinds.Warn, "warn mode caps every row at a warning");
        (await h.StoredRowsAsync()).Should().NotContain(m => m is CompactionCheckpointMessage);
        (await ContextObservationProjection.LoadLatestAsync(h.Store, Thread))!.Decision!.Decision.Should().Be("warn");
    }

    [Fact]
    public async Task ShadowMode_BuildsAndValidates_ButAppendsNoRowAndSendsTheRawInput()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Shadow), _ => Window);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().NotBeEmpty("shadow runs the summary pass");
        h.Agent.Requests.Should().OnlyContain(r => !HasEnvelope(r));
        h.Agent.Requests.Select(r => r.Count).Should().BeInAscendingOrder();
        h.Decisions.Select(d => d.Decision).Should().Contain(CompactionDecisionKinds.Shadow);
        h.Publisher.EventTypes.Should().NotContain(LifecycleEventTypes.CompactionApplied);
        (await h.StoredRowsAsync()).Should().NotContain(m => m is CompactionCheckpointMessage);
        var state = await h.StateAsync();
        state!.ActiveCheckpointId.Should().BeNull();
        state
            .History.Should()
            .Contain(e => e.Trigger == CompactionTrigger.Shadow && e.Status == CheckpointStatus.Rejected);
    }

    [Fact]
    public async Task CompactMode_ActivatesACheckpoint_AndSendsTheEnvelopeView()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().HaveCount(1);
        h.Summarizer.Requests[0].ModelId.Should().Be(Model, "the summary model defaults to the loop's model (Q2)");
        var applied = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied).Single();
        applied.Decision.Should().Be(CompactionDecisionKinds.Compact);
        applied.Reason.Should().Be(CompactionPolicy.EconomicReason);
        applied.CheckpointId.Should().NotBeNullOrEmpty();
        applied.BoundarySeq.Should().BePositive();

        var first = h.Agent.Requests.FindIndex(HasEnvelope);
        first.Should().BePositive("the first requests go out raw");
        applied.Tokens.Should().BeGreaterThanOrEqualTo((long)(0.8 * Usable), "the policy measured the raw request");
        Estimate(h.Agent.Requests[first]).Should().BeLessThan(applied.Tokens, "the view replaced the raw request");
        h.Agent.Requests[first].Count.Should().BeLessThan(h.Agent.Requests[first - 1].Count);
        h.Agent.Requests.Skip(first)
            .Should()
            .OnlyContain(r => HasEnvelope(r), "the view stays in force once activated");

        var rows = await h.StoredRowsAsync();
        var checkpoint = rows.OfType<CompactionCheckpointMessage>().Single();
        checkpoint.CheckpointId.Should().Be(applied.CheckpointId);
        checkpoint.Trigger.Should().Be(CompactionTrigger.Preemptive);
        var state = await h.StateAsync();
        state!.ActiveCheckpointId.Should().Be(applied.CheckpointId);
        state.ActiveBoundarySeq.Should().Be(applied.BoundarySeq);
    }

    [Fact]
    public async Task ReactiveOverflow_CompactsThenRetriesTheSameInputOnce()
    {
        var overflowed = 0;
        await using var h = new Harness(
            call =>
            {
                if (call == 7 && overflowed++ == 0)
                {
                    throw new HttpRequestException("context too long", null, HttpStatusCode.BadRequest);
                }

                return EchoThenDone(6)(call);
            },
            Options(CompactionMode.Compact),
            window: null
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().HaveCount(1);
        h.Agent.Requests.Should().HaveCount(8, "seven raw requests, one retry on the view");
        HasEnvelope(h.Agent.Requests[6]).Should().BeFalse();
        HasEnvelope(h.Agent.Requests[7]).Should().BeTrue();
        h.Decisions.Take(7).Should().OnlyContain(d => d.Reason == CompactionSkipReasons.CapacityUnknown);
        var applied = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied).Single();
        applied.Trigger.Should().Be("reactive");
        (await h.StoredRowsAsync())
            .OfType<CompactionCheckpointMessage>()
            .Single()
            .Trigger.Should()
            .Be(CompactionTrigger.Reactive);
    }

    [Fact]
    public async Task SecondOverflowAfterCompaction_FailsTheRunWithTheTypedReason()
    {
        await using var h = new Harness(
            call =>
                call >= 7
                    ? throw new HttpRequestException("context too long", null, HttpStatusCode.BadRequest)
                    : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            window: null
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().StartWith(CompactionFailureReasons.OverflowAfterCompaction);
        h.Summarizer.Requests.Should().HaveCount(1, "the reactive compaction runs once per run");
        h.Agent.Requests.Should().HaveCount(8, "one retry, never a third attempt");
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Should()
            .Contain(p => p.Reason == CompactionFailureReasons.OverflowAfterCompaction);
    }

    [Fact]
    public async Task TransportAbort_IsNotAnOverflow_AndNeverCompacts()
    {
        await using var h = new Harness(
            call => call == 7 ? throw new HttpRequestException("connection reset") : EchoThenDone(6)(call),
            Options(CompactionMode.Compact),
            window: null
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().NotContain(CompactionFailureReasons.OverflowAfterCompaction);
        h.Summarizer.Requests.Should().BeEmpty("Q1: a transport abort is not evidence of overflow");
    }

    [Fact]
    public async Task WhenCompactionFails_TheLoopNeverSendsBeyondTheReserve()
    {
        await using var h = new Harness(EchoThenDone(9), Options(CompactionMode.Compact), _ => Window);
        h.Summarizer.Fail = _ => new InvalidOperationException("summary model down");

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeTrue();
        completed.ErrorMessage.Should().StartWith(CompactionFailureReasons.OverflowAfterCompaction);
        h.Agent.Requests.Should()
            .OnlyContain(r => Estimate(r) <= Usable, "AC 7: never knowingly send beyond the reserve");
        h.Summarizer.Requests.Should().NotBeEmpty("the loop tried to compact before refusing");
        h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionFailed)
            .Select(p => p.Reason)
            .Should()
            .Contain(CompactionReasons.SummaryCallFailed)
            .And.Contain(CompactionFailureReasons.OverflowAfterCompaction);
        (await h.StoredRowsAsync()).Should().NotContain(m => m is CompactionCheckpointMessage);
    }

    [Fact]
    public async Task KillSwitch_SkipsDisabled_AndRollsTheActiveCheckpointBackOnTheNextRequest()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window);
        (await h.RunAsync("start")).IsError.Should().BeFalse();
        (await h.StateAsync())!.ActiveCheckpointId.Should().NotBeNull();
        var requestsBefore = h.Agent.Requests.Count;

        h.KillSwitch = "1";
        (await h.RunAsync("again")).IsError.Should().BeFalse();

        var afterKill = h.Decisions.Skip(requestsBefore).ToList();
        afterKill.Should().NotBeEmpty();
        afterKill
            .Should()
            .OnlyContain(d =>
                d.Decision == CompactionDecisionKinds.Skipped && d.Reason == CompactionSkipReasons.Disabled
            );
        var state = await h.StateAsync();
        state!.ActiveCheckpointId.Should().BeNull("§8.4: Active → RolledBack on the next request");
        state
            .History.Should()
            .Contain(e => e.Status == CheckpointStatus.RolledBack && e.Reason == CompactionFailureReasons.Killed);
        h.Agent.Requests.Skip(requestsBefore)
            .Should()
            .OnlyContain(r => !HasEnvelope(r), "the raw history is sent again");
    }

    [Fact]
    public async Task ProviderOwnedSession_IsSkipped_WithoutTouchingTheThread()
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            Thread,
            new ThreadMetadata
            {
                ThreadId = Thread,
                LastUpdated = 0,
                SessionMappings = new Dictionary<string, string> { ["claude"] = "session-1" },
            }
        );
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Compact), _ => Window, store: store);

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        h.Decisions.Should().OnlyContain(d => d.Reason == CompactionSkipReasons.ProviderOwnedSession);
        h.Summarizer.Requests.Should().BeEmpty();
        h.Agent.Requests.Should().OnlyContain(r => !HasEnvelope(r));
    }

    [Fact]
    public async Task NoBackgroundJob_AThresholdCrossedAtTheEndOfARunWaitsForTheNextRequest()
    {
        // Five tool turns leave the history under the compact threshold; the closing reply pushes it
        // over. Nothing may happen until the next request arrives, however long the thread sits idle.
        await using var h = new Harness(
            EchoThenDone(5, done: new string('y', 4_000)),
            Options(CompactionMode.Compact),
            _ => Window
        );

        (await h.RunAsync("start")).IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().BeEmpty();
        var decisionsAfterRunOne = h.Decisions.Count;

        h.Clock.Now += TimeSpan.FromHours(1);
        await Task.Delay(200);
        h.Summarizer.Requests.Should().BeEmpty("no timer, no inactivity job");
        h.Decisions.Should().HaveCount(decisionsAfterRunOne);
        (await h.StoredRowsAsync()).Should().NotContain(m => m is CompactionCheckpointMessage);

        (await h.RunAsync("continue")).IsError.Should().BeFalse();
        h.Summarizer.Requests.Should().HaveCount(1, "the first request of the next run is where the policy runs");
        HasEnvelope(h.Agent.Requests[decisionsAfterRunOne]).Should().BeTrue();
    }

    [Fact]
    public async Task ModeOff_LeavesTheLoopByteIdentical()
    {
        await using var h = new Harness(EchoThenDone(7), Options(CompactionMode.Off), _ => Window);

        (await h.RunAsync("start")).IsError.Should().BeFalse();

        h.Decisions.Should().BeEmpty();
        h.Publisher.EventTypes.Should().NotContain(t => t.StartsWith("compaction_", StringComparison.Ordinal));

        // #681 observes every generation whether or not compaction runs, so the absence to assert here is
        // the decision stamped on that observation (§5.5), not the observation itself.
        var observed = await ContextObservationProjection.LoadLatestAsync(h.Store, Thread);
        observed.Should().NotBeNull("#681 measures every generation regardless of compaction mode");
        observed!.Decision.Should().BeNull("Off evaluates no policy, so it stamps no decision");
    }

    [Fact]
    public async Task RecallTool_IsOfferedInWarnMode_NeverInOff_AndAnswersNothingCompactedWithoutACheckpoint()
    {
        await using var warn = new Harness(
            call =>
                call == 1
                    ?
                    [
                        new ToolCallMessage
                        {
                            ToolCallId = "recall-1",
                            FunctionName = RecallConversationToolProvider.ToolName,
                            FunctionArgs = """{"query":"anything"}""",
                            Role = Role.Assistant,
                        },
                    ]
                    : [new TextMessage { Text = "done", Role = Role.Assistant }],
            Options(CompactionMode.Warn),
            _ => Window
        );
        await using var off = new Harness(EchoThenDone(1), Options(CompactionMode.Off), _ => Window);

        (await warn.RunAsync("start")).IsError.Should().BeFalse();
        (await off.RunAsync("start")).IsError.Should().BeFalse();

        warn.Agent.FunctionNames.Should().OnlyContain(names => names.Contains(RecallConversationToolProvider.ToolName));
        off.Agent.FunctionNames.Should().OnlyContain(names => !names.Contains(RecallConversationToolProvider.ToolName));
        var answer = (await warn.StoredRowsAsync())
            .OfType<ToolCallResultMessage>()
            .Single(r => r.ToolCallId == "recall-1");
        answer.Result.Should().Contain(RecallConversationToolProvider.NothingCompacted);
    }

    [Fact]
    public async Task RecallRoundTrip_ReturnsACompactedRowVerbatim_AsAnOrdinaryTailRow()
    {
        // Corpus (j): after the checkpoint activates, the model recalls the compacted human row by
        // keyword; the answer is a normal tool result in the tail and the next request carries it.
        await using var h = new Harness(
            call =>
                call == 8
                    ?
                    [
                        new ToolCallMessage
                        {
                            ToolCallId = "recall-1",
                            FunctionName = RecallConversationToolProvider.ToolName,
                            FunctionArgs = """{"query":"start"}""",
                            Role = Role.Assistant,
                        },
                    ]
                    : EchoThenDone(7)(call),
            Options(CompactionMode.Compact),
            _ => Window
        );

        var completed = await h.RunAsync("start");

        completed.IsError.Should().BeFalse();
        var applied = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionApplied).Single();
        var persisted = await h.Store.LoadMessagesAsync(Thread);
        var answerRow = persisted.Single(p =>
            p.Id == MessagePersistenceConverter.BuildToolResultPersistedId(Thread, "recall-1")
        );
        answerRow.Seq.Should().BeGreaterThan(applied.BoundarySeq!.Value, "the answer is a tail row");
        var answer = (ToolCallResultMessage)MessagePersistenceConverter.FromPersistedMessage(answerRow);
        answer.Result.Should().Contain($"\"boundary_seq\":{applied.BoundarySeq}");
        answer.Result.Should().Contain("\"seq\":1").And.Contain("\"text\":\"start\"");

        var next = h.Agent.Requests[8];
        HasEnvelope(next).Should().BeTrue();
        next.OfType<ToolsCallAggregateMessage>()
            .Should()
            .Contain(a =>
                a.ToolsCallResult.ToolCallResults.Any(r => r.ToolCallId == "recall-1" && r.Result.Contains("\"seq\":1"))
            );
    }

    [Fact]
    public async Task ChildLoop_RegistersItsOwnRecallInstance_AndNeverInheritsTheParents()
    {
        var child = new ScriptedAgent(_ => [new TextMessage { Text = "child done", Role = Role.Assistant }]);
        var subAgents = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["echo"] = new SubAgentTemplate { SystemPrompt = "You echo.", AgentFactory = () => child },
            },
            DefaultConversationStoreFactory = _ => new InMemoryConversationStore(),
        };
        await using var h = new Harness(
            EchoThenDone(0),
            Options(CompactionMode.Warn),
            _ => Window,
            subAgentOptions: subAgents
        );

        var manager = h.Loop.SubAgentManager!;
        manager
            .GetInheritableToolSnapshot()
            .Contracts.Select(c => c.Name)
            .Should()
            .Contain("Echo")
            .And.NotContain(
                RecallConversationToolProvider.ToolName,
                "the parent's instance is bound to the parent's thread"
            );

        _ = await manager.SpawnAsync("echo", "say hi");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (child.FunctionNames.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        child.FunctionNames.Should().NotBeEmpty("the child ran a turn");
        child
            .FunctionNames[0]
            .Should()
            .Contain(RecallConversationToolProvider.ToolName, "the child registered its own instance");
        child.FunctionNames[0].Should().Contain("Echo", "domain tools are still inherited");
    }

    [Theory]
    [InlineData(typeof(ClaudeAgentLoop))]
    [InlineData(typeof(CodexAgentLoop))]
    [InlineData(typeof(CopilotAgentLoop))]
    public void ProviderOwnedLoops_HaveNoCompactionParameter(Type loop)
    {
        var parameters = loop.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        parameters
            .Should()
            .NotContain(typeof(CompactionSetup), "a provider-owned session is never compacted by the harness");
    }
}
