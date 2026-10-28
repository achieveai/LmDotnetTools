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
    private bool _walModeSet;
    private readonly SemaphoreSlim _walModeLock = new(1, 1);

    /// <summary>
    /// Taken around every open and every pool clear in the process, so none of them overlap. Microsoft.Data.Sqlite
    /// marks an opening connection's pooled handle active a moment before it records the connection that owns it;
    /// an open on an empty pool, or a clear, that looks in that moment reclaims the handle as leaked and hands it to
    /// a second connection, and two threads then drive one native handle (seen as "SQLite Error 1: 'not an error'"
    /// from <c>BeginTransaction</c> under four concurrent callers). Opens from a warm pool already queue on the
    /// pool's own lock, so this costs next to nothing.
    /// </summary>
    internal static readonly object PoolGate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteConnectionFactory"/> class.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file.</param>
    /// <param name="maxConnections">Maximum number of concurrent connections (default: 5).</param>
    public SqliteConnectionFactory(string databasePath, int maxConnections = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConnections, 1);

        _connectionString = BuildConnectionString(databasePath);
        _maxConnections = maxConnections;
        _semaphore = new SemaphoreSlim(maxConnections, maxConnections);
    }

    /// <summary>
    /// Releases the idle pooled handles of <paramref name="databasePath"/>: the pool every factory for that file
    /// opens from. Prefer it to <see cref="SqliteConnection.ClearAllPools"/>, which clears every database's pool in
    /// the process and can dispose another database's connection while it is being opened.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file, as given to the constructor.</param>
    public static void ClearPool(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        using var connection = new SqliteConnection(BuildConnectionString(databasePath));
        lock (PoolGate)
        {
            SqliteConnection.ClearPool(connection);
        }
    }

    // The pool is keyed by this exact string, so ClearPool must build it the same way the constructor does.
    private static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
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
            ct.ThrowIfCancellationRequested();
            lock (PoolGate)
            {
                // Synchronous on purpose: SqliteConnection has no truly asynchronous open, and a lock cannot
                // span an await.
                connection.Open();
            }

            await EnsureWalModeAsync(connection, ct).ConfigureAwait(false);
            await ApplyConnectionPragmasAsync(connection, ct).ConfigureAwait(false);

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

    // WAL mode is stored in the database file, so it is set once per factory.
    private async Task EnsureWalModeAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_walModeSet)
        {
            return;
        }

        await _walModeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_walModeSet)
            {
                return;
            }

            using var walCommand = connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            _ = await walCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _walModeSet = true;
        }
        finally
        {
            _ = _walModeLock.Release();
        }
    }

    // These belong to the connection, not the file, so every open sets them; a pooled handle keeps them anyway, and
    // none of them touches the database. Set once, they reached only the first handle: the rest ran synchronous=FULL,
    // a disk sync on every commit (WAL needs one only at checkpoints), with a 2 MB cache (negative means KB).
    private static async Task ApplyConnectionPragmasAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA cache_size=-10000;";
        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        _walModeLock.Dispose();
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
