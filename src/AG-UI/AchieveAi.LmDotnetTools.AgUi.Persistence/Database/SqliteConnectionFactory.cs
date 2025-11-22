using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Persistence.Database;

/// <summary>
/// Thread-safe factory for creating SQLite database connections with connection pooling.
/// </summary>
/// <remarks>
/// Uses SemaphoreSlim to limit concurrent connections and prevent database lock contention.
/// Automatically enables WAL mode and foreign keys for better concurrency and data integrity.
/// </remarks>
public sealed class SqliteConnectionFactory : IDbConnectionFactory, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _semaphore;
    private readonly ILogger<SqliteConnectionFactory> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteConnectionFactory"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="maxConcurrentConnections">Maximum number of concurrent connections (default: 10).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SqliteConnectionFactory(
        string connectionString,
        int maxConcurrentConnections = 10,
        ILogger<SqliteConnectionFactory>? logger = null
    )
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        if (maxConcurrentConnections < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentConnections), "Must be at least 1.");
        }

        _connectionString = connectionString;
        _semaphore = new SemaphoreSlim(maxConcurrentConnections, maxConcurrentConnections);
        _logger = logger ?? NullLogger<SqliteConnectionFactory>.Instance;
    }

    /// <summary>
    /// Creates and opens a new database connection.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open SQLite connection with WAL mode and foreign keys enabled.</returns>
    /// <remarks>
    /// The connection is wrapped to automatically release the semaphore on disposal.
    /// Waits for an available connection slot if max connections are in use.
    /// </remarks>
    public async Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Wait for an available connection slot
        await _semaphore.WaitAsync(ct);

        ManagedSqliteConnection? connection = null;
        try
        {
            // Create managed connection that will release semaphore on disposal
            connection = new ManagedSqliteConnection(_connectionString, _semaphore, _logger);

            // Initialize the connection (open and configure)
            await connection.InitializeAsync(ct);

            _logger.LogTrace("Created SQLite connection with WAL mode and foreign keys enabled");

            return connection;
        }
        catch
        {
            // Release semaphore if connection creation failed
            connection?.Dispose();
            _ = _semaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Disposes the connection factory and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _semaphore.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Managed SQLite connection that automatically releases semaphore on disposal.
    /// </summary>
    /// <remarks>
    /// Uses composition pattern to avoid base constructor initialization issues.
    /// The connection is initialized via InitializeAsync after construction.
    /// </remarks>
    private sealed class ManagedSqliteConnection : SqliteConnection
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly ILogger _logger;
        private bool _disposed;

        public ManagedSqliteConnection(string connectionString, SemaphoreSlim semaphore, ILogger logger)
            : base(connectionString)
        {
            _semaphore = semaphore;
            _logger = logger;
        }

        /// <summary>
        /// Initializes the connection by opening it and configuring WAL mode and foreign keys.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <remarks>
        /// Must be called after construction before using the connection.
        /// </remarks>
        public async Task InitializeAsync(CancellationToken ct)
        {
            await OpenAsync(ct);

            // Enable WAL mode for better concurrency
            using var walCommand = CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            _ = await walCommand.ExecuteNonQueryAsync(ct);

            // Enable foreign keys
            using var fkCommand = CreateCommand();
            fkCommand.CommandText = "PRAGMA foreign_keys=ON;";
            _ = await fkCommand.ExecuteNonQueryAsync(ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _ = (_semaphore?.Release());
                _logger.LogTrace("Released SQLite connection and semaphore slot");
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _ = (_semaphore?.Release());
            _logger.LogTrace("Released SQLite connection and semaphore slot (async)");
            _disposed = true;

            await base.DisposeAsync();
        }
    }
}
