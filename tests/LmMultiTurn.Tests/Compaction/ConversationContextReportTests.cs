using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
///     The read model behind <c>GET /api/conversations/{id}/context</c> (#681; spec 679 §4.1–4.5, §9): one
///     row per agent in the roster with its latest observation, freshness, cache temperature, compaction
///     state and usage, plus the root total — all from durable state, so it reads the same before and after
///     a restart.
/// </summary>
public sealed class ConversationContextReportTests : IAsyncLifetime
{
    private const string Root = "root-1";
    private const string Child = "subagent-agent-1";
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    private static IReadOnlyList<AgentExecutionRef> Roster() =>
        [
            AgentExecutionRef.Root(Root),
            new AgentExecutionRef(Root, Child, "agent-1", AgentExecutionRef.RootAgentId, UsageExecutionKind.SubAgent),
        ];

    private static ContextObservation Observation(
        string threadId,
        string agentId,
        long ordinal,
        long? measured = null,
        bool? caching = true
    ) =>
        new()
        {
            ThreadId = threadId,
            AgentId = agentId,
            RunId = "run-1",
            GenerationId = $"{threadId}-gen-{ordinal}",
            GenerationOrdinal = ordinal,
            ObservedAtUtc = T0.AddSeconds(ordinal),
            EffectiveModelId = "model-x",
            EstimatedInputTokens = 1_000 * ordinal,
            MeasuredInputTokens = measured,
            Provenance = measured is null ? MeasurementProvenance.Estimated : MeasurementProvenance.Measured,
            WindowTokens = 200_000,
            ReserveTokens = 8_000,
            PromptCachingEnabled = caching,
            RowsInView = 10,
        };

