using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using LmMultiTurn.Tests.Persistence;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// Pins that the activity a cache temperature derives from is read from durable state — metadata,
/// rows, the run ledger and the run lifecycle — and never from anything a process remembers (#680;
/// spec 679 §4.4).
/// </summary>
public sealed class ConversationActivityTests : IAsyncLifetime
{
    private const string Thread = "thread-act";
    private static readonly DateTimeOffset T1 = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private readonly ConversationStoreHarness _harness = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    public static TheoryData<string> AllKinds => ConversationStoreHarness.AllKinds;

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task LastActivity_IsTheNewestOfMetadataRowsLedgerAndLifecycle_ReadFromAFreshHandle(string kind)
    {
        var store = _harness.Open(kind);

        (await ConversationActivity.GetLastActivityAsync(store, Thread)).Should().BeNull("nothing durable exists yet");

        await store.SaveMetadataAsync(
            Thread,
            new ThreadMetadata { ThreadId = Thread, LastUpdated = T1.ToUnixTimeMilliseconds() }
        );
        (await ConversationActivity.GetLastActivityAsync(store, Thread)).Should().Be(T1);

        var t2 = T1.AddMinutes(1);
        await store.AppendMessagesAsync(
            Thread,
            [ConversationStoreHarness.Row(Thread, "row", t2.ToUnixTimeMilliseconds())]
        );
        (await ConversationActivity.GetLastActivityAsync(_harness.Reopen(kind), Thread))
            .Should()
            .Be(t2, "the newest row is newer than the metadata");

        var t3 = T1.AddMinutes(2);
        var ledger = (IRunLedgerStore)store;
        await ledger.UpsertRunLedgerAsync(new RunLedgerEntry(Thread, "run-1", RunStatus.Completed, [], T1, t3));
        (await ConversationActivity.GetLastActivityAsync(_harness.Reopen(kind), Thread))
            .Should()
            .Be(t3, "the run ledger's update is newer still");

        var t4 = T1.AddMinutes(3);
        var lifecycle = (IRunLifecycleStore)store;
        await lifecycle.RecordRunStartedAsync(
            new RunLifecycleState
            {
                ThreadId = Thread,
                RunId = "run-2",
                StartedAt = T1,
                UpdatedAt = T1,
            }
        );
        _ = await lifecycle.TryMarkRunTerminalAsync("run-2", "completed", 1, t4);
        (await ConversationActivity.GetLastActivityAsync(_harness.Reopen(kind), Thread))
            .Should()
            .Be(t4, "a terminal run is the newest durable activity");

        (await ConversationActivity.GetLastActivityAsync(store, "other-thread"))
            .Should()
            .BeNull("activity is per thread");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task LastActivity_OnALegacyThreadWithoutSeq_StillReadsTheNewestRow(string kind)
    {
        if (kind == "memory")
        {
            return; // no row can predate the binary in memory
        }

        _ = _harness.Open(kind);
        var t2 = T1.AddMinutes(1);
        await _harness.SeedLegacyRowsAsync(
            kind,
            Thread,
            [
                ConversationStoreHarness.Row(Thread, "old", T1.ToUnixTimeMilliseconds()),
                ConversationStoreHarness.Row(Thread, "new", t2.ToUnixTimeMilliseconds()),
            ]
        );

        (await ConversationActivity.GetLastActivityAsync(_harness.Reopen(kind), Thread)).Should().Be(t2);
    }

    [Fact]
    public void CacheTemperature_IsHotInsideTheTtl_ColdOutside_UnknownWhenNotCaching()
    {
        var ttl = TimeSpan.FromMinutes(5);
        var now = T1.AddMinutes(10);

        ConversationActivity
            .ResolveCacheTemperature(now.AddMinutes(-4), now, ttl, cachingEnabled: true)
            .Should()
            .Be(CacheTemperature.Hot);
        ConversationActivity
            .ResolveCacheTemperature(now.AddMinutes(-5), now, ttl, cachingEnabled: true)
            .Should()
            .Be(CacheTemperature.Cold, "the boundary is exclusive");
        ConversationActivity
            .ResolveCacheTemperature(now.AddMinutes(-6), now, ttl, cachingEnabled: true)
            .Should()
            .Be(CacheTemperature.Cold);
        ConversationActivity
            .ResolveCacheTemperature(lastActivity: null, now, ttl, cachingEnabled: true)
            .Should()
            .Be(CacheTemperature.Cold, "no activity means nothing is cached");
        ConversationActivity
            .ResolveCacheTemperature(now.AddMinutes(-1), now, ttl, cachingEnabled: false)
            .Should()
            .Be(CacheTemperature.Unknown);
    }
}
