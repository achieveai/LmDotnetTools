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
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// What an operator reads about a compacting loop (spec 679 §5.8): a run refused for size carries the typed reason as a
/// machine-readable error code, and the context observation shows the same request estimate the policy decided on.
/// </summary>
public sealed class CompactionObservabilityTests
{
    private const string Thread = "thread-observability";
    private const string Model = "test-model";

    [Fact]
    public async Task ASizeRefusal_CompletesTheRunWithTheTypedReasonAsItsErrorCode()
    {
        // A pasted instruction far larger than the usable window: the current instruction is never cut or trimmed,
        // so no legal view fits and the loop refuses to send.
        await using var h = new Harness(_ => [new TextMessage { Text = "done", Role = Role.Assistant }], window: 2_000);

        var completed = await h.RunAsync(new string('p', 36_000));

        completed.IsError.Should().BeTrue();
        h.Agent.Requests.Should().BeEmpty("the over-window request is never sent");
        // Which size reason the policy picks is the fit check's contract; this pins that the run names the typed reason
        // the refusal carried (the exception message leads with it) on the message and the lifecycle stream alike.
        completed
            .ErrorCode.Should()
            .BeOneOf(CompactionReasons.ViewExceedsWindow, CompactionFailureReasons.OverflowAfterCompaction);
        completed.ErrorMessage.Should().StartWith(completed.ErrorCode + ":");
        h.Publisher.Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted)
            .Should()
            .ContainSingle()
            .Which.Error!.Code.Should()
            .Be(completed.ErrorCode, "the lifecycle stream and the run message name one reason");
    }

    [Fact]
    public async Task TheEstimatedObservation_IsThePolicysEstimateOfTheSameRequest()
    {
        // The second request carries an encrypted reasoning blob (no readable text) and every request carries the
        // tool definitions. The policy counts the definitions, skips the blob and calibrates to the first reply's
        // measured count; the observation must show that same number, not a second estimator's.
        await using var h = new Harness(
            call =>
                call == 1
                    ?
                    [
                        new UsageMessage
                        {
                            Usage = new Usage { PromptTokens = 30_616, CompletionTokens = 10 },
                        },
                        new ReasoningMessage
                        {
                            Reasoning = new string('e', 8_000),
                            Visibility = ReasoningVisibility.Encrypted,
                            Role = Role.Assistant,
                        },
                        new TextMessage { Text = "first", Role = Role.Assistant },
                    ]
                    : [new TextMessage { Text = "second", Role = Role.Assistant }],
            window: 200_000
        );

        (await h.RunAsync("one")).IsError.Should().BeFalse();
        (await h.RunAsync("two")).IsError.Should().BeFalse();

        h.Agent.Requests[1]
            .SelectMany(m => m is CompositeMessage composite ? composite.Messages : [m])
            .OfType<ReasoningMessage>()
            .Should()
            .Contain(m => m.Visibility == ReasoningVisibility.Encrypted, "the fixture must send the blob");
        var decided = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionDecided);
        var observed = h
            .Publisher.Payloads<ContextMeasuredPayload>(LifecycleEventTypes.ContextMeasured)
            .Where(p => p.Provenance == nameof(MeasurementProvenance.Estimated))
            .ToList();
        observed.Should().HaveCount(2);
        foreach (var observation in observed)
        {
            decided
                .Should()
                .ContainSingle(d => d.GenerationId == observation.GenerationId)
                .Which.Tokens.Should()
                .Be(observation.EstimatedInputTokens, "generation {0}", observation.GenerationId);
        }
    }

    [Fact]
    public async Task TheMeasuredObservation_KeepsTheProvidersCount_AndThePolicysEstimate()
    {
        await using var h = new Harness(
            _ =>
                [
                    new UsageMessage
                    {
                        Usage = new Usage { PromptTokens = 30_616, CompletionTokens = 10 },
                    },
                    new TextMessage { Text = "done", Role = Role.Assistant },
                ],
            window: 200_000
        );

        (await h.RunAsync("one")).IsError.Should().BeFalse();

        var decided = h.Publisher.Payloads<CompactionPayload>(LifecycleEventTypes.CompactionDecided).Single();
        var measured = h
            .Publisher.Payloads<ContextMeasuredPayload>(LifecycleEventTypes.ContextMeasured)
            .Should()
            .ContainSingle(p => p.Provenance == nameof(MeasurementProvenance.Measured))
            .Subject;
        measured.MeasuredInputTokens.Should().Be(30_616);
        measured.EstimatedInputTokens.Should().Be(decided.Tokens);
    }

    private sealed class ScriptedAgent(Func<int, IReadOnlyList<IMessage>> script) : IStreamingAgent
    {
        public List<IReadOnlyList<IMessage>> Requests { get; } = [];

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add([.. messages]);
            return Task.FromResult(Stream([.. script(Requests.Count).Select(m => m.WithIds(options))]));
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
            IEnumerable<IMessage> messages,
            GenerateReplyOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        private static async IAsyncEnumerable<IMessage> Stream(
            IReadOnlyList<IMessage> messages,
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

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
        private readonly Task _runTask;

        public Harness(Func<int, IReadOnlyList<IMessage>> script, long window)
        {
            Agent = new ScriptedAgent(script);
            var registry = new FunctionRegistry().AddFunction(
                new FunctionContract
                {
                    Name = "Lookup",
                    Description = "Looks a value up by key in the operator's reference table",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "key",
                            Description = "The key to look up",
                            ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                            IsRequired = true,
                        },
                    ],
                },
                (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("value"))
            );
            Loop = new MultiTurnAgentLoop(
                Agent,
                registry,
                Thread,
                includeAskUserQuestionTool: false,
                includeNotifyClientTool: false,
                defaultOptions: new GenerateReplyOptions { ModelId = Model, MaxToken = 100 },
                store: new InMemoryConversationStore(),
                lifecycleServices: new MultiTurnLifecycleServices { Publisher = Publisher },
                compaction: new CompactionSetup
                {
                    Options = new CompactionOptions
                    {
                        Mode = CompactionMode.Compact,
                        ReserveMarginTokens = 0,
                        CooldownGenerations = 0,
                        CooldownNewTokens = 0,
                        CacheTtl = TimeSpan.Zero,
                    },
                    Summarizer = Mock.Of<ICheckpointSummarizer>(),
                    ResolveWindowTokens = _ => window + (Loop?.ToolSchemaTokens ?? 0),
                }
            );
            _runTask = Loop.RunAsync(_cts.Token);
        }

        public ScriptedAgent Agent { get; }

        public RecordingLifecyclePublisher Publisher { get; } = new();

        public MultiTurnAgentLoop Loop { get; }

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
}
