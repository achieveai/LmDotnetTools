using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace MemoryServer.Infrastructure;

/// <summary>
/// Test implementation of ISqliteSessionFactory that provides complete test isolation.
/// Each session gets a unique database file that is automatically cleaned up.
/// </summary>
public class TestSqliteSessionFactory : ISqliteSessionFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TestSqliteSessionFactory> _logger;
    private readonly string _testDirectory;
    private readonly string _sharedDatabasePath;
    private readonly string _sharedConnectionString;
    private readonly ConcurrentDictionary<string, DateTime> _activeSessions;
    private readonly object _metricsLock = new();

    private int _totalSessionsCreated;
    private int _failedSessionCreations;
    private readonly List<double> _sessionCreationTimes = new();
    private readonly List<double> _sessionLifetimes = new();

    public TestSqliteSessionFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<TestSqliteSessionFactory>();
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "MemoryServerTests",
            Guid.NewGuid().ToString("N")[..8]
        );
        _activeSessions = new ConcurrentDictionary<string, DateTime>();

        // Create a single shared database file for this test factory instance
        var factoryId = Guid.NewGuid().ToString("N")[..8];
        _sharedDatabasePath = Path.Combine(_testDirectory, $"test_memory_shared_{factoryId}.db");
        _sharedConnectionString =
            $"Data Source={_sharedDatabasePath};Mode=ReadWriteCreate;Cache=Shared;";

        // Ensure test directory exists
        Directory.CreateDirectory(_testDirectory);

        _logger.LogDebug(
            "Test SQLite session factory created with shared database: {SharedDatabasePath}",
            _sharedDatabasePath
        );
    }

    public async Task<ISqliteSession> CreateSessionAsync(
        CancellationToken cancellationToken = default
    )
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Use the shared database file for all sessions from this factory
            var sessionId = Guid.NewGuid().ToString("N")[..8];

            var session = new TestSqliteSession(
                _sharedConnectionString,
                _sharedDatabasePath,
                _loggerFactory.CreateLogger<TestSqliteSession>()
            );

            // Initialize the database schema only if this is the first session
            await session.InitializeAsync(cancellationToken);

            // Track session for metrics
            _activeSessions[session.SessionId] = DateTime.UtcNow;

            // Update metrics
            lock (_metricsLock)
            {
                _totalSessionsCreated++;
                _sessionCreationTimes.Add(stopwatch.ElapsedMilliseconds);

                // Keep only recent metrics (last 1000 entries)
                if (_sessionCreationTimes.Count > 1000)
                {
                    _sessionCreationTimes.RemoveAt(0);
                }
            }

            _logger.LogDebug(
                "Created test session {SessionId} using shared database {DatabasePath} in {ElapsedMs}ms",
                session.SessionId,
                _sharedDatabasePath,
                stopwatch.ElapsedMilliseconds
            );

            return new TrackedTestSqliteSession(session, this);
        }
        catch (Exception ex)
        {
            lock (_metricsLock)
            {
                _failedSessionCreations++;
            }

            _logger.LogError(
                ex,
                "Failed to create test session after {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    public Task<ISqliteSession> CreateSessionAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentException(
                "Connection string cannot be null or empty",
                nameof(connectionString)
            );

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Extract database path from connection string for cleanup
            var databasePath = ExtractDatabasePath(connectionString);

            var session = new TestSqliteSession(
                connectionString,
                databasePath,
                _loggerFactory.CreateLogger<TestSqliteSession>()
            );

            // Track session for metrics
            _activeSessions[session.SessionId] = DateTime.UtcNow;

            lock (_metricsLock)
            {
                _totalSessionsCreated++;
                _sessionCreationTimes.Add(stopwatch.ElapsedMilliseconds);

                if (_sessionCreationTimes.Count > 1000)
                {
                    _sessionCreationTimes.RemoveAt(0);
                }
            }

            _logger.LogDebug(
                "Created test session {SessionId} with custom connection string in {ElapsedMs}ms",
                session.SessionId,
                stopwatch.ElapsedMilliseconds
            );

            return Task.FromResult<ISqliteSession>(new TrackedTestSqliteSession(session, this));
        }
        catch (Exception ex)
        {
            lock (_metricsLock)
            {
                _failedSessionCreations++;
            }

            _logger.LogError(
                ex,
                "Failed to create test session with custom connection string after {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    public Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        // For test factory, each session initializes its own database
        // This method is a no-op as initialization happens per session
        return Task.CompletedTask;
    }

    public Task<SessionPerformanceMetrics> GetMetricsAsync(
        CancellationToken cancellationToken = default
    )
    {
        lock (_metricsLock)
        {
            var metrics = new SessionPerformanceMetrics
            {
                TotalSessionsCreated = _totalSessionsCreated,
                ActiveSessions = _activeSessions.Count,
                FailedSessionCreations = _failedSessionCreations,
                ConnectionLeaksDetected = 0, // TODO: Implement leak detection
                LastUpdated = DateTime.UtcNow,
            };

            if (_sessionCreationTimes.Count > 0)
            {
                metrics.AverageSessionCreationTimeMs = _sessionCreationTimes.Average();
            }

            if (_sessionLifetimes.Count > 0)
            {
                metrics.AverageSessionLifetimeMs = _sessionLifetimes.Average();
            }

            return Task.FromResult(metrics);
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await CreateSessionAsync(cancellationToken);

            var health = await session.GetHealthAsync(cancellationToken);
            return health.IsHealthy;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test health check failed");
            return false;
        }
    }

    internal void OnSessionDisposed(string sessionId)
    {
        if (_activeSessions.TryRemove(sessionId, out var createdAt))
        {
            var lifetime = (DateTime.UtcNow - createdAt).TotalMilliseconds;

            lock (_metricsLock)
            {
                _sessionLifetimes.Add(lifetime);

                // Keep only recent metrics
                if (_sessionLifetimes.Count > 1000)
                {
                    _sessionLifetimes.RemoveAt(0);
                }
            }

            _logger.LogDebug(
                "Test session {SessionId} disposed after {LifetimeMs}ms",
                sessionId,
                lifetime
            );
        }
    }

    /// <summary>
    /// Cleans up all test database files and directories.
    /// Should be called during test teardown.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
                _logger.LogDebug("Cleaned up test directory: {TestDirectory}", _testDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up test directory: {TestDirectory}",
                _testDirectory
            );
        }
    }

    private static string ExtractDatabasePath(string connectionString)
    {
        // Simple extraction of Data Source from connection string
        var parts = connectionString.Split(';');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring("Data Source=".Length);
            }
        }
        return string.Empty;
    }
}

