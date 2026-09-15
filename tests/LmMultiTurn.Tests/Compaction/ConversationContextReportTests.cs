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
    public async Task ALiveObservationOfTheSameGeneration_KeepsThePersistedDecisionAndCheckpoint()
    {
        // The loop's in-memory observation is written by the measurement alone; the policy stamps its
        // decision and the active checkpoint on the persisted record of the same generation. A live row
        // must not blank them, or the panel shows no decision for as long as the loop is alive.
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var decision = new CompactionDecisionSummary
        {
            Decision = CompactionDecisionKinds.Compact,
            Reason = "hard",
            Tokens = 6_940,
            Window = 200_000,
            Reserve = 8_000,
        };
        await ContextObservationProjection.RecordAsync(
            store,
            Observation(Root, "root", 5) with
            {
                ActiveCheckpointId = "cp-1",
                Decision = decision,
            }
        );
        var live = Observation(Root, "root", 5, measured: 9_000);
        var options = new ConversationContextReportOptions
        {
            TimeProvider = new FixedClock(DateTimeOffset.UtcNow),
            LiveObservation = threadId => threadId == Root ? live : null,
        };

        var report = await ConversationContextReport.BuildAsync(store, Root, Roster(), options);

        var root = report.Agents[0];
        root.Freshness.Should().Be(ContextFreshness.Fresh);
        root.Observation!.MeasuredInputTokens.Should().Be(9_000, "the live measurement still wins");
        root.Observation.Decision.Should().BeEquivalentTo(decision);
        root.Observation.ActiveCheckpointId.Should().Be("cp-1");
        root.Compaction.LastDecision!.GenerationOrdinal.Should().Be(5);
    }

    [Fact]
    public async Task NewerGenerationsWithoutADecision_StillReportTheLastRealDecision_WithItsGenerationAndAge()
    {
        // A wrap-up turn, or a live measurement ahead of the policy's stamp, carries no decision. The panel must
        // show what the compaction state rests on rather than "No decision yet" beside "Compacted".
        var store = _harness.Open("memory");
        await SeedAsync(store);
        var decision = new CompactionDecisionSummary
        {
            Decision = CompactionDecisionKinds.Compact,
            Reason = "economic",
            Tokens = 180_000,
            Window = 200_000,
            Reserve = 8_000,
        };
        await ContextObservationProjection.RecordAsync(
            store,
            Observation(Root, "root", 5) with
            {
                Decision = decision,
            }
        );
        await ContextObservationProjection.RecordAsync(store, Observation(Root, "root", 6));
        var live = Observation(Root, "root", 7, measured: 9_000);
        var options = new ConversationContextReportOptions
        {
            TimeProvider = new FixedClock(T0.AddSeconds(65)),
            LiveObservation = threadId => threadId == Root ? live : null,
        };

        var report = await ConversationContextReport.BuildAsync(store, Root, Roster(), options);

        var root = report.Agents[0];
        root.Observation!.Decision.Should().BeNull("the shown generation was not decided on");
        root.Compaction.LastDecision.Should()
            .BeEquivalentTo(
                new LastCompactionDecision
                {
                    Decision = decision,
                    GenerationOrdinal = 5,
                    GenerationId = $"{Root}-gen-5",
                    DecidedAtUtc = T0.AddSeconds(5),
                    AgeSeconds = 60,
                }
            );
        report.Agents[1].Compaction.LastDecision.Should().BeNull("no generation of that loop was decided on");
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

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task AQueuedManualCompaction_IsReportedFromThePersistedRequest_UntilALoopClaimsIt(string kind)
    {
        // A manual request can wait for the loop's next step; without this field a client that lost its frames
        // can only see InFlight, which a queued request never is.
        var store = _harness.Open(kind);
        await SeedAsync(store);
        static PendingManualCompaction Pending(string id, string? focus) =>
            new()
            {
                RequestId = id,
                Focus = focus,
                RequestedAt = T0.AddSeconds(30),
            };
        _ = await CompactionStateProjection.UpdateAsync(
            store,
            Root,
            s => s with { PendingManual = Pending("cmp-1", "keep the API decisions") }
        );
        // A thread with no checkpoint history yet still reports its queued request.
        _ = await CompactionStateProjection.UpdateAsync(
            store,
            "fresh-thread",
            s => s with { PendingManual = Pending("cmp-2", null) }
        );
        IReadOnlyList<AgentExecutionRef> roster =
        [
            .. Roster(),
            new AgentExecutionRef(
                Root,
                "fresh-thread",
                "agent-2",
                AgentExecutionRef.RootAgentId,
                UsageExecutionKind.SubAgent
            ),
        ];
        var options = new ConversationContextReportOptions { TimeProvider = new FixedClock(T0.AddSeconds(60)) };

        var report = await ConversationContextReport.BuildAsync(_harness.Reopen(kind), Root, roster, options);

        var root = report.Agents[0].Compaction;
        root.State.Should().Be(CompactionStates.Active, "a queued request does not change the checkpoint state");
        root.PendingManualCompaction.Should()
            .BeEquivalentTo(
                new PendingManualCompactionStatus
                {
                    RequestId = "cmp-1",
                    Focus = "keep the API decisions",
                    RequestedAtUtc = T0.AddSeconds(30),
                }
            );
        report.Agents[1].Compaction.PendingManualCompaction.Should().BeNull("nothing is queued for that loop");
        var fresh = report.Agents[2].Compaction;
        fresh.State.Should().Be(CompactionStates.None);
        fresh.PendingManualCompaction!.RequestId.Should().Be("cmp-2");
        fresh.PendingManualCompaction.Focus.Should().BeNull();

        using var json = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            )
        );
        var wire = json
            .RootElement.GetProperty("agents")[0]
            .GetProperty("compaction")
            .GetProperty("pendingManualCompaction");
        wire.GetProperty("requestId").GetString().Should().Be("cmp-1");
        wire.GetProperty("focus").GetString().Should().Be("keep the API decisions");
        wire.GetProperty("requestedAtUtc").GetDateTimeOffset().Should().Be(T0.AddSeconds(30));

        // Claimed by a loop: the field goes away.
        _ = await CompactionStateProjection.UpdateAsync(store, Root, s => s with { PendingManual = null });
        (await ConversationContextReport.BuildAsync(store, Root, Roster(), options))
            .Agents[0]
            .Compaction.PendingManualCompaction.Should()
            .BeNull();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task AnInFlightCheckpoint_OlderThanItsLongestAttempt_IsReportedAbandoned_WithoutWritingIt(string kind)
    {
        // A process that dies mid-summary leaves the entry Prepared; only a loop that restores the thread reconciles
        // it, and a sub-agent may never run again. Past the longest an attempt can take it cannot still be running.
        var store = _harness.Open(kind);
        await SeedAsync(store);
        _ = await CompactionStateProjection.PrepareAsync(
            store,
            Child,
            "cp-dead",
            1,
            0,
            CompactionTrigger.Preemptive,
            T0
        );
        var limit = ConversationContextReportOptions.DefaultInFlightAbandonedAfter;
        var defaults = new CompactionOptions();
        limit.Should().BeGreaterThan(defaults.SummaryTimeout * defaults.SummaryAttempts);

        async Task<AgentCompactionStatus> ChildAt(DateTimeOffset now, TimeSpan? abandonedAfter = null) =>
            (
                await ConversationContextReport.BuildAsync(
                    _harness.Reopen(kind),
                    Root,
                    Roster(),
                    new ConversationContextReportOptions
                    {
                        TimeProvider = new FixedClock(now),
                        InFlightAbandonedAfter = abandonedAfter ?? limit,
                    }
                )
            )
                .Agents[1]
                .Compaction;

        (await ChildAt(T0 + limit))
            .Should()
            .BeEquivalentTo(
                new
                {
                    State = CompactionStates.InFlight,
                    CheckpointId = "cp-dead",
                    Reason = (string?)null,
                }
            );
        (await ChildAt(T0 + limit + TimeSpan.FromSeconds(1)))
            .Should()
            .BeEquivalentTo(
                new
                {
                    State = CompactionStates.Rejected,
                    CheckpointId = "cp-dead",
                    Reason = CheckpointReasons.Abandoned,
                }
            );
        (await ChildAt(T0.AddSeconds(11), TimeSpan.FromSeconds(10)))
            .State.Should()
            .Be(CompactionStates.Rejected, "the host's own attempt limit applies");
        (await CompactionStateProjection.LoadAsync(store, Child))!
            .Find("cp-dead")!
            .Status.Should()
            .Be(CheckpointStatus.Prepared, "the report only reads; a restoring loop reconciles");
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

    /// <summary>
    ///     Spec 679 §4.3 shows every enum on the wire as its NAME. The web serializer that MVC uses
    ///     (<c>JsonSerializerDefaults.Web</c>, no global enum converter) would otherwise ship
    ///     <c>CostProvenance</c>, <c>CostCompleteness</c>, <c>UsageCompleteness</c> and
    ///     <c>UsageExecutionKind</c> as integers, which a client cannot label without a copy of the C# enum
    ///     order (#685). Pinned on the report types only: the persisted usage shapes are untouched.
    /// </summary>
    [Fact]
    public void TheReport_SerializesItsEnumsByName_UnderTheWebDefaults()
    {
        var report = new ConversationContextReport
        {
            RootThreadId = Root,
            GeneratedAtUtc = T0,
            Agents =
            [
                new AgentContextRow
                {
                    AgentId = "root",
                    ThreadId = Root,
                    ExecutionKind = UsageExecutionKind.Primary,
                    Freshness = ContextFreshness.Stale,
                    Compaction = new AgentCompactionStatus { State = CompactionStates.None },
                    Usage = new ExecutionUsageRow
                    {
                        ExecutionId = Root,
                        CostProvenance = CostProvenance.PublicEstimate,
                        EstimatedCostCompleteness = CostCompleteness.Partial,
                    },
                },
            ],
            Total = new ConversationCostTotal
            {
                CostProvenance = CostProvenance.ProviderReported,
                CostCompleteness = CostCompleteness.Complete,
                UsageCompleteness = UsageCompleteness.InProgress,
            },
        };

        using var json = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            )
        );

        var agent = json.RootElement.GetProperty("agents")[0];
        agent.GetProperty("executionKind").GetString().Should().Be("Primary");
        agent.GetProperty("usage").GetProperty("costProvenance").GetString().Should().Be("PublicEstimate");
        agent.GetProperty("usage").GetProperty("estimatedCostCompleteness").GetString().Should().Be("Partial");
        var total = json.RootElement.GetProperty("total");
        total.GetProperty("costProvenance").GetString().Should().Be("ProviderReported");
        total.GetProperty("costCompleteness").GetString().Should().Be("Complete");
        total.GetProperty("usageCompleteness").GetString().Should().Be("InProgress");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
