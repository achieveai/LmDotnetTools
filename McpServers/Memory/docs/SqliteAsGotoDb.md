# SQLite as the Go-To Database - Enhanced with Database Session Pattern

## Overview

SQLite serves as the primary database for the Memory MCP Server, providing a lightweight, serverless, and highly capable storage solution. This document outlines the comprehensive approach to using SQLite with advanced features including vector search (sqlite-vec), full-text search (FTS5), and graph traversals, enhanced with a robust Database Session Pattern for reliable connection management.

**ARCHITECTURE ENHANCEMENT**: This design has been significantly enhanced with a Database Session Pattern to address SQLite connection management challenges, eliminate file locking issues, ensure proper resource cleanup, and provide robust test isolation.

## Database Session Pattern Architecture

### Problem Statement

Traditional SQLite connection management approaches can lead to several critical issues:

1. **File Locking Issues**: Multiple connections to the same SQLite file can cause locking conflicts, especially in test environments
2. **Resource Leaks**: Improper connection disposal can leave file handles open, preventing cleanup
3. **WAL Mode Complications**: Write-Ahead Logging mode creates additional files (.wal, .shm) that can remain locked
4. **Test Isolation Problems**: Tests can interfere with each other due to shared database files and connection pooling
5. **Connection Pool Conflicts**: Multiple connections competing for the same resources

### Solution: Database Session Pattern

The Database Session Pattern encapsulates the entire SQLite connection lifecycle, ensuring proper resource management, cleanup, and isolation.

#### Core Interfaces

```csharp
/// <summary>
/// Represents a database session that encapsulates SQLite connection lifecycle management
/// </summary>
public interface ISqliteSession : IAsyncDisposable
{
    /// <summary>
    /// Executes an operation with the session's connection
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation);
    
    /// <summary>
    /// Executes an operation within a transaction
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation);
    
    /// <summary>
    /// Executes an operation with the session's connection (void return)
    /// </summary>
    Task ExecuteAsync(Func<SqliteConnection, Task> operation);
    
    /// <summary>
    /// Executes an operation within a transaction (void return)
    /// </summary>
    Task ExecuteInTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> operation);
}

/// <summary>
/// Factory for creating database sessions
/// </summary>
public interface ISqliteSessionFactory
{
    /// <summary>
    /// Creates a new database session
    /// </summary>
    Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new database session with a specific connection string
    /// </summary>
    Task<ISqliteSession> CreateSessionAsync(string connectionString, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Initializes the database schema
    /// </summary>
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
}
```

#### Production Implementation

```csharp
/// <summary>
/// Production implementation of SQLite session with proper resource management
/// </summary>
public class SqliteSession : ISqliteSession
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSession> _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteSession(string connectionString, ILogger<SqliteSession> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        
        await EnsureConnectionAsync();
        
        try
        {
            return await operation(_connection!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing database operation");
            throw;
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        
        await EnsureConnectionAsync();
        
        using var transaction = _connection!.BeginTransaction();
        try
        {
            var result = await operation(_connection, transaction);
            transaction.Commit();
            _logger.LogDebug("Transaction committed successfully");
            return result;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Transaction rolled back due to error");
            throw;
        }
    }

    public async Task ExecuteAsync(Func<SqliteConnection, Task> operation)
    {
        await ExecuteAsync(async conn =>
        {
            await operation(conn);
            return true;
        });
    }

    public async Task ExecuteInTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> operation)
    {
        await ExecuteInTransactionAsync(async (conn, trans) =>
        {
            await operation(conn, trans);
            return true;
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null && !_disposed)
        {
            try
            {
                // Force WAL checkpoint before closing to ensure all data is written
                await ExecuteAsync(async conn =>
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                    await cmd.ExecuteNonQueryAsync();
                    _logger.LogDebug("WAL checkpoint completed");
                    return true;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to execute WAL checkpoint during disposal");
            }
            
            await _connection.DisposeAsync();
            _connection = null;
            _logger.LogDebug("SQLite connection disposed");
        }
        _disposed = true;
    }

    private async Task EnsureConnectionAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SqliteSession));
            
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync();
            
            // Configure connection for optimal performance and reliability
            await ConfigureConnectionAsync(_connection);
            
            _logger.LogDebug("SQLite connection established");
        }
    }

    private async Task ConfigureConnectionAsync(SqliteConnection connection)
    {
        // Enable extensions for sqlite-vec
        connection.EnableExtensions(true);
        
        try
        {
            connection.LoadExtension("vec0");
            _logger.LogDebug("sqlite-vec extension loaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load sqlite-vec extension");
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
            using var cmd = connection.CreateCommand();
            cmd.CommandText = pragma;
            await cmd.ExecuteNonQueryAsync();
        }
        
        _logger.LogDebug("SQLite connection configured with performance pragmas");
    }
}
```