/// <summary>
/// Test implementation of ISqliteSession with automatic database file cleanup.
/// </summary>
public class TestSqliteSession : ISqliteSession
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ILogger<TestSqliteSession> _logger;
    private readonly Stopwatch _sessionStopwatch;
    private SqliteConnection? _connection;
    private bool _disposed;
    private int _operationCount;
    private DateTime _lastActivity;

    public string SessionId { get; }
    public bool IsDisposed => _disposed;

    public TestSqliteSession(
        string connectionString,
        string databasePath,
        ILogger<TestSqliteSession> logger
    )
    {
        _connectionString =
            connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        SessionId = Guid.NewGuid().ToString("N")[..8];
        _sessionStopwatch = Stopwatch.StartNew();
        _lastActivity = DateTime.UtcNow;

        _logger.LogDebug(
            "Test SQLite session {SessionId} created for database {DatabasePath}",
            SessionId,
            _databasePath
        );
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectionAsync(cancellationToken);

        await ExecuteAsync(
            async connection =>
            {
                await ExecuteSchemaScriptsAsync(connection, cancellationToken);
            },
            cancellationToken
        );

        _logger.LogDebug("Test session {SessionId} database initialized", SessionId);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        await EnsureConnectionAsync(cancellationToken);

        try
        {
            _operationCount++;
            _lastActivity = DateTime.UtcNow;

            _logger.LogDebug(
                "Executing operation {OperationCount} in test session {SessionId}",
                _operationCount,
                SessionId
            );

            var result = await operation(_connection!);

            _logger.LogDebug(
                "Operation {OperationCount} completed successfully in test session {SessionId}",
                _operationCount,
                SessionId
            );

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error executing operation {OperationCount} in test session {SessionId}",
                _operationCount,
                SessionId
            );
            throw;
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        if (operation == null)
            throw new ArgumentNullException(nameof(operation));

        await EnsureConnectionAsync(cancellationToken);

        using var transaction = _connection!.BeginTransaction();
        try
        {
            _operationCount++;
            _lastActivity = DateTime.UtcNow;

            _logger.LogDebug(
                "Executing transactional operation {OperationCount} in test session {SessionId}",
                _operationCount,
                SessionId
            );

            var result = await operation(_connection, transaction);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug(
                "Transactional operation {OperationCount} committed successfully in test session {SessionId}",
                _operationCount,
                SessionId
            );

            return result;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(
                ex,
                "Transactional operation {OperationCount} rolled back in test session {SessionId}",
                _operationCount,
                SessionId
            );
            throw;
        }
    }

    public async Task ExecuteAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken = default
    )
    {
        await ExecuteAsync(
            async conn =>
            {
                await operation(conn);
                return true;
            },
            cancellationToken
        );
    }

    public async Task ExecuteInTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task> operation,
        CancellationToken cancellationToken = default
    )
    {
        await ExecuteInTransactionAsync(
            async (conn, trans) =>
            {
                await operation(conn, trans);
                return true;
            },
            cancellationToken
        );
    }

    public Task<SessionHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = new SessionHealthStatus
        {
            IsHealthy = !_disposed && _connection?.State == System.Data.ConnectionState.Open,
            ConnectionState = _connection?.State.ToString() ?? "NotCreated",
            CreatedAt = DateTime.UtcNow - _sessionStopwatch.Elapsed,
            LastActivity = _lastActivity,
            OperationCount = _operationCount,
        };

        if (_disposed)
        {
            health.ErrorMessage = "Session is disposed";
        }
        else if (_connection?.State != System.Data.ConnectionState.Open && _connection != null)
        {
            health.ErrorMessage = $"Connection is in {_connection.State} state";
        }

        return Task.FromResult(health);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _logger.LogDebug(
            "Disposing test SQLite session {SessionId} after {ElapsedMs}ms with {OperationCount} operations",
            SessionId,
            _sessionStopwatch.ElapsedMilliseconds,
            _operationCount
        );

        try
        {
            if (_connection != null && _connection.State == System.Data.ConnectionState.Open)
            {
                // Force WAL checkpoint before closing
                try
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                    await cmd.ExecuteNonQueryAsync();
                    _logger.LogDebug(
                        "WAL checkpoint completed for test session {SessionId}",
                        SessionId
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to execute WAL checkpoint during disposal of test session {SessionId}",
                        SessionId
                    );
                }

                await _connection.DisposeAsync();
                _logger.LogDebug("Connection disposed for test session {SessionId}", SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal of test session {SessionId}", SessionId);
        }
        finally
        {
            _connection = null;
            _disposed = true;
            _sessionStopwatch.Stop();

            // Clean up database files
            await CleanupDatabaseFilesAsync();

            _logger.LogDebug(
                "Test SQLite session {SessionId} disposed successfully after {ElapsedMs}ms",
                SessionId,
                _sessionStopwatch.ElapsedMilliseconds
            );
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(
                nameof(TestSqliteSession),
                $"Test session {SessionId} is disposed"
            );

        if (_connection == null)
        {
            _logger.LogDebug("Creating connection for test session {SessionId}", SessionId);

            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken);

            // Configure connection for testing (no extensions needed for basic tests)
            await ConfigureTestConnectionAsync(_connection, cancellationToken);

            _logger.LogDebug("Connection established for test session {SessionId}", SessionId);
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _logger.LogWarning(
                "Connection for test session {SessionId} is in {State} state, recreating",
                SessionId,
                _connection.State
            );

            await _connection.DisposeAsync();
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken);
            await ConfigureTestConnectionAsync(_connection, cancellationToken);
        }
    }

    private async Task ConfigureTestConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        // Configure SQLite pragmas for testing (optimized for speed and isolation)
        var pragmas = new[]
        {
            "PRAGMA journal_mode=WAL", // Enable WAL mode for better concurrency
            "PRAGMA synchronous=NORMAL", // Balance between safety and performance
            "PRAGMA cache_size=1000", // Smaller cache for tests
            "PRAGMA foreign_keys=ON", // Enable foreign key constraints
            "PRAGMA temp_store=MEMORY", // Store temporary tables in memory
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
                _logger.LogWarning(
                    ex,
                    "Failed to execute pragma '{Pragma}' for test session {SessionId}",
                    pragma,
                    SessionId
                );
            }
        }

        _logger.LogDebug(
            "Test connection configured with pragmas for session {SessionId}",
            SessionId
        );
    }

    private async Task CleanupDatabaseFilesAsync()
    {
        try
        {
            // Wait a bit to ensure all file handles are released
            await Task.Delay(100);

            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
                _logger.LogDebug("Deleted test database file: {DatabasePath}", _databasePath);
            }

            // Clean up WAL and SHM files
            var walPath = _databasePath + "-wal";
            var shmPath = _databasePath + "-shm";

            if (File.Exists(walPath))
            {
                File.Delete(walPath);
                _logger.LogDebug("Deleted WAL file: {WalPath}", walPath);
            }

            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
                _logger.LogDebug("Deleted SHM file: {ShmPath}", shmPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up test database files for session {SessionId}",
                SessionId
            );
        }
    }

    private async Task ExecuteSchemaScriptsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        // Use the same schema scripts as production but optimized for testing
        var schemaScripts = GetTestSchemaScripts();

        foreach (var script in schemaScripts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogDebug(
            "Test schema scripts executed successfully for session {SessionId}",
            SessionId
        );
    }

    private static string[] GetTestSchemaScripts()
    {
        // Same schema as production but without some optimizations that aren't needed for tests
        return new[]
        {
            @"CREATE TABLE IF NOT EXISTS memory_id_sequence (
                id INTEGER PRIMARY KEY AUTOINCREMENT
            )",
            @"CREATE TABLE IF NOT EXISTS memories (
                id INTEGER PRIMARY KEY,
                content TEXT NOT NULL,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                metadata TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                version INTEGER DEFAULT 1
            )",
            @"CREATE TABLE IF NOT EXISTS memory_embeddings (
                memory_id INTEGER PRIMARY KEY,
                embedding BLOB NOT NULL,
                dimension INTEGER NOT NULL,
                FOREIGN KEY (memory_id) REFERENCES memories(id) ON DELETE CASCADE
            )",
            @"CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
                content,
                metadata,
                content='memories',
                content_rowid='id'
            )",
            @"CREATE TABLE IF NOT EXISTS entities (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT,
                aliases TEXT,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                confidence REAL DEFAULT 1.0,
                source_memory_ids TEXT,
                metadata TEXT,
                version INTEGER DEFAULT 1
            )",
            @"CREATE TABLE IF NOT EXISTS relationships (
                id INTEGER PRIMARY KEY,
                source_entity_name TEXT NOT NULL,
                relationship_type TEXT NOT NULL,
                target_entity_name TEXT NOT NULL,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                confidence REAL DEFAULT 1.0,
                source_memory_id TEXT,
                temporal_context TEXT,
                metadata TEXT,
                version INTEGER DEFAULT 1
            )",
            // Basic indexes for testing
            @"CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_entities_session ON entities(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_relationships_session ON relationships(user_id, agent_id, run_id)",
            // FTS5 triggers for automatic content indexing
            @"CREATE TRIGGER IF NOT EXISTS memories_fts_insert AFTER INSERT ON memories BEGIN
                INSERT INTO memory_fts(rowid, content, metadata) VALUES (new.id, new.content, new.metadata);
            END",
            @"CREATE TRIGGER IF NOT EXISTS memories_fts_update AFTER UPDATE ON memories BEGIN
                UPDATE memory_fts SET content = new.content, metadata = new.metadata WHERE rowid = new.id;
            END",
            @"CREATE TRIGGER IF NOT EXISTS memories_fts_delete AFTER DELETE ON memories BEGIN
                DELETE FROM memory_fts WHERE rowid = old.id;
            END",
            // Document Segmentation tables (for testing)
            @"CREATE TABLE IF NOT EXISTS document_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                parent_document_id INTEGER NOT NULL,
                segment_id TEXT UNIQUE NOT NULL,
                sequence_number INTEGER NOT NULL,
                content TEXT NOT NULL,
                title TEXT,
                summary TEXT,
                coherence_score REAL DEFAULT 0.0,
                independence_score REAL DEFAULT 0.0,
                topic_consistency_score REAL DEFAULT 0.0,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                metadata TEXT
            )",
            @"CREATE TABLE IF NOT EXISTS segment_relationships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_segment_id TEXT NOT NULL,
                target_segment_id TEXT NOT NULL,
                relationship_type TEXT NOT NULL,
                strength REAL DEFAULT 1.0,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                metadata TEXT
            )",
            // Document segments FTS5 table for full-text search (testing)
            @"CREATE VIRTUAL TABLE IF NOT EXISTS document_segments_fts USING fts5(
                content,
                title,
                summary,
                content='document_segments',
                content_rowid='id'
            )",
            // FTS5 triggers for document segments automatic content indexing
            @"CREATE TRIGGER IF NOT EXISTS document_segments_fts_insert AFTER INSERT ON document_segments BEGIN
                INSERT INTO document_segments_fts(rowid, content, title, summary) VALUES (new.id, new.content, new.title, new.summary);
            END",
            @"CREATE TRIGGER IF NOT EXISTS document_segments_fts_update AFTER UPDATE ON document_segments BEGIN
                UPDATE document_segments_fts SET content = new.content, title = new.title, summary = new.summary WHERE rowid = new.id;
            END",
            @"CREATE TRIGGER IF NOT EXISTS document_segments_fts_delete AFTER DELETE ON document_segments BEGIN
                DELETE FROM document_segments_fts WHERE rowid = old.id;
            END",
            // Test indexes for document segments
            @"CREATE INDEX IF NOT EXISTS idx_test_document_segments_parent ON document_segments(parent_document_id)",
            @"CREATE INDEX IF NOT EXISTS idx_test_document_segments_session ON document_segments(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_test_segment_relationships_session ON segment_relationships(user_id, agent_id, run_id)",
        };
    }
}

