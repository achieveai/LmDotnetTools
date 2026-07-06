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
    public async Task Delete_RemovesRow()
    {
        await using var factory = new InMemorySqliteConnectionFactory();
        await SqliteSchemaInitializer.InitializeSchemaAsync(factory);
        var store = new SqliteNotifyWaitStore(factory);
        await store.SaveAsync(new NotifyWaitRecord("w1", "t", "schedule", "{}", null, null, 0, 0, 0, "active"));

        await store.DeleteAsync("w1");

        (await store.LoadActiveAsync("t")).Should().BeEmpty();
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
