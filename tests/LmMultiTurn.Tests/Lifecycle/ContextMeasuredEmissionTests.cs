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
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Lifecycle;

/// <summary>
///     Per-generation context observation in the live loop (#681; spec 679 §4.1–4.2): every generation is
///     observed twice — estimated before dispatch, measured once the provider reports usage — persisted
///     under the thread's metadata, published as <c>context_measured</c>, and broadcast as a transient
///     <c>context_pressure</c> frame to the loop's subscribers.
/// </summary>
public sealed class ContextMeasuredEmissionTests
{
    private const string Thread = "ctx-thread";
    private readonly Mock<IStreamingAgent> _mockAgent = new();

    [Fact]
    public async Task AGeneration_IsObservedEstimatedThenMeasured_AndPersistedOncePerGeneration()
    {
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 1_000, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        await using var loop = CreateLoop(store, publisher, new FixedCapacity(200_000, 8_192));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var frames = new List<ContextPressureMessage>();
        _ = Task.Run(
            async () =>
            {
                await foreach (var msg in loop.SubscribeAsync(cts.Token))
                {
                    if (msg is ContextPressureMessage frame)
                    {
                        lock (frames)
                        {
                            frames.Add(frame);
                        }
                    }
                }
            },
            cts.Token
        );
        await Task.Delay(100, cts.Token);

        await DrainRunAsync(loop, cts.Token);

        var payloads = publisher.Payloads<ContextMeasuredPayload>(LifecycleEventTypes.ContextMeasured);
        payloads.Should().HaveCount(2);
        payloads[0].Provenance.Should().Be(nameof(MeasurementProvenance.Estimated));
        payloads[0].EstimatedInputTokens.Should().BeGreaterThan(0);
        payloads[0].MeasuredInputTokens.Should().BeNull();
        payloads[0].WindowTokens.Should().Be(200_000);
        payloads[0].ReserveTokens.Should().Be(4_000, "the loop's own output budget is the reserve");
        payloads[0].AgentId.Should().Be(AgentExecutionRef.RootAgentId);
        payloads[0].EffectiveModelId.Should().Be("model-x");
        payloads[1].Provenance.Should().Be(nameof(MeasurementProvenance.Measured));
        payloads[1].MeasuredInputTokens.Should().Be(1_000);
        payloads[1].Utilization.Should().BeApproximately(1_000d / (200_000 - 4_000), 1e-9);
        payloads[1].GenerationId.Should().Be(payloads[0].GenerationId);
        payloads[1].GenerationOrdinal.Should().Be(payloads[0].GenerationOrdinal);
        publisher.CorrelationsFor(LifecycleEventTypes.ContextMeasured).Should().OnlyContain(c => c.ThreadId == Thread);

        var latest = await ContextObservationProjection.LoadLatestAsync(store, Thread);
        latest.Should().NotBeNull();
        latest!.Provenance.Should().Be(MeasurementProvenance.Measured);
        latest.MeasuredInputTokens.Should().Be(1_000);
        latest.GenerationOrdinal.Should().Be(1);
        latest.PromptCachingEnabled.Should().BeTrue();
        latest.RowsInView.Should().BeGreaterThan(0);
        (await ContextObservationProjection.LoadHistoryAsync(store, Thread))
            .Should()
            .ContainSingle("estimated and measured are one generation, so one ring entry");

        loop.LatestContextObservation.Should().BeEquivalentTo(latest);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            lock (frames)
            {
                if (frames.Any(f => f.Provenance == nameof(MeasurementProvenance.Measured)))
                {
                    break;
                }
            }

            await Task.Delay(20, cts.Token);
        }

        lock (frames)
        {
            var measured = frames.Should().Contain(f => f.Provenance == nameof(MeasurementProvenance.Measured)).Subject;
            measured.ThreadId.Should().Be(Thread);
            measured.MeasuredInputTokens.Should().Be(1_000);
            measured.WindowTokens.Should().Be(200_000);
        }

        await cts.CancelAsync();
    }

    [Fact]
    public async Task WithoutACapacityResolver_TheObservationIsStillPersisted_WithNoWindow()
    {
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 1_000, CompletionTokens = 40 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            Thread,
            store: store,
            defaultOptions: new GenerateReplyOptions { ModelId = "model-x" }
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);
        await DrainRunAsync(loop, cts.Token);

        var latest = await ContextObservationProjection.LoadLatestAsync(store, Thread);
        latest.Should().NotBeNull();
        latest!.WindowTokens.Should().BeNull();
        latest.Utilization.Should().BeNull("an unknown window reads as unknown, never as a number");
        latest.MeasuredInputTokens.Should().Be(1_000);
        latest.PromptCachingEnabled.Should().BeFalse();

        await cts.CancelAsync();
    }

    [Fact]
    public async Task GenerationOrdinals_ContinueAcrossARestart()
    {
        SetupMockAgentResponse([
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 10, CompletionTokens = 4 },
                GenerationId = "gen-1",
            },
            new TextMessage { Text = "done", Role = Role.Assistant },
        ]);

        var store = new InMemoryConversationStore();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var first = CreateLoop(store, new RecordingLifecyclePublisher(), new FixedCapacity(100_000, null));
        _ = first.RunAsync(cts.Token);
        await DrainRunAsync(first, cts.Token);
        await first.DisposeAsync();
        (await ContextObservationProjection.LoadLatestAsync(store, Thread))!.GenerationOrdinal.Should().Be(1);

        await using var second = CreateLoop(store, new RecordingLifecyclePublisher(), new FixedCapacity(100_000, null));
        _ = second.RunAsync(cts.Token);
        await DrainRunAsync(second, cts.Token);

        (await ContextObservationProjection.LoadLatestAsync(store, Thread))!.GenerationOrdinal.Should().Be(2);
        (await ContextObservationProjection.LoadHistoryAsync(store, Thread)).Should().HaveCount(2);

        await cts.CancelAsync();
    }

    private MultiTurnAgentLoop CreateLoop(
        IConversationStore store,
        RecordingLifecyclePublisher publisher,
        IModelCapacityResolver capacity
    ) =>
        new(
            _mockAgent.Object,
            new FunctionRegistry(),
            Thread,
            store: store,
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = "model-x",
                MaxToken = 4_000,
                PromptCaching = PromptCachingMode.Auto,
            },
            lifecycleServices: new MultiTurnLifecycleServices { Publisher = publisher, CapacityResolver = capacity }
        );

    private static async Task DrainRunAsync(MultiTurnAgentLoop loop, CancellationToken ct)
    {
        var userInput = new UserInput(
            [new TextMessage { Text = "Hi", Role = Role.User }],
            InputId: Guid.NewGuid().ToString("N")
        );
        await foreach (var _ in loop.ExecuteRunAsync(userInput, ct))
        {
            // drain the run to completion
        }
    }

    private void SetupMockAgentResponse(List<IMessage> messages)
    {
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable(messages)));
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

    private sealed class FixedCapacity(long window, long? maxOutput) : IModelCapacityResolver
    {
        public ModelCapacity? Resolve(string modelId) => new(window, maxOutput);
    }
}