/// <summary>
/// Wrapper for TestSqliteSession that tracks disposal for metrics.
/// </summary>
internal class TrackedTestSqliteSession : ISqliteSession
{
    private readonly ISqliteSession _innerSession;
    private readonly TestSqliteSessionFactory _factory;
    private bool _disposed;

    public TrackedTestSqliteSession(ISqliteSession innerSession, TestSqliteSessionFactory factory)
    {
        _innerSession = innerSession;
        _factory = factory;
    }

    public string SessionId => _innerSession.SessionId;
    public bool IsDisposed => _disposed || _innerSession.IsDisposed;

    public Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, Task<T>> operation,
        CancellationToken cancellationToken = default
    ) => _innerSession.ExecuteAsync(operation, cancellationToken);

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, Task<T>> operation,
        CancellationToken cancellationToken = default
    ) => _innerSession.ExecuteInTransactionAsync(operation, cancellationToken);

    public Task ExecuteAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken = default
    ) => _innerSession.ExecuteAsync(operation, cancellationToken);

    public Task ExecuteInTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task> operation,
        CancellationToken cancellationToken = default
    ) => _innerSession.ExecuteInTransactionAsync(operation, cancellationToken);

    public Task<SessionHealthStatus> GetHealthAsync(
        CancellationToken cancellationToken = default
    ) => _innerSession.GetHealthAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _factory.OnSessionDisposed(SessionId);
            await _innerSession.DisposeAsync();
            _disposed = true;
        }
    }
}
