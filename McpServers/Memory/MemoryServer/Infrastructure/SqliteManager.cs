using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using MemoryServer.Models;

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
    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _connectionSemaphore.WaitAsync(cancellationToken);
        
        try
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            
            // Configure connection settings
            await ConfigureConnectionAsync(connection, cancellationToken);
            
            return connection;
        }
        catch
        {
            _connectionSemaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Releases a database connection back to the pool.
    /// </summary>
    public void ReleaseConnection(SqliteConnection connection)
    {
        try
        {
            connection?.Dispose();
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Initializes the database schema and extensions.
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        // Use SemaphoreSlim for async-safe initialization
        await _initSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized) return;

            _logger.LogInformation("Initializing SQLite database...");
            
            try
            {
                using var connection = await GetConnectionAsync(cancellationToken);
                
                // Load extensions first
                LoadExtensions(connection);
                
                // Create schema
                await ExecuteSchemaScriptsAsync(connection, cancellationToken);
                
                _isInitialized = true;
                _logger.LogInformation("SQLite database initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SQLite database");
                throw;
            }
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    /// <summary>
    /// Configures connection-specific settings.
    /// </summary>
    private async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var commands = new List<string>();
        
        // Enable WAL mode for better concurrency
        if (_options.EnableWAL)
        {
            commands.Add("PRAGMA journal_mode = WAL;");
        }
        
        // Set busy timeout
        commands.Add($"PRAGMA busy_timeout = {_options.BusyTimeout};");
        
        // Enable foreign keys
        commands.Add("PRAGMA foreign_keys = ON;");
        
        // Optimize for performance
        commands.Add("PRAGMA synchronous = NORMAL;");
        commands.Add("PRAGMA cache_size = -64000;"); // 64MB cache
        commands.Add("PRAGMA temp_store = MEMORY;");
        
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
            
            // Try to load sqlite-vec extension
            // Note: In production, you would need to have the sqlite-vec extension available
            // For now, we'll implement a fallback vector storage approach
            try
            {
                connection.LoadExtension("vec0");
                _logger.LogInformation("SQLite-vec extension loaded successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SQLite-vec extension not available, using fallback vector storage");
                // We'll implement vector storage using BLOB columns instead
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure SQLite extensions");
            throw;
        }
    }

    /// <summary>
    /// Executes database schema creation scripts.
    /// </summary>
    private async Task ExecuteSchemaScriptsAsync(SqliteConnection connection, CancellationToken cancellationToken)
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
        schema.AppendLine(@"
            CREATE TABLE IF NOT EXISTS memory_id_sequence (
                id INTEGER PRIMARY KEY AUTOINCREMENT
            );");
        
        // Main memories table with integer primary key
        schema.AppendLine(@"
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
            );");
        
        // Vector embeddings table (fallback if sqlite-vec not available)
        schema.AppendLine(@"
            CREATE TABLE IF NOT EXISTS memory_embeddings (
                memory_id INTEGER PRIMARY KEY,
                embedding BLOB NOT NULL,
                dimension INTEGER NOT NULL,
                FOREIGN KEY (memory_id) REFERENCES memories(id) ON DELETE CASCADE
            );");
        
        // FTS5 virtual table for full-text search
        schema.AppendLine(@"
            CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
                memory_id UNINDEXED,
                content,
                metadata,
                content='memories',
                content_rowid='id'
            );");
        
        // Session defaults storage
        schema.AppendLine(@"
            CREATE TABLE IF NOT EXISTS session_defaults (
                connection_id TEXT PRIMARY KEY,
                user_id TEXT,
                agent_id TEXT,
                run_id TEXT,
                metadata TEXT, -- JSON
                source INTEGER NOT NULL DEFAULT 0, -- SessionDefaultsSource enum
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );");
        
        // Graph database tables for entities and relationships
        
        // Entities table for knowledge graph
        schema.AppendLine(@"
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
            );");
        
        // Relationships table for knowledge graph
        schema.AppendLine(@"
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
            );");
        
        // Indexes for performance
        schema.AppendLine(@"
            CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(user_id, agent_id, run_id);
            CREATE INDEX IF NOT EXISTS idx_memories_created ON memories(created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_memories_updated ON memories(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_session_defaults_created ON session_defaults(created_at DESC);
            
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
            CREATE INDEX IF NOT EXISTS idx_relationships_memory ON relationships(source_memory_id);");
        
        // Triggers for FTS5 synchronization
        schema.AppendLine(@"
            CREATE TRIGGER IF NOT EXISTS memories_fts_insert AFTER INSERT ON memories BEGIN
                INSERT INTO memory_fts(memory_id, content, metadata) VALUES (new.id, new.content, new.metadata);
            END;
            
            CREATE TRIGGER IF NOT EXISTS memories_fts_update AFTER UPDATE ON memories BEGIN
                UPDATE memory_fts SET content = new.content, metadata = new.metadata WHERE memory_id = new.id;
            END;
            
            CREATE TRIGGER IF NOT EXISTS memories_fts_delete AFTER DELETE ON memories BEGIN
                DELETE FROM memory_fts WHERE memory_id = old.id;
            END;");
        
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