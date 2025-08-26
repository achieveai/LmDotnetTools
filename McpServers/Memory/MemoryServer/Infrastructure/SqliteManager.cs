using System.Text;
using MemoryServer.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MemoryServer.Infrastructure;

/// <summary>
/// Manages SQLite database connections, initialization, and extension loading.
/// Handles connection pooling and ensures proper database setup.
/// </summary>
public class SqliteManager : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteManager> _logger;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly SemaphoreSlim _initSemaphore;
    private readonly DatabaseOptions _options;
    private bool _isInitialized = false;
    private readonly object _initLock = new object();

    public SqliteManager(IOptions<DatabaseOptions> options, ILogger<SqliteManager> logger)
    {
        _options = options.Value;
        _connectionString = _options.ConnectionString;
        _logger = logger;
        _connectionSemaphore = new SemaphoreSlim(_options.MaxConnections, _options.MaxConnections);
        _initSemaphore = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Gets a database connection with proper configuration.
    /// </summary>
    public async Task<ManagedSqliteConnection> GetConnectionAsync(
        CancellationToken cancellationToken = default
    )
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var taskId = System.Threading.Tasks.Task.CurrentId ?? -1;

        _logger.LogDebug(
            "🔄 GetConnectionAsync START - Thread: {ThreadId}, Task: {TaskId}, Available: {Available}/{Max}",
            threadId,
            taskId,
            _connectionSemaphore.CurrentCount,
            _options.MaxConnections
        );

        await _connectionSemaphore.WaitAsync(cancellationToken);
        _logger.LogDebug(
            "✅ Semaphore acquired - Thread: {ThreadId}, Task: {TaskId}, Elapsed: {Elapsed}ms",
            threadId,
            taskId,
            stopwatch.ElapsedMilliseconds
        );

        try
        {
            _logger.LogDebug(
                "🔌 Creating connection - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );
            var connection = new SqliteConnection(_connectionString);

            _logger.LogDebug(
                "📡 Opening connection - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );
            await connection.OpenAsync(cancellationToken);
            _logger.LogDebug(
                "✅ Connection opened - Thread: {ThreadId}, Task: {TaskId}, State: {State}",
                threadId,
                taskId,
                connection.State
            );

            // Configure connection settings
            _logger.LogDebug(
                "⚙️ Configuring connection - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );
            await ConfigureConnectionAsync(connection, cancellationToken);
            _logger.LogDebug(
                "✅ Connection configured - Thread: {ThreadId}, Task: {TaskId}, Total Elapsed: {Elapsed}ms",
                threadId,
                taskId,
                stopwatch.ElapsedMilliseconds
            );

            // Return managed connection that will auto-release semaphore
            return new ManagedSqliteConnection(connection, this);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "❌ GetConnectionAsync FAILED - Thread: {ThreadId}, Task: {TaskId}, Elapsed: {Elapsed}ms",
                threadId,
                taskId,
                stopwatch.ElapsedMilliseconds
            );
            _connectionSemaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Releases a database connection back to the pool.
    /// This is called automatically by ManagedSqliteConnection.Dispose().
    /// </summary>
    internal void ReleaseConnection(SqliteConnection connection)
    {
        var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        var taskId = System.Threading.Tasks.Task.CurrentId ?? -1;

        _logger.LogDebug(
            "🔄 ReleaseConnection START - Thread: {ThreadId}, Task: {TaskId}, Available: {Available}/{Max}",
            threadId,
            taskId,
            _connectionSemaphore.CurrentCount,
            _options.MaxConnections
        );

        try
        {
            if (connection != null)
            {
                _logger.LogDebug(
                    "🗑️ Disposing connection - Thread: {ThreadId}, Task: {TaskId}, State: {State}",
                    threadId,
                    taskId,
                    connection.State
                );
                connection.Dispose();
                _logger.LogDebug(
                    "✅ Connection disposed - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
            }
            else
            {
                _logger.LogWarning(
                    "⚠️ Attempting to release null connection - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "❌ Error disposing connection - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );
        }
        finally
        {
            _logger.LogDebug(
                "🔓 Releasing semaphore - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );
            _connectionSemaphore.Release();
            _logger.LogDebug(
                "✅ Semaphore released - Thread: {ThreadId}, Task: {TaskId}, Available: {Available}/{Max}",
                threadId,
                taskId,
                _connectionSemaphore.CurrentCount,
                _options.MaxConnections
            );
        }
    }

    /// <summary>
    /// Initializes the database schema and extensions.
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
            return;

        // Use SemaphoreSlim for async-safe initialization
        await _initSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
                return;

            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var taskId = System.Threading.Tasks.Task.CurrentId ?? -1;
            _logger.LogDebug(
                "🔧 InitializeDatabaseAsync START - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );

            _logger.LogInformation("Initializing SQLite database...");

            try
            {
                // Create direct connection for initialization (bypass semaphore to avoid deadlock)
                _logger.LogDebug(
                    "🔌 Creating direct initialization connection - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
                using var connection = new SqliteConnection(_connectionString);
                _logger.LogDebug(
                    "📡 Opening initialization connection - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
                await connection.OpenAsync(cancellationToken);
                _logger.LogDebug(
                    "✅ Initialization connection opened - Thread: {ThreadId}, Task: {TaskId}, State: {State}",
                    threadId,
                    taskId,
                    connection.State
                );

                // Configure the connection
                _logger.LogDebug(
                    "⚙️ Configuring initialization connection - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
                await ConfigureConnectionAsync(connection, cancellationToken);
                _logger.LogDebug(
                    "✅ Initialization connection configured - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );

                // Load extensions first
                _logger.LogDebug(
                    "🔌 Loading extensions - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
                LoadExtensions(connection);
                _logger.LogDebug(
                    "✅ Extensions loaded - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );

                // Create schema
                _logger.LogDebug(
                    "🏗️ Creating database schema - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
                await ExecuteSchemaScriptsAsync(connection, cancellationToken);
                _logger.LogDebug(
                    "✅ Schema created - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );

                // Verify connection state before disposal
                _logger.LogDebug(
                    "🔍 Pre-disposal connection state - Thread: {ThreadId}, Task: {TaskId}, State: {State}",
                    threadId,
                    taskId,
                    connection.State
                );

                _isInitialized = true;
                _logger.LogInformation("SQLite database initialized successfully");
                _logger.LogDebug(
                    "✅ InitializeDatabaseAsync COMPLETE - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to initialize SQLite database - Thread: {ThreadId}, Task: {TaskId}",
                    threadId,
                    taskId
                );
                throw;
            }
        }
        finally
        {
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var taskId = System.Threading.Tasks.Task.CurrentId ?? -1;
            _logger.LogDebug(
                "🔓 Releasing initialization semaphore - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );
            _initSemaphore.Release();
            _logger.LogDebug(
                "✅ Initialization semaphore released - Thread: {ThreadId}, Task: {TaskId}",
                threadId,
                taskId
            );
        }
    }

    /// <summary>
    /// Configures connection-specific settings.
    /// </summary>
    private async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var commands = new List<string>();

        // For testing, use simpler configuration to avoid locking issues
        // Enable WAL mode only if not in memory database
        if (
            _options.EnableWAL
            && !_connectionString.Contains(":memory:")
            && !_connectionString.Contains("Mode=Memory")
        )
        {
            commands.Add("PRAGMA journal_mode = WAL;");
        }
        else
        {
            // Use DELETE mode for in-memory and test databases
            commands.Add("PRAGMA journal_mode = DELETE;");
        }

        // Set busy timeout
        commands.Add($"PRAGMA busy_timeout = {_options.BusyTimeout};");

        // Enable foreign keys
        commands.Add("PRAGMA foreign_keys = ON;");

        // Optimize for performance but avoid locking issues
        commands.Add("PRAGMA synchronous = NORMAL;");
        commands.Add("PRAGMA cache_size = -8000;"); // 8MB cache (smaller for tests)
        commands.Add("PRAGMA temp_store = MEMORY;");

        // Add locking mode for better concurrency
        commands.Add("PRAGMA locking_mode = NORMAL;");

        foreach (var commandText in commands)
        {
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Loads SQLite extensions including sqlite-vec.
    /// </summary>
    private void LoadExtensions(SqliteConnection connection)
    {
        try
        {
            // Enable extension loading
            connection.EnableExtensions(true);

            // Load sqlite-vec extension - this is required for vector functionality
            connection.LoadExtension("vec0");
            _logger.LogInformation("sqlite-vec extension loaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load sqlite-vec extension. Vector functionality requires this extension."
            );
            throw new InvalidOperationException(
                "sqlite-vec extension is required for vector functionality but could not be loaded. Ensure the sqlite-vec NuGet package is properly installed.",
                ex
            );
        }
    }

    /// <summary>
    /// Executes database schema creation scripts.
    /// </summary>
    private async Task ExecuteSchemaScriptsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var schema = GetDatabaseSchema();

        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.CommandTimeout = _options.CommandTimeout;

        await command.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation("Database schema created successfully");
    }

    /// <summary>
    /// Gets the complete database schema SQL.
    /// </summary>
    private string GetDatabaseSchema()
    {
        var schema = new StringBuilder();

        // ID sequence table for generating unique integers
        schema.AppendLine(
            @"
            CREATE TABLE IF NOT EXISTS memory_id_sequence (
                id INTEGER PRIMARY KEY AUTOINCREMENT
            );"
        );

        // Main memories table with integer primary key
        schema.AppendLine(
            @"
            CREATE TABLE IF NOT EXISTS memories (
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
            );"
        );

        // Vector embeddings using sqlite-vec (primary approach)
        schema.AppendLine(
            @"
                            CREATE VIRTUAL TABLE IF NOT EXISTS memory_embeddings USING vec0(
                    memory_id INTEGER PRIMARY KEY,
                    embedding FLOAT[1024]
                );"
        );

        // Vector metadata table for embedding information
        schema.AppendLine(
            @"
            CREATE TABLE IF NOT EXISTS embedding_metadata (
                memory_id INTEGER PRIMARY KEY,
                model_name TEXT NOT NULL,
                embedding_dimension INTEGER NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (memory_id) REFERENCES memories(id) ON DELETE CASCADE
            );"
        );

        // FTS5 virtual table for full-text search
        schema.AppendLine(
            @"
            CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
                content,
                metadata,
                content='memories',
                content_rowid='id'
            );"
        );

        // Graph database tables for entities and relationships

        // Entities table for knowledge graph
        schema.AppendLine(
            @"
            CREATE TABLE IF NOT EXISTS entities (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT,
                aliases TEXT, -- JSON array
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                confidence REAL NOT NULL DEFAULT 1.0,
                source_memory_ids TEXT, -- JSON array of memory IDs
                metadata TEXT, -- JSON
                version INTEGER DEFAULT 1,
                CONSTRAINT chk_entity_name_length CHECK (length(name) > 0 AND length(name) <= 500),
                CONSTRAINT chk_entity_user_id_format CHECK (length(user_id) > 0 AND length(user_id) <= 100),
                CONSTRAINT chk_entity_confidence CHECK (confidence >= 0.0 AND confidence <= 1.0),
                UNIQUE(name, user_id, agent_id, run_id)
            );"
        );

        // Relationships table for knowledge graph
        schema.AppendLine(
            @"
            CREATE TABLE IF NOT EXISTS relationships (
                id INTEGER PRIMARY KEY,
                source TEXT NOT NULL,
                relationship_type TEXT NOT NULL,
                target TEXT NOT NULL,
                user_id TEXT NOT NULL,
                agent_id TEXT,
                run_id TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                confidence REAL NOT NULL DEFAULT 1.0,
                source_memory_id INTEGER,
                temporal_context TEXT,
                metadata TEXT, -- JSON
                version INTEGER DEFAULT 1,
                CONSTRAINT chk_relationship_source_length CHECK (length(source) > 0 AND length(source) <= 500),
                CONSTRAINT chk_relationship_type_length CHECK (length(relationship_type) > 0 AND length(relationship_type) <= 200),
                CONSTRAINT chk_relationship_target_length CHECK (length(target) > 0 AND length(target) <= 500),
                CONSTRAINT chk_relationship_user_id_format CHECK (length(user_id) > 0 AND length(user_id) <= 100),
                CONSTRAINT chk_relationship_confidence CHECK (confidence >= 0.0 AND confidence <= 1.0),
                FOREIGN KEY (source_memory_id) REFERENCES memories(id) ON DELETE SET NULL,
                UNIQUE(source, relationship_type, target, user_id, agent_id, run_id)
            );"
        );

        // FTS5 virtual tables for entity and relationship search
        schema.AppendLine(
            @"
            CREATE VIRTUAL TABLE IF NOT EXISTS entities_fts USING fts5(
                name,
                type,
                aliases,
                metadata,
                content='entities',
                content_rowid='id'
            );"
        );

        schema.AppendLine(
            @"
            CREATE VIRTUAL TABLE IF NOT EXISTS relationships_fts USING fts5(
                source,
                relationship_type,
                target,
                temporal_context,
                metadata,
                content='relationships',
                content_rowid='id'
            );"
        );

        // Vector embeddings for entities and relationships using sqlite-vec
        schema.AppendLine(
            @"
                            CREATE VIRTUAL TABLE IF NOT EXISTS entity_embeddings USING vec0(
                    entity_id INTEGER PRIMARY KEY,
                    embedding FLOAT[1024]
                );"
        );

        schema.AppendLine(
            @"
                            CREATE VIRTUAL TABLE IF NOT EXISTS relationship_embeddings USING vec0(
                    relationship_id INTEGER PRIMARY KEY,
                    embedding FLOAT[1024]
                );"
        );

        // Metadata tables for entity and relationship embeddings
        schema.AppendLine(
            @"
            CREATE TABLE IF NOT EXISTS entity_embedding_metadata (
                entity_id INTEGER PRIMARY KEY,
                model_name TEXT NOT NULL,
                embedding_dimension INTEGER NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (entity_id) REFERENCES entities(id) ON DELETE CASCADE
            );"
        );

        schema.AppendLine(
            @"
            CREATE TABLE IF NOT EXISTS relationship_embedding_metadata (
                relationship_id INTEGER PRIMARY KEY,
                model_name TEXT NOT NULL,
                embedding_dimension INTEGER NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (relationship_id) REFERENCES relationships(id) ON DELETE CASCADE
            );"
        );

        // Indexes for performance
        schema.AppendLine(
            @"
            CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(user_id, agent_id, run_id);
            CREATE INDEX IF NOT EXISTS idx_memories_created ON memories(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_memories_updated ON memories(updated_at DESC);
            
            -- Embedding metadata indexes
            CREATE INDEX IF NOT EXISTS idx_embedding_metadata_model ON embedding_metadata(model_name);
            CREATE INDEX IF NOT EXISTS idx_embedding_metadata_dimension ON embedding_metadata(embedding_dimension);
            CREATE INDEX IF NOT EXISTS idx_embedding_metadata_created ON embedding_metadata(created_at DESC);
            
            -- Entity embedding metadata indexes
            CREATE INDEX IF NOT EXISTS idx_entity_embedding_metadata_model ON entity_embedding_metadata(model_name);
            CREATE INDEX IF NOT EXISTS idx_entity_embedding_metadata_dimension ON entity_embedding_metadata(embedding_dimension);
            CREATE INDEX IF NOT EXISTS idx_entity_embedding_metadata_created ON entity_embedding_metadata(created_at DESC);
            
            -- Relationship embedding metadata indexes
            CREATE INDEX IF NOT EXISTS idx_relationship_embedding_metadata_model ON relationship_embedding_metadata(model_name);
            CREATE INDEX IF NOT EXISTS idx_relationship_embedding_metadata_dimension ON relationship_embedding_metadata(embedding_dimension);
            CREATE INDEX IF NOT EXISTS idx_relationship_embedding_metadata_created ON relationship_embedding_metadata(created_at DESC);
            
            -- Graph database indexes
            CREATE INDEX IF NOT EXISTS idx_entities_session ON entities(user_id, agent_id, run_id);
            CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);
            CREATE INDEX IF NOT EXISTS idx_entities_type ON entities(type);
            CREATE INDEX IF NOT EXISTS idx_entities_created ON entities(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_entities_updated ON entities(updated_at DESC);
            
            CREATE INDEX IF NOT EXISTS idx_relationships_session ON relationships(user_id, agent_id, run_id);
            CREATE INDEX IF NOT EXISTS idx_relationships_source ON relationships(source);
            CREATE INDEX IF NOT EXISTS idx_relationships_target ON relationships(target);
            CREATE INDEX IF NOT EXISTS idx_relationships_type ON relationships(relationship_type);
            CREATE INDEX IF NOT EXISTS idx_relationships_source_target ON relationships(source, target);
            CREATE INDEX IF NOT EXISTS idx_relationships_created ON relationships(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_relationships_updated ON relationships(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_relationships_memory ON relationships(source_memory_id);"
        );

        // FTS5 triggers for automatic content indexing
        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS memories_fts_insert AFTER INSERT ON memories BEGIN
                INSERT INTO memory_fts(rowid, content, metadata) VALUES (new.id, new.content, new.metadata);
            END"
        );

        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS memories_fts_update AFTER UPDATE ON memories BEGIN
                UPDATE memory_fts SET content = new.content, metadata = new.metadata WHERE rowid = new.id;
            END"
        );

        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS memories_fts_delete AFTER DELETE ON memories BEGIN
                DELETE FROM memory_fts WHERE rowid = old.id;
            END"
        );

        // FTS5 triggers for entities
        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS entities_fts_insert AFTER INSERT ON entities BEGIN
                INSERT INTO entities_fts(rowid, name, type, aliases, metadata) VALUES (new.id, new.name, new.type, new.aliases, new.metadata);
            END"
        );

        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS entities_fts_update AFTER UPDATE ON entities BEGIN
                UPDATE entities_fts SET name = new.name, type = new.type, aliases = new.aliases, metadata = new.metadata WHERE rowid = new.id;
            END"
        );

        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS entities_fts_delete AFTER DELETE ON entities BEGIN
                DELETE FROM entities_fts WHERE rowid = old.id;
            END"
        );

        // FTS5 triggers for relationships
        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS relationships_fts_insert AFTER INSERT ON relationships BEGIN
                INSERT INTO relationships_fts(rowid, source, relationship_type, target, temporal_context, metadata) VALUES (new.id, new.source, new.relationship_type, new.target, new.temporal_context, new.metadata);
            END"
        );

        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS relationships_fts_update AFTER UPDATE ON relationships BEGIN
                UPDATE relationships_fts SET source = new.source, relationship_type = new.relationship_type, target = new.target, temporal_context = new.temporal_context, metadata = new.metadata WHERE rowid = new.id;
            END"
        );

        schema.AppendLine(
            @"
            CREATE TRIGGER IF NOT EXISTS relationships_fts_delete AFTER DELETE ON relationships BEGIN
                DELETE FROM relationships_fts WHERE rowid = old.id;
            END"
        );

        return schema.ToString();
    }

    /// <summary>
    /// Executes a health check on the database.
    /// </summary>
    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = await GetConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return false;
        }
    }

    public void Dispose()
    {
        _connectionSemaphore?.Dispose();
        _initSemaphore?.Dispose();
    }
}

/// <summary>
/// Wrapper for SqliteConnection that automatically releases semaphore on disposal.
/// </summary>
public class ManagedSqliteConnection : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteManager _manager;
    private bool _disposed = false;
    private readonly int _connectionId;
    private static int _nextConnectionId = 1;

    public ManagedSqliteConnection(SqliteConnection connection, SqliteManager manager)
    {
        _connection = connection;
        _manager = manager;
        _connectionId = Interlocked.Increment(ref _nextConnectionId);

        var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        System.Diagnostics.Debug.WriteLine(
            $"🔌 ManagedConnection-{_connectionId} CREATED - Thread: {threadId}, State: {connection.State}"
        );
    }

    // Delegate all SqliteConnection members
    public string ConnectionString => _connection.ConnectionString;
    public System.Data.ConnectionState State => _connection.State;

    public SqliteCommand CreateCommand() => _connection.CreateCommand();

    public SqliteTransaction BeginTransaction()
    {
        var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        System.Diagnostics.Debug.WriteLine(
            $"🔄 ManagedConnection-{_connectionId} BeginTransaction START - Thread: {threadId}, State: {State}"
        );

        try
        {
            var transaction = _connection.BeginTransaction();
            System.Diagnostics.Debug.WriteLine(
                $"✅ ManagedConnection-{_connectionId} BeginTransaction SUCCESS - Thread: {threadId}"
            );
            return transaction;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"❌ ManagedConnection-{_connectionId} BeginTransaction FAILED - Thread: {threadId}, Error: {ex.Message}"
            );
            throw;
        }
    }

    public SqliteTransaction BeginTransaction(System.Data.IsolationLevel isolationLevel) =>
        _connection.BeginTransaction(isolationLevel);

    public void Dispose()
    {
        if (!_disposed)
        {
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            System.Diagnostics.Debug.WriteLine(
                $"🗑️ ManagedConnection-{_connectionId} DISPOSE START - Thread: {threadId}, State: {State}"
            );

            _connection?.Dispose();
            if (_connection != null)
            {
                _manager.ReleaseConnection(_connection);
            }
            _disposed = true;

            System.Diagnostics.Debug.WriteLine(
                $"✅ ManagedConnection-{_connectionId} DISPOSE COMPLETE - Thread: {threadId}"
            );
        }
    }

    // Implicit conversion to SqliteConnection for compatibility
    public static implicit operator SqliteConnection(ManagedSqliteConnection managed) =>
        managed._connection;
}
