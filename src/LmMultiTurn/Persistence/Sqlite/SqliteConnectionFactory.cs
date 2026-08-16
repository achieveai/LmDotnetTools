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

            // Private, deliberately. A shared cache makes several connections to one file share a page
            // cache and its locks, which is what a named in-memory database needs and what a WAL file on
            // disk does not: WAL already lets a writer and readers proceed at once, and layering the
            // shared cache's own table-level locking over it only narrows that. It also puts connections
            // that the pool hands to different threads onto shared native state, where a concurrent open
            // and close of the same file can fault inside the SQLite provider rather than returning an
            // error. Connection pooling stays on; only the sharing of cache state goes.
            Cache = SqliteCacheMode.Private,
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

        PooledConnection? connection = null;
        try
        {
            // Opened as the pooled connection itself. Opening a plain connection first and then handing it
            // to the wrapper would mean closing it — which returns its handle to the pool — and opening a
            // second one, so for a moment a handle this call is responsible for is available to another
            // thread.
            connection = new PooledConnection(_connectionString, _semaphore);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Set pragmas once (they persist for the database file)
            await EnsurePragmasSetAsync(connection, ct).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            // Disposing the connection is what returns the permit; releasing it here as well would hand
            // out one more than the factory is allowed to.
            if (connection is null)
            {
                _ = _semaphore.Release();
            }
            else
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

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
            _ = await walCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Set synchronous to NORMAL for balance of safety and speed
            using var syncCommand = connection.CreateCommand();
            syncCommand.CommandText = "PRAGMA synchronous=NORMAL;";
            _ = await syncCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Enable foreign keys
            using var fkCommand = connection.CreateCommand();
            fkCommand.CommandText = "PRAGMA foreign_keys=ON;";
            _ = await fkCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // Set cache size (negative means KB, positive means pages)
            using var cacheCommand = connection.CreateCommand();
            cacheCommand.CommandText = "PRAGMA cache_size=-10000;";
            _ = await cacheCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _pragmasSet = true;
        }
        finally
        {
            _ = _pragmaLock.Release();
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
    /// A SqliteConnection that returns its permit to the factory when disposed, so the number of live
    /// connections is bounded by the caller's lifetime rather than by the pool's.
    /// </summary>
    private sealed class PooledConnection : SqliteConnection
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public PooledConnection(string connectionString, SemaphoreSlim semaphore)
            : base(connectionString) => _semaphore = semaphore;

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // try/finally, not sequential statements: base.Dispose can throw (a SQLite close can
            // surface a native error), and if it does, an un-finallied Release is skipped. The
            // permit is then lost for the lifetime of the factory -- and unlike a leaked handle,
            // which the GC eventually reclaims, a lost permit never comes back. Leak enough of
            // them and GetConnectionAsync blocks forever on a semaphore no one will ever post to,
            // which presents as the whole persistence layer hanging with no error anywhere.
            // Releasing the permit is this type's ONLY responsibility to the factory; it has to
            // happen whether or not the underlying connection closed cleanly.
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    _ = _semaphore.Release();
                }
            }
        }

        public override async ValueTask DisposeAsync()
        {
            // Deliberately does NOT set _disposed here before delegating: DbConnection's default
            // DisposeAsync() implementation calls the synchronous Dispose(), which virtually
            // dispatches back to THIS type's Dispose(bool) override on the very same instance. If
            // _disposed were already true at that point, that reentrant call's own guard would
            // short-circuit and skip base.Dispose(disposing) entirely -- meaning the underlying
            // SqliteConnection's native handle would never actually be closed or returned to the
            // pool, silently leaking it for the lifetime of this (GC-tracked) object. Dispose(bool)
            // already sets _disposed and releases the semaphore exactly once; letting that call own
            // both keeps this override a thin, idempotent forward rather than a second, competing
            // disposal path.
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
