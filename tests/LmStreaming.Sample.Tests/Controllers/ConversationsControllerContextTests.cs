using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using LmStreaming.Sample.Identity;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
///     <c>GET /api/conversations/{threadId}/context</c> (#681): one row per agent — the root and every
///     persisted descendant — with its latest context observation, freshness, compaction state and usage,
///     plus the root cost total. Read from durable state, so it answers the same after a restart; the live
///     loop's observation wins only while the loop is pooled.
/// </summary>
public class ConversationsControllerContextTests
{
    private const string ThreadId = "ctx-root";
    private const string ChildThreadId = "subagent-agent-1";

    private static ContextObservation Observation(string threadId, string agentId, long ordinal, long? measured) =>
        new()
        {
            ThreadId = threadId,
            AgentId = agentId,
            RunId = "run-1",
            GenerationId = $"{threadId}-gen-{ordinal}",
            GenerationOrdinal = ordinal,
            ObservedAtUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
            EffectiveModelId = "model-x",
            EstimatedInputTokens = 1_000,
            MeasuredInputTokens = measured,
            Provenance = measured is null ? MeasurementProvenance.Estimated : MeasurementProvenance.Measured,
            WindowTokens = 200_000,
            ReserveTokens = 8_000,
            RowsInView = 3,
        };

    private static Task SeedRootAsync(IConversationStore store) =>
        store.UpdateMetadataAsync(
            ThreadId,
            existing =>
                existing
                ?? new ThreadMetadata
                {
                    ThreadId = ThreadId,
                    LastUpdated = 0,
                    TenantId = "tenant-1",
                }
        );

    private static Task SeedChildAsync(IConversationStore store) =>
        store.SaveMetadataAsync(
            ChildThreadId,
            new ThreadMetadata
            {
                ThreadId = ChildThreadId,
                LastUpdated = 0,
                TenantId = "tenant-1",
                Properties = SubAgentProvenance.Build(
                    ThreadId,
                    new SubAgentSnapshot(
                        "agent-1",
                        Name: "agent-1",
                        TemplateName: "worker",
                        Task: "task",
                        Status: SubAgentStatus.Completed,
                        ThreadId: ChildThreadId,
                        LastActivityUtc: DateTimeOffset.UtcNow,
                        TerminalAtUtc: DateTimeOffset.UtcNow
                    )
                ),
            }
        );

    private static async Task SeedUsageAsync(IConversationStore store)
    {
        var ledger = new UsageLedger(ThreadId);
        ledger.RecordUsage(
            UsageRecordMapper.FromUsageMessage(
                new UsageMessage
                {
                    Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                    GenerationId = "g1",
                },
                ThreadId,
                UsageExecutionKind.Primary,
                "model-x"
            )
        );
        ledger.RecordUsage(
            UsageRecordMapper.FromUsageMessage(
                new UsageMessage
                {
                    Usage = new Usage { PromptTokens = 200, CompletionTokens = 10 },
                    GenerationId = "g2",
                },
                ChildThreadId,
                UsageExecutionKind.SubAgent,
                "model-y"
            )
        );
        await ConversationUsageProjection.SaveAsync(
            store,
            ledger.Snapshot(UsageCompleteness.Complete),
            ledger.SnapshotRecords()
        );
    }

    private static ConversationsController ControllerFor(IConversationStore store, MultiTurnAgentPool pool) =>
        ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes()
        );

    [Fact]
    public async Task GetContext_ReportsTheRootAndEveryPersistedDescendant_FromDurableState()
    {
        var store = new InMemoryConversationStore();
        await SeedRootAsync(store);
        await SeedChildAsync(store);
        await SeedUsageAsync(store);
        await ContextObservationProjection.RecordAsync(store, Observation(ThreadId, "root", 2, measured: 5_000));
        await ContextObservationProjection.RecordAsync(store, Observation(ChildThreadId, "agent-1", 1, null));
        await using var pool = ConversationsControllerTests.CreatePool();

        var result = await ControllerFor(store, pool).GetContext(ThreadId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<ConversationContextReport>(ok.Value);
        report.RootThreadId.Should().Be(ThreadId);
        report
            .Agents.Select(a => (a.AgentId, a.ThreadId, a.ParentAgentId, a.ExecutionKind))
            .Should()
            .Equal(
                ("root", ThreadId, null, UsageExecutionKind.Primary),
                ("agent-1", ChildThreadId, "root", UsageExecutionKind.SubAgent)
            );

        var root = report.Agents[0];
        root.Observation!.MeasuredInputTokens.Should().Be(5_000);
        root.Observation.Utilization.Should().BeApproximately(5_000d / (200_000 - 8_000), 1e-9);
        root.Freshness.Should().Be(ContextFreshness.Stale, "no pooled loop vouched for it");
        root.Usage!.InputTokens.Should().Be(100);

        var child = report.Agents[1];
        child.Observation!.Provenance.Should().Be(MeasurementProvenance.Estimated);
        child.Usage!.InputTokens.Should().Be(200);

        report.Total.TotalTokens.Should().Be(350);
        report.Total.UsageCompleteness.Should().Be(UsageCompleteness.Complete);
        report.Total.CostCompleteness.Should().Be(CostCompleteness.Unavailable);
    }

    [Fact]
    public async Task GetContext_PrefersThePooledLoopsLiveObservation_AndReadsItFresh()
    {
        var store = new InMemoryConversationStore();
        await SeedRootAsync(store);
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
                Task.FromResult(
                    Stream(
                        new UsageMessage
                        {
                            Usage = new Usage { PromptTokens = 1_234, CompletionTokens = 4 },
                            GenerationId = "gen-1",
                        },
                        new TextMessage { Text = "done", Role = Role.Assistant }
                    )
                )
            );
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(
                    new MultiTurnAgentLoop(
                        agent.Object,
                        new FunctionRegistry(),
                        threadId,
                        store: store,
                        defaultOptions: new GenerateReplyOptions { ModelId = "model-x" }
                    )
                ),
            NullLogger<MultiTurnAgentPool>.Instance
        );
        var loop = (MultiTurnAgentLoop)
            pool.GetOrCreateAgent(ThreadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);
        await foreach (
            var _ in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "in-1"),
                cts.Token
            )
        )
        {
            // drain the generation so the loop has observed it
        }

        var result = await ControllerFor(store, pool).GetContext(ThreadId, cts.Token);

        var report = Assert.IsType<ConversationContextReport>(Assert.IsType<OkObjectResult>(result).Value);
        var root = report.Agents.Should().ContainSingle().Subject;
        root.Freshness.Should().Be(ContextFreshness.Fresh);
        root.Observation.Should().BeSameAs(loop.LatestContextObservation);
        root.Observation!.MeasuredInputTokens.Should().Be(1_234);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task GetContext_ReturnsTheExistenceHiding404_ForAThreadNobodyKnows()
    {
        var store = new InMemoryConversationStore();
        await using var pool = ConversationsControllerTests.CreatePool();

        var result = await ControllerFor(store, pool).GetContext("never-minted");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        notFound.Value.Should().BeEquivalentTo(UnknownThreadRefusal.Body("never-minted"));
    }

    private static async IAsyncEnumerable<IMessage> Stream(
        IMessage first,
        IMessage second,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        yield return first;
        await Task.Yield();
        yield return second;
    }
}
