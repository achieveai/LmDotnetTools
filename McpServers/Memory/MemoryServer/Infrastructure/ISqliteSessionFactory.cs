namespace MemoryServer.Infrastructure;

/// <summary>
/// Factory for creating database sessions with proper lifecycle management.
/// Supports both production and test environments with appropriate configurations.
/// </summary>
public interface ISqliteSessionFactory
{
    /// <summary>
    /// Creates a new database session with the default connection string.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>New database session</returns>
    Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new database session with a specific connection string.
    /// </summary>
    /// <param name="connectionString">SQLite connection string</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>New database session</returns>
    Task<ISqliteSession> CreateSessionAsync(string connectionString, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Initializes the database schema if not already initialized.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets factory performance metrics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Performance metrics</returns>
    Task<SessionPerformanceMetrics> GetMetricsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Performs a health check on the factory and database connectivity.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Health check result</returns>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Performance metrics for session factory operations.
/// </summary>
public class SessionPerformanceMetrics
{
    /// <summary>
    /// Total number of sessions created.
    /// </summary>
    public int TotalSessionsCreated { get; set; }
    
    /// <summary>
    /// Number of currently active sessions.
    /// </summary>
    public int ActiveSessions { get; set; }
    
    /// <summary>
    /// Average session creation time in milliseconds.
    /// </summary>
    public double AverageSessionCreationTimeMs { get; set; }
    
    /// <summary>
    /// Average session lifetime in milliseconds.
    /// </summary>
    public double AverageSessionLifetimeMs { get; set; }
    
    /// <summary>
    /// Number of failed session creations.
    /// </summary>
    public int FailedSessionCreations { get; set; }
    
    /// <summary>
    /// Number of connection leaks detected.
    /// </summary>
    public int ConnectionLeaksDetected { get; set; }
    
    /// <summary>
    /// Last metrics collection timestamp.
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
} 