    private static async Task SeedAsync(IConversationStore store)
    {
        await ContextObservationProjection.RecordAsync(store, Observation(Root, "root", 3));
        await ContextObservationProjection.RecordAsync(store, Observation(Child, "agent-1", 1, measured: 4_321));

        _ = await CompactionStateProjection.PrepareAsync(store, Root, "cp-1", 1, 0, CompactionTrigger.Preemptive, T0);
        _ = await CompactionStateProjection.MarkValidatedAsync(store, Root, "cp-1", T0);
        _ = await CompactionStateProjection.TryCommitAsync(store, Root, "cp-1", T0);
        _ = await CompactionStateProjection.ActivateAsync(store, Root, "cp-1", rowSeq: 1, T0);

        _ = await CompactionStateProjection.PrepareAsync(store, Child, "cp-2", 1, 0, CompactionTrigger.Reactive, T0);
        _ = await CompactionStateProjection.RejectAsync(store, Child, "cp-2", CheckpointReasons.Abandoned, T0);

        var ledger = new UsageLedger(Root);
        ledger.RecordUsage(
            UsageRecordMapper.FromUsageMessage(
                new UsageMessage
                {
                    Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                    GenerationId = "g1",
                },
                Root,
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
                Child,
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

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Build_ReadsEveryRowFromDurableState_AndSurvivesAFreshHandle(string kind)
    {
        var store = _harness.Open(kind);
        await SeedAsync(store);
        var options = new ConversationContextReportOptions { TimeProvider = new FixedClock(DateTimeOffset.UtcNow) };

        var report = await ConversationContextReport.BuildAsync(store, Root, Roster(), options);

        report.RootThreadId.Should().Be(Root);
        report.SchemaVersion.Should().Be(1);
        report.Agents.Select(a => a.AgentId).Should().Equal("root", "agent-1");

        var root = report.Agents[0];
        root.ThreadId.Should().Be(Root);
        root.ParentAgentId.Should().BeNull();
        root.ExecutionKind.Should().Be(UsageExecutionKind.Primary);
        root.Observation!.GenerationOrdinal.Should().Be(3);
        root.Observation.Provenance.Should().Be(MeasurementProvenance.Estimated);
        root.Freshness.Should().Be(ContextFreshness.Stale, "nothing live vouched for it");
        root.CacheTemperature.Should().Be(CacheTemperature.Hot, "the seed just wrote durable state");
        root.Compaction.State.Should().Be(CompactionStates.Active);
        root.Compaction.CheckpointId.Should().Be("cp-1");
        root.Usage!.InputTokens.Should().Be(100);
        root.Usage.ExecutionKinds.Should().Equal(UsageExecutionKind.Primary);

        var child = report.Agents[1];
        child.ParentAgentId.Should().Be("root");
        child.Observation!.MeasuredInputTokens.Should().Be(4_321);
        child.Compaction.State.Should().Be(CompactionStates.Rejected);
        child.Compaction.CheckpointId.Should().Be("cp-2");
        child.Compaction.Reason.Should().Be(CheckpointReasons.Abandoned);
        child.Usage!.InputTokens.Should().Be(200);

        report.Total.TotalTokens.Should().Be(350);
        report.Total.InputTokens.Should().Be(300);
        report.Total.UsageCompleteness.Should().Be(UsageCompleteness.Complete);
        report.Total.CostCompleteness.Should().Be(CostCompleteness.Unavailable);
        report.Total.PreferredCostMicros.Should().BeNull();

        var reloaded = await ConversationContextReport.BuildAsync(_harness.Reopen(kind), Root, Roster(), options);
        reloaded.Should().BeEquivalentTo(report, o => o.Excluding(r => r.GeneratedAtUtc));
    }

    [Fact]
    public async Task ALiveObservation_WinsOverThePersistedOne_AndReadsFresh()
    {
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var live = Observation(Root, "root", 4, measured: 9_000);
        var options = new ConversationContextReportOptions
        {
            TimeProvider = new FixedClock(DateTimeOffset.UtcNow),
            LiveObservation = threadId => threadId == Root ? live : null,
        };

        var report = await ConversationContextReport.BuildAsync(store, Root, Roster(), options);

        report.Agents[0].Freshness.Should().Be(ContextFreshness.Fresh);
        report.Agents[0].Observation.Should().BeSameAs(live);
        report.Agents[1].Freshness.Should().Be(ContextFreshness.Stale);
    }

    [Fact]
    public async Task CacheTemperature_FollowsDurableActivityAgainstTheTtl_AndIsUnknownWithoutCaching()
    {
        var store = _harness.Open("memory");
        await SeedAsync(store);
        await ContextObservationProjection.RecordAsync(store, Observation(Child, "agent-1", 2, caching: false));

        var cold = await ConversationContextReport.BuildAsync(
            store,
            Root,
            Roster(),
            new ConversationContextReportOptions { TimeProvider = new FixedClock(DateTimeOffset.UtcNow.AddHours(1)) }
        );

        cold.Agents[0].CacheTemperature.Should().Be(CacheTemperature.Cold);
        cold.Agents[1].CacheTemperature.Should().Be(CacheTemperature.Unknown, "that loop sends without caching");
    }

    [Fact]
    public async Task AnExcludedLoop_ReadsUnsupported_WithNoContext()
    {
        // §9: a loop that runs on a provider-side session (non-empty SessionMappings) owns its own context;
        // the host can neither observe nor compact it.
        var store = _harness.Open("memory");
        await SeedAsync(store);
        await store.UpdateMetadataAsync(
            Child,
            existing =>
                (existing ?? new ThreadMetadata { ThreadId = Child, LastUpdated = 0 }) with
                {
                    SessionMappings = ImmutableDictionary<string, string>.Empty.Add("codex", "sess-1"),
                }
        );

        var report = await ConversationContextReport.BuildAsync(store, Root, Roster());

        var child = report.Agents[1];
        child.Compaction.State.Should().Be(CompactionStates.Unsupported);
        child.Observation.Should().BeNull();
        child.Freshness.Should().Be(ContextFreshness.None);
        child.CacheTemperature.Should().Be(CacheTemperature.Unknown);
        child.Usage!.InputTokens.Should().Be(200, "spend is still spend");
    }

    [Fact]
    public async Task AnUnobservedConversation_StillReportsItsRoot_WithEverythingAbsent()
    {
        var store = _harness.Open("memory");

        var report = await ConversationContextReport.BuildAsync(store, Root, []);

        var root = report.Agents.Should().ContainSingle().Subject;
        root.AgentId.Should().Be("root");
        root.Observation.Should().BeNull();
        root.Freshness.Should().Be(ContextFreshness.None);
        root.Compaction.State.Should().Be(CompactionStates.None);
        root.CacheTemperature.Should().Be(CacheTemperature.Unknown);
        root.Usage.Should().BeNull();
        report.Total.TotalTokens.Should().Be(0);
        report.Total.UsageCompleteness.Should().BeNull();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
