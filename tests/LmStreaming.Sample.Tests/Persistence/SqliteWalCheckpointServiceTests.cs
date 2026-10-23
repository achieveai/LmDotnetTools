using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Persistence;

public sealed class SqliteWalCheckpointServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"wal-checkpoint-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Checkpoint_TruncatesFrames_AndDefersWithoutWarningForActiveReader()
    {
        var logger = new CollectingLogger<SqliteWalCheckpointService>();
        var service = new SqliteWalCheckpointService(_databasePath, new ConfigurationBuilder().Build(), logger);
        await using var writer = await OpenWalConnectionAsync();
        await ExecuteAsync(writer, "CREATE TABLE values_table (value INTEGER); INSERT INTO values_table VALUES (1);");
        var walPath = _databasePath + "-wal";
        new FileInfo(walPath).Length.Should().BeGreaterThan(0);

        await service.TryCheckpointAsync(CancellationToken.None);

        new FileInfo(walPath).Length.Should().Be(0);

        await ExecuteAsync(writer, "INSERT INTO values_table VALUES (2);");
        await using var reader = await OpenConnectionAsync();
        using var readTransaction = reader.BeginTransaction(deferred: true);
        await ExecuteScalarAsync(reader, readTransaction, "SELECT COUNT(*) FROM values_table;");
        await ExecuteAsync(writer, "INSERT INTO values_table VALUES (3);");

        await service.TryCheckpointAsync(CancellationToken.None);

        new FileInfo(walPath).Length.Should().BeGreaterThan(0);
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
        logger
            .Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Debug && entry.Message.Contains("truncate deferred", StringComparison.Ordinal)
            );
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(_databasePath);
        TryDelete(_databasePath + "-wal");
        TryDelete(_databasePath + "-shm");
    }

    private async Task<SqliteConnection> OpenWalConnectionAsync()
    {
        var connection = await OpenConnectionAsync();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
