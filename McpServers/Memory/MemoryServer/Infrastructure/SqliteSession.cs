using Microsoft.Data.Sqlite;
using System.Data;
using System.Diagnostics;

namespace MemoryServer.Infrastructure;

/// <summary>
/// Production implementation of ISqliteSession with proper resource management.
/// Maintains a single connection per session with guaranteed cleanup and WAL checkpoint handling.
/// </summary>
public class SqliteSession : ISqliteSession
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSession> _logger;
    private readonly Stopwatch _sessionStopwatch;
    private SqliteConnection? _connection;
    private bool _disposed;
    private int _operationCount;
    private DateTime _lastActivity;

    public string SessionId { get; }
    public bool IsDisposed => _disposed;

    public SqliteSession(string connectionString, ILogger<SqliteSession> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        SessionId = Guid.NewGuid().ToString("N")[..8]; // Short session ID for logging
        _sessionStopwatch = Stopwatch.StartNew();
        _lastActivity = DateTime.UtcNow;

        _logger.LogDebug("SQLite session {SessionId} created", SessionId);
    }

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));

        await EnsureConnectionAsync(cancellationToken);

        try
        {
            _operationCount++;
            _lastActivity = DateTime.UtcNow;

            _logger.LogDebug("Executing operation {OperationCount} in session {SessionId}", _operationCount, SessionId);

            var result = await operation(_connection!);

            _logger.LogDebug("Operation {OperationCount} completed successfully in session {SessionId}", _operationCount, SessionId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing operation {OperationCount} in session {SessionId}", _operationCount, SessionId);
            throw;
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));

        await EnsureConnectionAsync(cancellationToken);

        using var transaction = _connection!.BeginTransaction();
        try
        {
            _operationCount++;
            _lastActivity = DateTime.UtcNow;

            _logger.LogDebug("Executing transactional operation {OperationCount} in session {SessionId}", _operationCount, SessionId);

            var result = await operation(_connection, transaction);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Transactional operation {OperationCount} committed successfully in session {SessionId}", _operationCount, SessionId);

            return result;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Transactional operation {OperationCount} rolled back in session {SessionId}", _operationCount, SessionId);
            throw;
        }
    }

    public async Task ExecuteAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async conn =>
        {
            await operation(conn);
            return true;
        }, cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> operation, CancellationToken cancellationToken = default)
    {
        await ExecuteInTransactionAsync(async (conn, trans) =>
        {
            await operation(conn, trans);
            return true;
        }, cancellationToken);
    }

    public Task<SessionHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = new SessionHealthStatus
        {
            IsHealthy = !_disposed && _connection?.State == ConnectionState.Open,
            ConnectionState = _connection?.State.ToString() ?? "NotCreated",
            CreatedAt = DateTime.UtcNow - _sessionStopwatch.Elapsed,
            LastActivity = _lastActivity,
            OperationCount = _operationCount
        };

        if (_disposed)
        {
            health.ErrorMessage = "Session is disposed";
        }
        else if (_connection?.State != ConnectionState.Open && _connection != null)
        {
            health.ErrorMessage = $"Connection is in {_connection.State} state";
        }

        return Task.FromResult(health);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _logger.LogDebug("Disposing SQLite session {SessionId} after {ElapsedMs}ms with {OperationCount} operations",
            SessionId, _sessionStopwatch.ElapsedMilliseconds, _operationCount);

        try
        {
            if (_connection != null && _connection.State == ConnectionState.Open)
            {
                // Force WAL checkpoint before closing to ensure all data is written
                try
                {
                    await ExecuteAsync(async conn =>
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                        await cmd.ExecuteNonQueryAsync();
                        _logger.LogDebug("WAL checkpoint completed for session {SessionId}", SessionId);
                        return true;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to execute WAL checkpoint during disposal of session {SessionId}", SessionId);
                }

                await _connection.DisposeAsync();
                _logger.LogDebug("Connection disposed for session {SessionId}", SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal of session {SessionId}", SessionId);
        }
        finally
        {
            _connection = null;
            _disposed = true;
            _sessionStopwatch.Stop();

            _logger.LogDebug("SQLite session {SessionId} disposed successfully after {ElapsedMs}ms",
                SessionId, _sessionStopwatch.ElapsedMilliseconds);
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqliteSession), $"Session {SessionId} is disposed");

        if (_connection == null)
        {
            _logger.LogDebug("Creating connection for session {SessionId}", SessionId);

            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken);

            // Configure connection for optimal performance and reliability
            await ConfigureConnectionAsync(_connection, cancellationToken);

            _logger.LogDebug("Connection established for session {SessionId}", SessionId);
        }
        else if (_connection.State != ConnectionState.Open)
        {
            _logger.LogWarning("Connection for session {SessionId} is in {State} state, recreating", SessionId, _connection.State);

            await _connection.DisposeAsync();
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(_connection, cancellationToken);
        }
    }

    private async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Load sqlite-vec extension (required for vector functionality)
        try
        {
            connection.EnableExtensions(true);

            // Load sqlite-vec extension - this is required for vector functionality
            connection.LoadExtension("vec0");
            _logger.LogInformation("sqlite-vec extension loaded successfully for session {SessionId}", SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sqlite-vec extension for session {SessionId}. Vector functionality requires this extension.", SessionId);
            throw new InvalidOperationException("sqlite-vec extension is required for vector functionality but could not be loaded. Ensure the sqlite-vec NuGet package is properly installed.", ex);
        }

        // Configure SQLite pragmas for optimal performance
        var pragmas = new[]
        {
            "PRAGMA journal_mode=WAL",           // Enable WAL mode for better concurrency
            "PRAGMA synchronous=NORMAL",        // Balance between safety and performance
            "PRAGMA cache_size=10000",          // Increase cache size for better performance
            "PRAGMA foreign_keys=ON",           // Enable foreign key constraints
            "PRAGMA temp_store=MEMORY",         // Store temporary tables in memory
            "PRAGMA mmap_size=268435456"        // Enable memory-mapped I/O (256MB)
        };

        foreach (var pragma in pragmas)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = pragma;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to execute pragma '{Pragma}' for session {SessionId}", pragma, SessionId);
            }
        }

        _logger.LogDebug("Connection configured with performance pragmas for session {SessionId}", SessionId);
    }
}