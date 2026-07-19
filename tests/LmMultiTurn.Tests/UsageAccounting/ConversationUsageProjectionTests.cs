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
