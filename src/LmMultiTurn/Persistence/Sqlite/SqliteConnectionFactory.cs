using Microsoft.Data.Sqlite;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;

/// <summary>
/// Factory for creating SQLite connections with connection pooling support.
/// Uses Microsoft.Data.Sqlite's built-in connection pooling with a semaphore
/// to limit maximum concurrent connections.
/// </summary>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxConnections;
    private bool _disposed;
    private bool _pragmasSet;
    private readonly SemaphoreSlim _pragmaLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteConnectionFactory"/> class.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="maxConnections">Maximum number of concurrent connections (default: 5).</param>
    public SqliteConnectionFactory(string databasePath, int maxConnections = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        _maxConnections = maxConnections;
        _semaphore = new SemaphoreSlim(maxConnections, maxConnections);
    }

    /// <inheritdoc />
    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _semaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Set pragmas once (they persist for the database file)
            await EnsurePragmasSetAsync(connection, ct).ConfigureAwait(false);

            // Wrap the connection to release semaphore on dispose
            return new PooledConnection(connection, _semaphore);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private async Task EnsurePragmasSetAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_pragmasSet)
        {
            return;
        }

        await _pragmaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pragmasSet)
            {
                return;
            }

            // Use WAL mode for better concurrency
            using var walCommand = connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            await walCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Set synchronous to NORMAL for balance of safety and speed
            using var syncCommand = connection.CreateCommand();
            syncCommand.CommandText = "PRAGMA synchronous=NORMAL;";
            await syncCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Enable foreign keys
            using var fkCommand = connection.CreateCommand();
            fkCommand.CommandText = "PRAGMA foreign_keys=ON;";
            await fkCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Set cache size (negative means KB, positive means pages)
            using var cacheCommand = connection.CreateCommand();
            cacheCommand.CommandText = "PRAGMA cache_size=-10000;";
            await cacheCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _pragmasSet = true;
        }
        finally
        {
            _pragmaLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Wait for all connections to be returned
        for (var i = 0; i < _maxConnections; i++)
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
        }

        _semaphore.Dispose();
        _pragmaLock.Dispose();
    }

    /// <summary>
    /// A thin wrapper around SqliteConnection that releases the semaphore when disposed.
    /// Uses composition instead of inheritance to avoid base class constructor issues.
    /// </summary>
    private sealed class PooledConnection : SqliteConnection
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public PooledConnection(SqliteConnection connection, SemaphoreSlim semaphore)
            : base(connection.ConnectionString)
        {
            _semaphore = semaphore;

            // We need to close the passed connection and open this one
            // because we can't transfer the underlying connection
            connection.Close();
            connection.Dispose();
            Open();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            base.Dispose(disposing);

            if (disposing)
            {
                _semaphore.Release();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
            _semaphore.Release();
        }
    }
}
