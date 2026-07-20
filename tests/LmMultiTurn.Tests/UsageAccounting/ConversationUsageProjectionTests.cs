using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

public class ConversationUsageProjectionTests
{
    private static ConversationUsageAggregate SampleAggregate()
    {
        var ledger = new UsageLedger("conv-1");
        ledger.UpsertAttempt(new UsageRecord
        {
            LogicalCallId = "a1",
            ProviderAttemptId = "a1",
            RootConversationId = "conv-1",
            RequestedModel = "model-A",
            InputTokens = 100,
            OutputTokens = 40,
            CacheWriteTokens = 10,
            EstimatedPublicCostMicros = 6000,
        });
        ledger.UpsertAttempt(new UsageRecord
        {
            LogicalCallId = "b1",
            ProviderAttemptId = "b1",
            RootConversationId = "conv-1",
            RequestedModel = "model-B",
            InputTokens = 20,
            OutputTokens = 10,
            ProviderReportedCostMicros = 5000,
            Finalized = true,
        });
        return ledger.Snapshot(UsageCompleteness.Complete);
    }

    private static async Task AssertRoundTripsAsync(IConversationStore store)
    {
        var original = SampleAggregate();

        await ConversationUsageProjection.SaveAsync(store, original);
        var loaded = await ConversationUsageProjection.LoadAsync(store, "conv-1");

        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task RoundTrips_ThroughInMemoryStore()
    {
        await AssertRoundTripsAsync(new InMemoryConversationStore());
    }

    [Fact]
    public async Task RoundTrips_ThroughFileStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"usage_proj_{Guid.NewGuid():N}");
        try
        {
            await AssertRoundTripsAsync(new FileConversationStore(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RoundTrips_ThroughSqliteStore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"usage_proj_{Guid.NewGuid():N}.db");
        var store = new SqliteConversationStore(dbPath);
        try
        {
            await AssertRoundTripsAsync(store);
        }
        finally
        {
            await store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void FromMetadata_ReturnsNull_WhenNoProjectionPresent()
    {
        ConversationUsageProjection.FromMetadata(null).Should().BeNull();

        var empty = new ThreadMetadata { ThreadId = "conv-1", LastUpdated = 0 };
        ConversationUsageProjection.FromMetadata(empty).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_PersistsRecords_LoadableForRebuild()
    {
        var store = new InMemoryConversationStore();
        var ledger = new UsageLedger("conv-1");
        ledger.UpsertAttempt(new UsageRecord
        {
            LogicalCallId = "a1",
            ProviderAttemptId = "a1",
            RootConversationId = "conv-1",
            RequestedModel = "model-A",
            InputTokens = 100,
            OutputTokens = 40,
        });

        await ConversationUsageProjection.SaveAsync(store, ledger.Snapshot(), ledger.SnapshotRecords());

        var records = await ConversationUsageProjection.LoadRecordsAsync(store, "conv-1");
        records.Should().ContainSingle();
        records[0].ProviderAttemptId.Should().Be("a1");
        records[0].InputTokens.Should().Be(100);
    }

    [Fact]
    public async Task SaveAsync_DoesNotReplaceNewerAggregate_WithStaleLowerWatermarkWrite()
    {
        var store = new InMemoryConversationStore();
        var newer = SampleAggregate() with { FoldedRevision = 50, TotalTokens = 900 };
        var stale = SampleAggregate() with { FoldedRevision = 5, TotalTokens = 100 };

        await ConversationUsageProjection.SaveAsync(store, newer);
        await ConversationUsageProjection.SaveAsync(store, stale); // out-of-order / post-restart write

        var loaded = await ConversationUsageProjection.LoadAsync(store, "conv-1");
        loaded!.FoldedRevision.Should().Be(50);
        loaded.TotalTokens.Should().Be(900);
    }

    [Fact]
    public async Task SaveAsync_MergesRecords_AcrossWritersWithEqualRevision()
    {
        // Two writers (e.g. a post-restart writer, or a second instance) can legitimately reuse the same
        // process-local revision for DIFFERENT attempts. A revision-`>`-guard alone would let the second
        // write clobber the first; a durable merge-by-attempt-id must preserve both (#196).
        var store = new InMemoryConversationStore();

        var a1 = new UsageRecord
        {
            LogicalCallId = "a1",
            ProviderAttemptId = "a1",
            RootConversationId = "conv-1",
            RequestedModel = "m",
            InputTokens = 100,
            OutputTokens = 10,
            Revision = 1,
        };
        var b1 = new UsageRecord
        {
            LogicalCallId = "b1",
            ProviderAttemptId = "b1",
            RootConversationId = "conv-1",
            RequestedModel = "m",
            InputTokens = 50,
            OutputTokens = 5,
            Revision = 1,
        };

        await ConversationUsageProjection.SaveAsync(
            store, ConversationUsageAggregate.Fold("conv-1", [a1], foldedRevision: 1), [a1]);
        await ConversationUsageProjection.SaveAsync(
            store, ConversationUsageAggregate.Fold("conv-1", [b1], foldedRevision: 1), [b1]);

        var records = await ConversationUsageProjection.LoadRecordsAsync(store, "conv-1");
        records.Select(r => r.ProviderAttemptId).Should().BeEquivalentTo(["a1", "b1"]);

        var loaded = await ConversationUsageProjection.LoadAsync(store, "conv-1");
        loaded!.TotalTokens.Should().Be(165); // (100+10) + (50+5)
    }

    [Fact]
    public async Task SaveAsync_MergesRecords_HigherRevisionWins_ForSameAttempt()
    {
        var store = new InMemoryConversationStore();

        var early = new UsageRecord
        {
            LogicalCallId = "a1",
            ProviderAttemptId = "a1",
            RootConversationId = "conv-1",
            RequestedModel = "m",
            InputTokens = 40,
            OutputTokens = 5,
            Revision = 1,
        };
        var final = early with { InputTokens = 100, OutputTokens = 20, Revision = 7 };

        await ConversationUsageProjection.SaveAsync(
            store, ConversationUsageAggregate.Fold("conv-1", [early], foldedRevision: 1), [early]);
        await ConversationUsageProjection.SaveAsync(
            store, ConversationUsageAggregate.Fold("conv-1", [final], foldedRevision: 7), [final]);

        var loaded = await ConversationUsageProjection.LoadAsync(store, "conv-1");
        loaded!.TotalTokens.Should().Be(120); // final revision (100+20), not the sum of both revisions
    }

    [Fact]
    public async Task SaveAsync_DoesNotOverwrite_NewerSchemaProjection()
    {
        // During a rollback / mixed-version deployment an older (v1) writer must not clobber a projection
        // written by a newer (v2) build — the forward-compatible data must be preserved intact (#196).
        var store = new InMemoryConversationStore();
        var futureJson = JsonSerializer.Serialize(SampleAggregate() with { SchemaVersion = 2, TotalTokens = 777 });

        await store.UpdateMetadataAsync(
            "conv-1",
            existing =>
            {
                var props = (existing?.Properties ?? ImmutableDictionary<string, object>.Empty)
                    .SetItem(ConversationUsageProjection.PropertyKey, futureJson);
                return existing is not null
                    ? existing with { Properties = props }
                    : new ThreadMetadata { ThreadId = "conv-1", LastUpdated = 0, Properties = props };
            });

        // A current (v1) writer attempts to save a smaller total.
        await ConversationUsageProjection.SaveAsync(store, SampleAggregate() with { TotalTokens = 5 });

        var metadata = await store.LoadMetadataAsync("conv-1");
        var raw = (string)metadata!.Properties![ConversationUsageProjection.PropertyKey];
        var preserved = JsonSerializer.Deserialize<ConversationUsageAggregate>(raw);
        preserved!.SchemaVersion.Should().Be(2);
        preserved.TotalTokens.Should().Be(777);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }
}
