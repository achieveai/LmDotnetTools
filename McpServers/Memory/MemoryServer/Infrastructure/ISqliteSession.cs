using Microsoft.Data.Sqlite;

namespace MemoryServer.Infrastructure;

/// <summary>
/// Represents a database session that encapsulates SQLite connection lifecycle management.
/// Provides a single connection per session with proper resource cleanup and transaction support.
/// </summary>
public interface ISqliteSession : IAsyncDisposable
{
    /// <summary>
    /// Executes an operation with the session's connection.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">Operation to execute with the connection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the operation</returns>
    Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation within a transaction.
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">Operation to execute with connection and transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the operation</returns>
    Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation with the session's connection (void return).
    /// </summary>
    /// <param name="operation">Operation to execute with the connection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ExecuteAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation within a transaction (void return).
    /// </summary>
    /// <param name="operation">Operation to execute with connection and transaction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ExecuteInTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets session health information.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session health status</returns>
    Task<SessionHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the session identifier for tracking and debugging.
    /// </summary>
    string SessionId { get; }

    /// <summary>
    /// Gets whether the session is disposed.
    /// </summary>
    bool IsDisposed { get; }
}

/// <summary>
/// Represents the health status of a database session.
/// </summary>
public class SessionHealthStatus
{
    /// <summary>
    /// Whether the session is healthy and operational.
    /// </summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// The current connection state.
    /// </summary>
    public string ConnectionState { get; set; } = string.Empty;

    /// <summary>
    /// Session creation timestamp.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last activity timestamp.
    /// </summary>
    public DateTime LastActivity { get; set; }

    /// <summary>
    /// Number of operations executed in this session.
    /// </summary>
    public int OperationCount { get; set; }

    /// <summary>
    /// Any error messages or warnings.
    /// </summary>
    public string? ErrorMessage { get; set; }
}