#### Production Session Factory

```csharp
/// <summary>
/// Production implementation of SQLite session factory
/// </summary>
public class SqliteSessionFactory : ISqliteSessionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionFactory> _logger;
    private readonly SemaphoreSlim _initializationSemaphore;
    private bool _isInitialized;

    public SqliteSessionFactory(string connectionString, ILogger<SqliteSessionFactory> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _initializationSemaphore = new SemaphoreSlim(1, 1);
    }

    public async Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return new SqliteSession(_connectionString, _logger);
    }

    public async Task<ISqliteSession> CreateSessionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));
            
        return new SqliteSession(connectionString, _logger);
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized) return;

            using var session = new SqliteSession(_connectionString, _logger);
            
            await session.ExecuteAsync(async connection =>
            {
                await ExecuteSchemaScriptsAsync(connection, cancellationToken);
                return true;
            });

            _isInitialized = true;
            _logger.LogInformation("Database initialized successfully");
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_isInitialized)
        {
            await InitializeDatabaseAsync(cancellationToken);
        }
    }

    private async Task ExecuteSchemaScriptsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var schemaScripts = GetSchemaScripts();
        
        foreach (var script in schemaScripts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        
        _logger.LogDebug("Schema scripts executed successfully");
    }

    private static string[] GetSchemaScripts()
    {
        return new[]
        {
            // ID sequence table for generating unique integers
            @"CREATE TABLE IF NOT EXISTS memory_id_sequence (
                id INTEGER PRIMARY KEY AUTOINCREMENT
            )",

            // Main memories table with integer primary key
            @"CREATE TABLE IF NOT EXISTS memories (
                id INTEGER PRIMARY KEY,
                content TEXT NOT NULL,
                user_id TEXT,
                agent_id TEXT,
                run_id TEXT,
                metadata TEXT, -- JSON
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                version INTEGER DEFAULT 1
            )",

            // Vector embeddings using sqlite-vec
            @"CREATE VIRTUAL TABLE IF NOT EXISTS memory_embeddings USING vec0(
                memory_id INTEGER PRIMARY KEY,
                embedding BLOB
            )",

            // FTS5 virtual table for full-text search
            @"CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
                memory_id UNINDEXED,
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
                metadata TEXT -- JSON
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
                metadata TEXT -- JSON
            )",

            // Session defaults storage
            @"CREATE TABLE IF NOT EXISTS session_defaults (
                connection_id TEXT PRIMARY KEY,
                user_id TEXT,
                agent_id TEXT,
                run_id TEXT,
                metadata TEXT, -- JSON
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            )",

            // Indexes for performance
            @"CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_memories_created ON memories(created_at DESC)",
            @"CREATE INDEX IF NOT EXISTS idx_entities_session ON entities(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name)",
            @"CREATE INDEX IF NOT EXISTS idx_relationships_session ON relationships(user_id, agent_id, run_id)",
            @"CREATE INDEX IF NOT EXISTS idx_relationships_entities ON relationships(source_entity_name, target_entity_name)",
            @"CREATE INDEX IF NOT EXISTS idx_session_defaults_created ON session_defaults(created_at DESC)"
        };
    }
}
```

#### Test Implementation for Isolation

```csharp
/// <summary>
/// Test-specific implementation of SQLite session factory for complete isolation
/// </summary>
public class TestSqliteSessionFactory : ISqliteSessionFactory
{
    private readonly ILogger<TestSqliteSessionFactory> _logger;
    private readonly string _testDatabaseDirectory;

    public TestSqliteSessionFactory(ILogger<TestSqliteSessionFactory> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _testDatabaseDirectory = Path.Combine(Path.GetTempPath(), "MemoryMcpTests");
        
        // Ensure test directory exists
        Directory.CreateDirectory(_testDatabaseDirectory);
    }

    public async Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        // Create unique database file for each test session
        var uniqueDbPath = Path.Combine(_testDatabaseDirectory, $"test_memory_{Guid.NewGuid()}.db");
        var connectionString = $"Data Source={uniqueDbPath};Mode=ReadWriteCreate;";
        
        var session = new TestSqliteSession(connectionString, _logger, uniqueDbPath);
        await session.InitializeAsync(cancellationToken);
        return session;
    }

    public async Task<ISqliteSession> CreateSessionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // For tests, always create isolated sessions
        return await CreateSessionAsync(cancellationToken);
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        // Test factory doesn't need global initialization
        await Task.CompletedTask;
    }
}

/// <summary>
/// Test-specific SQLite session with automatic cleanup
/// </summary>
public class TestSqliteSession : SqliteSession
{
    private readonly string _databasePath;
    private readonly ILogger _logger;

    public TestSqliteSession(string connectionString, ILogger logger, string databasePath)
        : base(connectionString, logger)
    {
        _databasePath = databasePath;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async connection =>
        {
            var factory = new SqliteSessionFactory(connection.ConnectionString, _logger);
            await factory.InitializeDatabaseAsync(cancellationToken);
            return true;
        });
    }

    public override async ValueTask DisposeAsync()
    {
        // First dispose the base connection properly
        await base.DisposeAsync();
        
        // Then clean up test database files
        await CleanupTestFilesAsync();
    }

    private async Task CleanupTestFilesAsync()
    {
        try
        {
            // Wait a bit to ensure all handles are released
            await Task.Delay(100);
            
            // Clean up main database file
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
            _logger.LogWarning(ex, "Failed to clean up test database files for {DatabasePath}", _databasePath);
        }
    }
}
```

