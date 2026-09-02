using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins the per-thread observation ring and latest pointer on every store flavour (#680; spec 679 §4.1).
/// </summary>
public sealed class ContextObservationProjectionTests : IAsyncLifetime
{
    private const string Thread = "thread-obs";
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    private static ContextObservation Observation(long ordinal, long? measured = null) =>
        new()
        {
            ThreadId = Thread,
            AgentId = "root",
            RunId = "run-1",
            GenerationId = $"gen-{ordinal}",
            GenerationOrdinal = ordinal,
            ObservedAtUtc = T0.AddSeconds(ordinal),
            EffectiveModelId = "model-x",
            EstimatedInputTokens = 1_000 * ordinal,
            MeasuredInputTokens = measured,
            Provenance = measured is null ? MeasurementProvenance.Estimated : MeasurementProvenance.Measured,
            WindowTokens = 200_000,
            ReserveTokens = 10_000,
            ActiveCheckpointId = ordinal > 2 ? "cp-1" : null,
            RowsInView = 10 + ordinal,
            Decision = new CompactionDecisionSummary
            {
                Decision = "NoAction",
                Tokens = 1_000 * ordinal,
                Window = 200_000,
                Reserve = 10_000,
                CacheTemperature = CacheTemperature.Hot,
            },
        };

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Record_ThenLoad_RoundTripsEveryField_ThroughAFreshHandle(string kind)
    {
        var store = _harness.Open(kind);
        var observation = Observation(1, measured: 1_234);

        await ContextObservationProjection.RecordAsync(store, observation);

        var reopened = _harness.Reopen(kind);
        var latest = await ContextObservationProjection.LoadLatestAsync(reopened, Thread);
        latest.Should().BeEquivalentTo(observation);
        latest!.Utilization.Should().BeApproximately(1_234d / 190_000d, 1e-9);
        (await ContextObservationProjection.LoadHistoryAsync(reopened, Thread))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(observation);

        var raw = (await store.LoadMetadataAsync(Thread))!.Properties![ContextObservationProjection.LatestPropertyKey];
        raw.ToString().Should().Contain("\"Measured\"", "provenance is persisted by name, not by ordinal");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Record_KeepsARingOfTheLastN_NewestLast(string kind)
    {
        var store = _harness.Open(kind);

        for (var i = 1; i <= 5; i++)
        {
            await ContextObservationProjection.RecordAsync(store, Observation(i), historyLength: 3);
        }

        var history = await ContextObservationProjection.LoadHistoryAsync(store, Thread);
        history.Select(o => o.GenerationOrdinal).Should().Equal(3, 4, 5);
        (await ContextObservationProjection.LoadLatestAsync(store, Thread))!.GenerationOrdinal.Should().Be(5);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Record_NeverOverwritesANewerSchemaRing_AndLeavesLatestAlone(string kind)
    {
        var store = _harness.Open(kind);
        const string futureRing = """{"schema_version":2,"observations":[],"a_new_field":1}""";
        const string futureLatest = """{"schema_version":2,"thread_id":"thread-obs"}""";
        await store.UpdateMetadataAsync(
            Thread,
            existing =>
                (existing ?? new ThreadMetadata { ThreadId = Thread, LastUpdated = 0 }) with
                {
                    Properties = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
                        .SetItem(ContextObservationProjection.HistoryPropertyKey, futureRing)
                        .SetItem(ContextObservationProjection.LatestPropertyKey, futureLatest),
                }
        );

        await ContextObservationProjection.RecordAsync(store, Observation(1));

        var properties = (await store.LoadMetadataAsync(Thread))!.Properties!;
        properties[ContextObservationProjection.HistoryPropertyKey].ToString().Should().Be(futureRing);
        properties[ContextObservationProjection.LatestPropertyKey].ToString().Should().Be(futureLatest);
        (await ContextObservationProjection.LoadLatestAsync(store, Thread)).Should().BeNull();
        (await ContextObservationProjection.LoadHistoryAsync(store, Thread)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Load_OnAThreadWithNoObservations_IsEmpty(string kind)
    {
        var store = _harness.Open(kind);

        (await ContextObservationProjection.LoadLatestAsync(store, Thread)).Should().BeNull();
        (await ContextObservationProjection.LoadHistoryAsync(store, Thread)).Should().BeEmpty();
    }
}
