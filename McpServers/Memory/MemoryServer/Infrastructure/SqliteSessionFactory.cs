using System.Collections.Concurrent;
using System.Diagnostics;
using MemoryServer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemoryServer.Infrastructure;

/// <summary>
/// Production implementation of ISqliteSessionFactory with proper session management and metrics.
/// </summary>
public class SqliteSessionFactory : ISqliteSessionFactory
{
    private readonly string _connectionString;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SqliteSessionFactory> _logger;
    private readonly SemaphoreSlim _initializationSemaphore;
    private readonly ConcurrentDictionary<string, DateTime> _activeSessions;
    private readonly object _metricsLock = new();

    private bool _isInitialized;
    private int _totalSessionsCreated;
    private int _failedSessionCreations;
    private readonly List<double> _sessionCreationTimes = [];
    private readonly List<double> _sessionLifetimes = [];

    public SqliteSessionFactory(IOptions<DatabaseOptions> options, ILoggerFactory loggerFactory)
    {
        var databaseOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _connectionString =
            databaseOptions.ConnectionString ?? throw new ArgumentException("Connection string is required");
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<SqliteSessionFactory>();

        _initializationSemaphore = new SemaphoreSlim(1, 1);
        _activeSessions = new ConcurrentDictionary<string, DateTime>();

        _logger.LogDebug(
            "SQLite session factory created with connection string: {ConnectionString}",
            MaskConnectionString(_connectionString)
        );
    }

    public async Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var session = new SqliteSession(_connectionString, _loggerFactory.CreateLogger<SqliteSession>());

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
                "Created session {SessionId} in {ElapsedMs}ms",
                session.SessionId,
                stopwatch.ElapsedMilliseconds
            );

            // Wrap session to track disposal
            return new TrackedSqliteSession(session, this);
        }
        catch (Exception ex)
        {
            lock (_metricsLock)
            {
                _failedSessionCreations++;
            }

            _logger.LogError(ex, "Failed to create session after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public Task<ISqliteSession> CreateSessionAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var session = new SqliteSession(connectionString, _loggerFactory.CreateLogger<SqliteSession>());

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
                "Created session {SessionId} with custom connection string in {ElapsedMs}ms",
                session.SessionId,
                stopwatch.ElapsedMilliseconds
            );

            return Task.FromResult<ISqliteSession>(new TrackedSqliteSession(session, this));
        }
        catch (Exception ex)
        {
            lock (_metricsLock)
            {
                _failedSessionCreations++;
            }

            _logger.LogError(
                ex,
                "Failed to create session with custom connection string after {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    /// <summary>
    /// Initializes the database schema and applies any necessary migrations.
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            _logger.LogInformation("Initializing database schema...");

            // Create session directly without going through EnsureInitializedAsync to avoid circular dependency
            var session = new SqliteSession(_connectionString, _loggerFactory.CreateLogger<SqliteSession>());
            await using var _ = session;

            await session.ExecuteAsync(
                async connection =>
                {
                    // Create all tables first
                    foreach (var script in GetSchemaScripts())
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = script;
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }

                    // Apply migrations for existing databases
                    await ApplyMigrationsAsync(connection, cancellationToken);
                },
                cancellationToken
            );

            _isInitialized = true;
            _logger.LogInformation("Database schema initialized successfully");
        }
        finally
        {
            _ = _initializationSemaphore.Release();
        }
    }

    /// <summary>
    /// Applies database migrations for schema updates.
    /// </summary>
    private static Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // No migrations needed after removing session_defaults table
        return Task.CompletedTask;
    }

    public Task<SessionPerformanceMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
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
            _logger.LogError(ex, "Health check failed");
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

            _logger.LogDebug("Session {SessionId} disposed after {LifetimeMs}ms", sessionId, lifetime);
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_isInitialized)
        {
            await InitializeDatabaseAsync(cancellationToken);
        }
    }

    private async Task ExecuteSchemaScriptsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var schemaScripts = GetSchemaScripts();

        foreach (var script in schemaScripts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = script;
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogDebug("Schema scripts executed successfully");
    }

    private static string[] GetSchemaScripts()
    {
        return
        [
            // ID sequence table for generating unique integers
            @"CREATE TABLE IF NOT EXISTS memory_id_sequence (
                id INTEGER PRIMARY KEY AUTOINCREMENT
            )",
            // Main memories table with integer primary key
            @"CREATE TABLE IF NOT EXISTS memories (
                id INTEGER PRIMARY KEY,
                content TEXT NOT NULL,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                metadata TEXT, -- JSON
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                version INTEGER DEFAULT 1,
                CONSTRAINT chk_content_length CHECK (length(content) <= 10000),
                CONSTRAINT chk_user_id_format CHECK (length(user_id) > 0 AND length(user_id) <= 100)
            )",
            // Vector embeddings using sqlite-vec (primary approach)
            @"                CREATE VIRTUAL TABLE IF NOT EXISTS memory_embeddings USING vec0(
                    memory_id INTEGER PRIMARY KEY,
                    embedding FLOAT[1024]
                );",
            // FTS5 virtual table for full-text search
            @"CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
                content,
                metadata,
                content='memories',
                content_rowid='id'
            )",
            // Graph entities table
            @"CREATE TABLE IF NOT EXISTS entities (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT,
                aliases TEXT, -- JSON array
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                confidence REAL DEFAULT 1.0,
                source_memory_ids TEXT, -- JSON array
                metadata TEXT, -- JSON
                version INTEGER DEFAULT 1
            )",
            // Graph relationships table
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
                metadata TEXT, -- JSON
                version INTEGER DEFAULT 1
            )",
            // Performance indexes
            @"CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_memories_created ON memories(created_at DESC)",
            @"CREATE INDEX IF NOT EXISTS idx_entities_session ON entities(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name)",
            @"CREATE INDEX IF NOT EXISTS idx_relationships_session ON relationships(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_relationships_entities ON relationships(source_entity_name, target_entity_name)",
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
            // Document Segmentation tables (Phase 1)
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
                metadata TEXT, -- JSON
                CONSTRAINT chk_document_segments_content_length CHECK (length(content) <= 50000),
                CONSTRAINT chk_document_segments_coherence_score CHECK (coherence_score >= 0.0 AND coherence_score <= 1.0),
                CONSTRAINT chk_document_segments_independence_score CHECK (independence_score >= 0.0 AND independence_score <= 1.0),
                CONSTRAINT chk_document_segments_topic_consistency_score CHECK (topic_consistency_score >= 0.0 AND topic_consistency_score <= 1.0),
                FOREIGN KEY (parent_document_id) REFERENCES memories(id) ON DELETE CASCADE
            )",
            @"CREATE TABLE IF NOT EXISTS segment_relationships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_segment_id TEXT NOT NULL,
                target_segment_id TEXT NOT NULL,
                relationship_type TEXT NOT NULL, -- 'sequential', 'hierarchical', 'referential', 'topical'
                strength REAL DEFAULT 1.0,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                metadata TEXT, -- JSON
                CONSTRAINT chk_segment_relationships_strength CHECK (strength >= 0.0 AND strength <= 1.0),
                CONSTRAINT chk_segment_relationships_type CHECK (relationship_type IN ('sequential', 'hierarchical', 'referential', 'topical')),
                FOREIGN KEY (source_segment_id) REFERENCES document_segments(segment_id) ON DELETE CASCADE,
                FOREIGN KEY (target_segment_id) REFERENCES document_segments(segment_id) ON DELETE CASCADE
            )",
            // Document segmentation indexes
            @"CREATE INDEX IF NOT EXISTS idx_document_segments_parent ON document_segments(parent_document_id)",
            @"CREATE INDEX IF NOT EXISTS idx_document_segments_session ON document_segments(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_document_segments_sequence ON document_segments(parent_document_id, sequence_number)",
            @"CREATE INDEX IF NOT EXISTS idx_document_segments_created ON document_segments(created_at DESC)",
            @"CREATE INDEX IF NOT EXISTS idx_segment_relationships_source ON segment_relationships(source_segment_id)",
            @"CREATE INDEX IF NOT EXISTS idx_segment_relationships_target ON segment_relationships(target_segment_id)",
            @"CREATE INDEX IF NOT EXISTS idx_segment_relationships_session ON segment_relationships(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_segment_relationships_type ON segment_relationships(relationship_type)",
            // FTS5 virtual table for segment content search
            @"CREATE VIRTUAL TABLE IF NOT EXISTS document_segments_fts USING fts5(
                content,
                title,
                summary,
                metadata,
                content='document_segments',
                content_rowid='id'
            )",
            // FTS5 triggers for document segments
            @"CREATE TRIGGER IF NOT EXISTS document_segments_fts_insert AFTER INSERT ON document_segments BEGIN
                INSERT INTO document_segments_fts(rowid, content, title, summary, metadata) 
                VALUES (new.id, new.content, new.title, new.summary, new.metadata);
            END",
            @"CREATE TRIGGER IF NOT EXISTS document_segments_fts_update AFTER UPDATE ON document_segments BEGIN
                UPDATE document_segments_fts SET 
                    content = new.content, 
                    title = new.title, 
                    summary = new.summary, 
                    metadata = new.metadata 
                WHERE rowid = new.id;
            END",
            @"CREATE TRIGGER IF NOT EXISTS document_segments_fts_delete AFTER DELETE ON document_segments BEGIN
                DELETE FROM document_segments_fts WHERE rowid = old.id;
            END",
        ];
    }

    private static string MaskConnectionString(string connectionString)
    {
        // Simple masking for logging - hide sensitive parts
        if (connectionString.Contains("Password", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString
                .Split(';')
                .Select(part => part.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "Password=***" : part)
                .Aggregate((a, b) => $"{a};{b}");
        }
        return connectionString;
    }
}