## Repository Pattern Integration

### Updated Repository Implementation

```csharp
/// <summary>
/// Example repository using the Database Session Pattern
/// </summary>
public class GraphRepository : IGraphRepository
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<GraphRepository> _logger;

    public GraphRepository(ISqliteSessionFactory sessionFactory, ILogger<GraphRepository> logger)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> AddEntityAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            // Generate new ID
            var id = await GenerateEntityIdAsync(connection, transaction, cancellationToken);
            
            // Insert entity
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO entities (id, name, type, aliases, user_id, agent_id, run_id, confidence, source_memory_ids, metadata)
                VALUES (@id, @name, @type, @aliases, @userId, @agentId, @runId, @confidence, @sourceMemoryIds, @metadata)";
            
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@name", entity.Name);
            command.Parameters.AddWithValue("@type", entity.Type ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@aliases", JsonSerializer.Serialize(entity.Aliases ?? new List<string>()));
            command.Parameters.AddWithValue("@userId", entity.UserId);
            command.Parameters.AddWithValue("@agentId", entity.AgentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@runId", entity.RunId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@confidence", entity.Confidence);
            command.Parameters.AddWithValue("@sourceMemoryIds", JsonSerializer.Serialize(entity.SourceMemoryIds ?? new List<string>()));
            command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entity.Metadata ?? new Dictionary<string, object>()));
            
            await command.ExecuteNonQueryAsync(cancellationToken);
            
            _logger.LogDebug("Added entity {EntityName} with ID {EntityId}", entity.Name, id);
            return id;
        });
    }

    public async Task<Entity?> GetEntityByIdAsync(int id, string userId, string? agentId = null, string? runId = null, CancellationToken cancellationToken = default)
    {
        using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, name, type, aliases, user_id, agent_id, run_id, created_at, updated_at, confidence, source_memory_ids, metadata
                FROM entities 
                WHERE id = @id AND user_id = @userId 
                AND (@agentId IS NULL OR agent_id = @agentId)
                AND (@runId IS NULL OR run_id = @runId)";
            
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@agentId", agentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@runId", runId ?? (object)DBNull.Value);
            
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            
            if (await reader.ReadAsync(cancellationToken))
            {
                return MapEntityFromReader(reader);
            }
            
            return null;
        });
    }

    private async Task<int> GenerateEntityIdAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO memory_id_sequence DEFAULT VALUES;
            SELECT last_insert_rowid();";
        
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static Entity MapEntityFromReader(SqliteDataReader reader)
    {
        return new Entity
        {
            Id = reader.GetInt32("id"),
            Name = reader.GetString("name"),
            Type = reader.IsDBNull("type") ? null : reader.GetString("type"),
            Aliases = JsonSerializer.Deserialize<List<string>>(reader.GetString("aliases")) ?? new List<string>(),
            UserId = reader.GetString("user_id"),
            AgentId = reader.IsDBNull("agent_id") ? null : reader.GetString("agent_id"),
            RunId = reader.IsDBNull("run_id") ? null : reader.GetString("run_id"),
            CreatedAt = reader.GetDateTime("created_at"),
            UpdatedAt = reader.GetDateTime("updated_at"),
            Confidence = reader.GetDouble("confidence"),
            SourceMemoryIds = JsonSerializer.Deserialize<List<string>>(reader.GetString("source_memory_ids")) ?? new List<string>(),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString("metadata")) ?? new Dictionary<string, object>()
        };
    }
}
```

## Dependency Injection Configuration

### Service Registration

```csharp
// Program.cs or Startup.cs
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMemoryDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Data Source=memory.db;Mode=ReadWriteCreate;";

        // Register session factory based on environment
        if (IsTestEnvironment())
        {
            services.AddSingleton<ISqliteSessionFactory, TestSqliteSessionFactory>();
        }
        else
        {
            services.AddSingleton<ISqliteSessionFactory>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger<SqliteSessionFactory>>();
                return new SqliteSessionFactory(connectionString, logger);
            });
        }

        // Register repositories
        services.AddScoped<IGraphRepository, GraphRepository>();
        services.AddScoped<IMemoryRepository, MemoryRepository>();

        return services;
    }

    private static bool IsTestEnvironment()
    {
        return Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Test" ||
               Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Test";
    }
}
```

