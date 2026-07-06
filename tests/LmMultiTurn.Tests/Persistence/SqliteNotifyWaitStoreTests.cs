using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Tests for <see cref="SqliteNotifyWaitStore"/>.
/// </summary>
public class SqliteNotifyWaitStoreTests
{
    [Fact]
    public async Task Save_Then_LoadActive_ReturnsRow_ScopedByThread()
    {
        await using var factory = new InMemorySqliteConnectionFactory();
        await SqliteSchemaInitializer.InitializeSchemaAsync(factory);
        var store = new SqliteNotifyWaitStore(factory);

        var rec = new NotifyWaitRecord("w1", "threadA", "schedule", "{}", "hourly", 3, 0, 0, 0, "active");
        await store.SaveAsync(rec);

        (await store.LoadActiveAsync("threadA")).Should().ContainSingle(r => r.WaitId == "w1");
        (await store.LoadActiveAsync("threadB")).Should().BeEmpty();
    }

    [Fact]
    public async Task Save_OnConflict_FullyUpsertsMutableColumns_NotJustFiresSoFarAndStatus()
    {
        await using var factory = new InMemorySqliteConnectionFactory();
        await SqliteSchemaInitializer.InitializeSchemaAsync(factory);
        var store = new SqliteNotifyWaitStore(factory);

        var initial = new NotifyWaitRecord("w", "t", "schedule", "{}", null, 5, 0, 1_000, 500, "active");
        await store.SaveAsync(initial);

        // Simulate the restore re-arm case: same wait_id, clamped maxFires and refreshed
        // timeout/armed timestamps.
        var reArmed = new NotifyWaitRecord("w", "t", "schedule", "{}", null, 3, 0, 2_000, 1_500, "active");
        await store.SaveAsync(reArmed);

        var rows = await store.LoadActiveAsync("t");
        rows.Should().ContainSingle();
        var row = rows.Single();
        row.MaxFires.Should().Be(3);
        row.TimeoutAtUnixMs.Should().Be(2_000);
        row.ArmedAtUnixMs.Should().Be(1_500);
    }

    [Fact]
    public async Task SaveAsync_WithoutPriorExplicitSchemaInit_SelfInitializesAndRoundTrips()
    {
        // Regression: a store constructed without the caller remembering to invoke
        // SqliteSchemaInitializer.InitializeSchemaAsync beforehand must still work — lazy,
        // idempotent, double-checked schema init on first use (mirrors SqliteConversationStore).
        await using var factory = new InMemorySqliteConnectionFactory();
        var store = new SqliteNotifyWaitStore(factory);

        var rec = new NotifyWaitRecord("w-lazy", "threadLazy", "schedule", "{}", "hourly", 3, 0, 0, 0, "active");
        await store.SaveAsync(rec);

        (await store.LoadActiveAsync("threadLazy")).Should().ContainSingle(r => r.WaitId == "w-lazy");
    }

    [Fact]
    public async Task DeleteAsync_WithoutPriorExplicitSchemaInit_SelfInitializes()
    {
        await using var factory = new InMemorySqliteConnectionFactory();
        var store = new SqliteNotifyWaitStore(factory);

        // Deleting a non-existent row on a never-initialized schema must not throw — schema init
        // happens lazily inside DeleteAsync itself, same as SaveAsync/LoadActiveAsync.
        var act = () => store.DeleteAsync("threadX", "does-not-exist");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        await using var factory = new InMemorySqliteConnectionFactory();
        await SqliteSchemaInitializer.InitializeSchemaAsync(factory);
        var store = new SqliteNotifyWaitStore(factory);
        await store.SaveAsync(new NotifyWaitRecord("w1", "t", "schedule", "{}", null, null, 0, 0, 0, "active"));

        await store.DeleteAsync("t", "w1");

        (await store.LoadActiveAsync("t")).Should().BeEmpty();
    }

    [Fact]
    public async Task SameWaitId_DifferentThreads_BothPersist_NoOverwrite()
    {
        // wait_id is the model-assigned tool_call_id, not globally unique across threads (tests
        // routinely reuse fixed ids like "tc_notify"). Identity must be scoped by (thread_id,
        // wait_id) so two threads sharing one store never collide/overwrite each other's row.
        await using var factory = new InMemorySqliteConnectionFactory();
        await SqliteSchemaInitializer.InitializeSchemaAsync(factory);
        var store = new SqliteNotifyWaitStore(factory);

        await store.SaveAsync(new NotifyWaitRecord("tc_notify", "threadA", "schedule", "{}", "a-label", null, 0, 1_000, 500, "active"));
        await store.SaveAsync(new NotifyWaitRecord("tc_notify", "threadB", "schedule", "{}", "b-label", null, 0, 2_000, 1_500, "active"));

        // Both independently loadable — neither overwrote the other.
        var rowsA = await store.LoadActiveAsync("threadA");
        var rowsB = await store.LoadActiveAsync("threadB");
        rowsA.Should().ContainSingle(r => r.WaitId == "tc_notify" && r.Label == "a-label");
        rowsB.Should().ContainSingle(r => r.WaitId == "tc_notify" && r.Label == "b-label");

        // Deleting one thread's row must not affect the other's.
        await store.DeleteAsync("threadA", "tc_notify");

        (await store.LoadActiveAsync("threadA")).Should().BeEmpty();
        (await store.LoadActiveAsync("threadB")).Should().ContainSingle(r => r.WaitId == "tc_notify" && r.Label == "b-label");
    }

    /// <summary>
    /// Minimal shared-cache in-memory <see cref="ISqliteConnectionFactory"/> for tests. Unlike the
    /// production file-backed factory, this keeps one connection open for the factory's lifetime
    /// so the shared in-memory database isn't dropped between per-call connections handed out by
    /// <see cref="GetConnectionAsync"/>.
    /// </summary>
    private sealed class InMemorySqliteConnectionFactory : ISqliteConnectionFactory
    {
        private readonly string _connectionString;
        private readonly SqliteConnection _keepAliveConnection;
        private bool _disposed;

        public InMemorySqliteConnectionFactory()
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"notify-wait-tests-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            _keepAliveConnection = new SqliteConnection(_connectionString);
            _keepAliveConnection.Open();
        }

        public async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _keepAliveConnection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