/// <summary>
/// Wrapper for SqliteSession that tracks disposal for metrics.
/// </summary>
internal class TrackedSqliteSession : ISqliteSession
{
    private readonly ISqliteSession _innerSession;
    private readonly SqliteSessionFactory _factory;
    private bool _disposed;

    public TrackedSqliteSession(ISqliteSession innerSession, SqliteSessionFactory factory)
    {
        _innerSession = innerSession;
        _factory = factory;
    }

    public string SessionId => _innerSession.SessionId;
    public bool IsDisposed => _disposed || _innerSession.IsDisposed;

    public Task<T> ExecuteAsync<T>(
        Func<Microsoft.Data.Sqlite.SqliteConnection, Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        return _innerSession.ExecuteAsync(operation, cancellationToken);
    }

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, Task<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        return _innerSession.ExecuteInTransactionAsync(operation, cancellationToken);
    }

    public Task ExecuteAsync(
        Func<Microsoft.Data.Sqlite.SqliteConnection, Task> operation,
        CancellationToken cancellationToken = default
    )
    {
        return _innerSession.ExecuteAsync(operation, cancellationToken);
    }

    public Task ExecuteInTransactionAsync(
        Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, Task> operation,
        CancellationToken cancellationToken = default
    )
    {
        return _innerSession.ExecuteInTransactionAsync(operation, cancellationToken);
    }

    public Task<SessionHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return _innerSession.GetHealthAsync(cancellationToken);
    }

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