## Performance Optimization

### Connection Management Best Practices

1. **Session Scoping**: Create sessions at the service boundary and dispose them properly
2. **Transaction Management**: Use transactions for multi-operation consistency
3. **WAL Mode**: Leverage WAL mode for better concurrency while ensuring proper cleanup
4. **Connection Pooling**: In production, consider connection pooling at the session factory level
5. **Resource Monitoring**: Track connection lifecycle and detect leaks

### SQLite Configuration

```csharp
private static readonly string[] OptimalPragmas = new[]
{
    "PRAGMA journal_mode=WAL",           // Enable WAL mode for better concurrency
    "PRAGMA synchronous=NORMAL",        // Balance between safety and performance
    "PRAGMA cache_size=10000",          // Increase cache size (10MB)
    "PRAGMA foreign_keys=ON",           // Enable foreign key constraints
    "PRAGMA temp_store=MEMORY",         // Store temporary tables in memory
    "PRAGMA mmap_size=268435456",       // Enable memory-mapped I/O (256MB)
    "PRAGMA optimize"                   // Optimize query planner statistics
};
```

## Testing Strategy

### Test Configuration

```csharp
[TestClass]
public class GraphRepositoryTests
{
    private ISqliteSessionFactory _sessionFactory;
    private IGraphRepository _repository;

    [TestInitialize]
    public async Task Setup()
    {
        var logger = new Mock<ILogger<TestSqliteSessionFactory>>().Object;
        _sessionFactory = new TestSqliteSessionFactory(logger);
        
        var repoLogger = new Mock<ILogger<GraphRepository>>().Object;
        _repository = new GraphRepository(_sessionFactory, repoLogger);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        // Session factory handles cleanup automatically
        if (_sessionFactory is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    [TestMethod]
    public async Task AddEntity_ShouldCreateEntityWithUniqueId()
    {
        // Arrange
        var entity = new Entity
        {
            Name = "Test Entity",
            Type = "Person",
            UserId = "user123"
        };

        // Act
        var id = await _repository.AddEntityAsync(entity);

        // Assert
        Assert.IsTrue(id > 0);
        
        var retrieved = await _repository.GetEntityByIdAsync(id, "user123");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("Test Entity", retrieved.Name);
    }
}
```

## Monitoring and Observability

### Connection Lifecycle Monitoring

```csharp
public class SqliteSessionMonitor
{
    private readonly ILogger<SqliteSessionMonitor> _logger;
    private readonly ConcurrentDictionary<string, SessionMetrics> _activeConnections;

    public void TrackSessionCreated(string sessionId)
    {
        _activeConnections[sessionId] = new SessionMetrics
        {
            CreatedAt = DateTime.UtcNow,
            SessionId = sessionId
        };
        
        _logger.LogDebug("Session created: {SessionId}", sessionId);
    }

    public void TrackSessionDisposed(string sessionId)
    {
        if (_activeConnections.TryRemove(sessionId, out var metrics))
        {
            var duration = DateTime.UtcNow - metrics.CreatedAt;
            _logger.LogDebug("Session disposed: {SessionId}, Duration: {Duration}ms", 
                sessionId, duration.TotalMilliseconds);
        }
    }

    public int GetActiveConnectionCount() => _activeConnections.Count;
}
```

## Migration Strategy

### Gradual Migration Approach

1. **Phase 1**: Implement session interfaces alongside existing SqliteManager
2. **Phase 2**: Update repositories one by one to use session pattern
3. **Phase 3**: Update service layer to inject session factory
4. **Phase 4**: Remove old SqliteManager and validate all tests pass
5. **Phase 5**: Performance optimization and monitoring

### Backward Compatibility

```csharp
// Adapter pattern for gradual migration
public class SqliteManagerAdapter : ISqliteSessionFactory
{
    private readonly SqliteManager _sqliteManager;

    public async Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _sqliteManager.GetConnectionAsync(cancellationToken);
        return new SqliteConnectionSession(connection);
    }
}
```

## Conclusion

The Database Session Pattern provides a robust, reliable, and test-friendly approach to SQLite connection management. By encapsulating the connection lifecycle, ensuring proper resource cleanup, and providing complete test isolation, this pattern eliminates the common issues associated with SQLite usage in production applications.

Key benefits include:
- **Eliminated file locking issues** through proper resource management
- **Complete test isolation** with unique database instances per test
- **Guaranteed resource cleanup** with automatic WAL checkpointing
- **Improved error handling** with transaction scoping
- **Better performance monitoring** with connection lifecycle tracking

This architecture ensures that the Memory MCP Server can reliably handle SQLite operations in both development and production environments while maintaining high performance and data integrity.
