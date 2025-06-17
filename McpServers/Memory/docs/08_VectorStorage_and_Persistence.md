# Vector Storage & Persistence

This consolidated document merges the following original file:

- VectorStorage.md

---

## Vector Storage Implementation

<details>
<summary>Full Vector Storage Implementation</summary>

```markdown
<!-- Begin VectorStorage.md content -->
# Vector Storage Design - Enhanced with Database Session Pattern

## Overview

The Vector Storage system provides semantic similarity search capabilities for the memory system using SQLite with the sqlite-vec extension as the primary vector database. Enhanced with the Database Session Pattern, it ensures reliable connection management, proper resource cleanup, and robust test isolation while handling embedding storage, similarity search, metadata filtering, and efficient vector operations.

**ARCHITECTURE ENHANCEMENT**: This design has been updated to use SQLite with sqlite-vec extension and the Database Session Pattern, providing reliable resource management, test isolation, and simplified deployment without external dependencies.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                Vector Storage Layer (Enhanced)              │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ VectorStore │  │   SQLite    │  │    Embedding        │  │
│  │    Base     │  │ with        │  │    Manager          │  │
│  │             │  │ sqlite-vec  │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Database Session Layer                       │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ISqliteSession│  │ISqliteSession│  │   Session          │  │
│  │ Interface   │  │  Factory    │  │  Implementations    │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Core Operations                          │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Insert    │  │   Search    │  │      Update         │  │
│  │ Operations  │  │ Operations  │  │    Operations       │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Advanced Features                        │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Metadata   │  │   Batch     │  │     Performance     │  │
│  │  Filtering  │  │ Operations  │  │   Optimization      │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Core Components

### 1. VectorStoreBase (Abstract Interface)

**Purpose**: Defines the contract for all vector store implementations, ensuring consistent behavior across different vector database providers with session-scoped operations.

**Key Operations**:
- `create_collection()`: Initialize vector collections with specified dimensions and distance metrics
- `insert()`: Store vectors with associated metadata and unique integer identifiers
- `search()`: Perform similarity search with filtering and scoring capabilities
- `get()`: Retrieve specific vectors by integer identifier
- `update()`: Modify existing vectors and their metadata
- `delete()`: Remove vectors from the collection
- `list_vectors()`: Enumerate vectors with optional filtering
- `collection_info()`: Retrieve collection statistics and configuration

**Session-Scoped Interface**:
```csharp
public interface IVectorStore
{
    Task<int> InsertAsync(ISqliteSession session, VectorRecord record, CancellationToken cancellationToken = default);
    Task<List<VectorSearchResult>> SearchAsync(ISqliteSession session, VectorSearchRequest request, CancellationToken cancellationToken = default);
    Task<VectorRecord?> GetAsync(ISqliteSession session, int id, CancellationToken cancellationToken = default);
    Task UpdateAsync(ISqliteSession session, int id, VectorRecord record, CancellationToken cancellationToken = default);
    Task DeleteAsync(ISqliteSession session, int id, CancellationToken cancellationToken = default);
    Task<List<VectorRecord>> ListAsync(ISqliteSession session, VectorListRequest request, CancellationToken cancellationToken = default);
}
```

**Design Principles**:
- Session-scoped operations for reliable resource management
- Provider-agnostic interface for easy switching between vector databases
- Consistent error handling and response formats
- Support for both synchronous and asynchronous operations
- Comprehensive metadata support for rich filtering capabilities
- Integer ID usage for optimal LLM integration

### 2. SQLite with sqlite-vec Provider Implementation

**Connection Management with Session Pattern**:
- Session-scoped connection lifecycle management
- Automatic WAL checkpoint handling during session disposal
- Test isolation with unique database instances
- Production connection pooling and optimization
- Health monitoring and connection leak detection

**Collection Management**:
- Automatic virtual table creation with optimal configuration
- Support for multiple distance metrics and vector dimensions
- Collection optimization for search performance
- Schema migration and upgrade capabilities

**Vector Operations with Session Pattern**:
```csharp
public class SqliteVectorStore : IVectorStore
{
    private readonly ILogger<SqliteVectorStore> _logger;

    public async Task<int> InsertAsync(ISqliteSession session, VectorRecord record, CancellationToken cancellationToken = default)
    {
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            // Generate unique integer ID
            var id = await GenerateVectorIdAsync(connection, transaction, cancellationToken);
            
            // Insert vector embedding
            using var vectorCmd = connection.CreateCommand();
            vectorCmd.Transaction = transaction;
            vectorCmd.CommandText = @"
                INSERT INTO memory_embeddings (memory_id, embedding)
                VALUES (@id, @embedding)";
            
            vectorCmd.Parameters.AddWithValue("@id", id);
            vectorCmd.Parameters.AddWithValue("@embedding", record.Embedding);
            
            await vectorCmd.ExecuteNonQueryAsync(cancellationToken);
            
            // Insert metadata
            using var metadataCmd = connection.CreateCommand();
            metadataCmd.Transaction = transaction;
            metadataCmd.CommandText = @"
                INSERT INTO memories (id, content, user_id, agent_id, run_id, metadata, created_at, updated_at)
                VALUES (@id, @content, @userId, @agentId, @runId, @metadata, @createdAt, @updatedAt)";
            
            metadataCmd.Parameters.AddWithValue("@id", id);
            metadataCmd.Parameters.AddWithValue("@content", record.Content);
            metadataCmd.Parameters.AddWithValue("@userId", record.UserId ?? (object)DBNull.Value);
            metadataCmd.Parameters.AddWithValue("@agentId", record.AgentId ?? (object)DBNull.Value);
            metadataCmd.Parameters.AddWithValue("@runId", record.RunId ?? (object)DBNull.Value);
            metadataCmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(record.Metadata ?? new Dictionary<string, object>()));
            metadataCmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow);
            metadataCmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
            
            await metadataCmd.ExecuteNonQueryAsync(cancellationToken);
            
            _logger.LogDebug("Inserted vector with ID {VectorId}", id);
            return id;
        });
    }

    public async Task<List<VectorSearchResult>> SearchAsync(ISqliteSession session, VectorSearchRequest request, CancellationToken cancellationToken = default)
    {
        return await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            
            // Build similarity search query with session filtering
            command.CommandText = @"
                SELECT m.id, m.content, m.user_id, m.agent_id, m.run_id, m.metadata, m.created_at, m.updated_at,
                       vec_distance_cosine(e.embedding, @queryVector) as distance
                FROM memories m
                JOIN memory_embeddings e ON m.id = e.memory_id
                WHERE (@userId IS NULL OR m.user_id = @userId)
                  AND (@agentId IS NULL OR m.agent_id = @agentId)
                  AND (@runId IS NULL OR m.run_id = @runId)
                  AND vec_distance_cosine(e.embedding, @queryVector) < @threshold
                ORDER BY distance ASC
                LIMIT @limit";
            
            command.Parameters.AddWithValue("@queryVector", request.QueryVector);
            command.Parameters.AddWithValue("@userId", request.UserId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@agentId", request.AgentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@runId", request.RunId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@threshold", request.ScoreThreshold ?? 1.0);
            command.Parameters.AddWithValue("@limit", request.Limit);
            
            var results = new List<VectorSearchResult>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            
            while (await reader.ReadAsync(cancellationToken))
            {
                var result = new VectorSearchResult
                {
                    Id = reader.GetInt32("id"),
                    Content = reader.GetString("content"),
                    UserId = reader.IsDBNull("user_id") ? null : reader.GetString("user_id"),
                    AgentId = reader.IsDBNull("agent_id") ? null : reader.GetString("agent_id"),
                    RunId = reader.IsDBNull("run_id") ? null : reader.GetString("run_id"),
                    Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString("metadata")) ?? new Dictionary<string, object>(),
                    CreatedAt = reader.GetDateTime("created_at"),
                    UpdatedAt = reader.GetDateTime("updated_at"),
                    Score = 1.0 - reader.GetDouble("distance") // Convert distance to similarity score
                };
                
                results.Add(result);
            }
            
            _logger.LogDebug("Found {ResultCount} vectors for search query", results.Count);
            return results;
        });
    }
}
```

**Metadata Filtering with SQL**:
- Session-based filtering using SQL WHERE clauses
- Rich filtering capabilities using SQLite's JSON functions
- Support for exact match, range, and existence filters
- Complex boolean logic with AND/OR/NOT operations
- Optimized filter execution with proper indexing

**Performance Optimization**:
- Intelligent indexing strategies for large collections
- Query optimization based on access patterns
- Memory management for efficient resource utilization
- WAL mode optimization for concurrent access
- Connection pooling for high-throughput scenarios

### 3. Vector Store Factory with Session Pattern

**Provider Management**:
- Dynamic provider registration and discovery
- Configuration-driven provider selection
- Session factory integration for reliable connections
- Extensible architecture for custom providers

**Session Integration**:
```csharp
public class VectorStoreFactory : IVectorStoreFactory
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<VectorStoreFactory> _logger;

    public async Task<IVectorStore> CreateVectorStoreAsync(VectorStoreConfiguration config)
    {
        return config.Provider.ToLowerInvariant() switch
        {
            "sqlite" => new SqliteVectorStore(_logger),
            _ => throw new NotSupportedException($"Vector store provider '{config.Provider}' is not supported")
        };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Initialize database schema using session factory
        await _sessionFactory.InitializeDatabaseAsync(cancellationToken);
    }
}
```

**Configuration Handling**:
- Provider-specific configuration validation
- Environment variable integration for secure credential management
- Default configuration templates for common scenarios
- Session factory configuration integration

### 4. Embedding Manager with Session Support

**Purpose**: Manages embedding generation and caching for optimal performance and cost efficiency with session-scoped operations.

**Caching Strategy**:
- LRU cache with configurable size limits
- Intelligent cache key generation based on content and operation type
- Cache warming for frequently accessed patterns
- Session-aware caching for multi-tenant scenarios

**Batch Processing with Session Pattern**:
```csharp
public class EmbeddingManager : IEmbeddingManager
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EmbeddingManager> _logger;

    public async Task<float[]> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default)
    {
        var cacheKey = GenerateCacheKey(content);
        
        if (_cache.TryGetValue(cacheKey, out float[]? cachedEmbedding))
        {
            _logger.LogDebug("Cache hit for embedding generation");
            return cachedEmbedding!;
        }

        var embedding = await _embeddingProvider.GenerateEmbeddingAsync(content, cancellationToken);
        
        _cache.Set(cacheKey, embedding, TimeSpan.FromHours(24));
        _logger.LogDebug("Generated and cached embedding for content");
        
        return embedding;
    }

    public async Task<List<float[]>> GenerateBatchEmbeddingsAsync(List<string> contents, CancellationToken cancellationToken = default)
    {
        var results = new List<float[]>();
        var uncachedContents = new List<(int index, string content)>();

        // Check cache for existing embeddings
        for (int i = 0; i < contents.Count; i++)
        {
            var cacheKey = GenerateCacheKey(contents[i]);
            if (_cache.TryGetValue(cacheKey, out float[]? cachedEmbedding))
            {
                results.Add(cachedEmbedding!);
            }
            else
            {
                uncachedContents.Add((i, contents[i]));
                results.Add(null!); // Placeholder
            }
        }

        // Generate embeddings for uncached content
        if (uncachedContents.Any())
        {
            var uncachedTexts = uncachedContents.Select(x => x.content).ToList();
            var newEmbeddings = await _embeddingProvider.GenerateBatchEmbeddingsAsync(uncachedTexts, cancellationToken);

            // Update results and cache
            for (int i = 0; i < uncachedContents.Count; i++)
            {
                var (index, content) = uncachedContents[i];
                var embedding = newEmbeddings[i];
                
                results[index] = embedding;
                
                var cacheKey = GenerateCacheKey(content);
                _cache.Set(cacheKey, embedding, TimeSpan.FromHours(24));
            }
        }

        return results;
    }
}
```

## Advanced Features

### 1. Session-Based Metadata Filtering System

**Session Isolation**:
- Strict filtering based on session identifiers (user_id, agent_id, run_id)
- Multi-tenant support with secure data separation using SQL WHERE clauses
- Access control validation for all operations
- Audit trail for compliance and debugging

**Content-Based Filtering**:
- Filtering by memory type, role, and content categories using SQLite JSON functions
- Temporal filtering for time-based queries
- Custom metadata field filtering for application-specific needs
- Complex filter composition with boolean logic

**Performance Optimization**:
- Index creation for frequently filtered fields
- Query optimization based on filter selectivity
- Session-scoped query caching for repeated operations
- Monitoring of filter performance and optimization

### 2. Batch Operations with Session Pattern

**Bulk Data Loading**:
```csharp
public async Task<List<int>> InsertBatchAsync(ISqliteSession session, List<VectorRecord> records, CancellationToken cancellationToken = default)
{
    return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
    {
        var ids = new List<int>();
        
        foreach (var record in records)
        {
            var id = await InsertSingleRecordAsync(connection, transaction, record, cancellationToken);
            ids.Add(id);
        }
        
        _logger.LogDebug("Inserted {RecordCount} vector records in batch", records.Count);
        return ids;
    });
}
```

**Batch Search Operations**:
- Multiple query processing in single sessions
- Result aggregation and deduplication
- Performance optimization through query batching
- Resource management for concurrent operations

### 3. Performance Monitoring with Session Metrics

**Metrics Collection**:
- Session creation and disposal time tracking
- Operation latency and throughput tracking
- Error rate monitoring and alerting
- Resource utilization monitoring (CPU, memory, disk)
- Cache performance and hit rate analysis
- Connection leak detection and prevention

**Performance Optimization**:
- Query performance analysis and optimization
- Index usage monitoring and optimization
- Session pool monitoring and tuning
- Capacity planning and scaling recommendations

**Session Health Monitoring**:
```csharp
public class VectorStoreHealthMonitor
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<VectorStoreHealthMonitor> _logger;

    public async Task<HealthStatus> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
            
            var healthStatus = await session.GetHealthAsync();
            var metrics = await _sessionFactory.GetMetricsAsync();
            
            return new HealthStatus
            {
                IsHealthy = healthStatus.IsHealthy && metrics.ConnectionLeaksDetected == 0,
                SessionMetrics = metrics,
                LastChecked = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return new HealthStatus
            {
                IsHealthy = false,
                ErrorMessage = ex.Message,
                LastChecked = DateTime.UtcNow
            };
        }
    }
}
```

## Integration Patterns

### 1. Memory System Integration with Session Pattern

**Repository Pattern Integration**:
```csharp
public class MemoryRepository : IMemoryRepository
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingManager _embeddingManager;

    public async Task<int> AddMemoryAsync(MemoryEntity memory, CancellationToken cancellationToken = default)
    {
        using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        // Generate embedding
        var embedding = await _embeddingManager.GenerateEmbeddingAsync(memory.Content, cancellationToken);
        
        // Create vector record
        var vectorRecord = new VectorRecord
        {
            Content = memory.Content,
            Embedding = embedding,
            UserId = memory.UserId,
            AgentId = memory.AgentId,
            RunId = memory.RunId,
            Metadata = memory.Metadata
        };
        
        // Insert using session-scoped vector store
        return await _vectorStore.InsertAsync(session, vectorRecord, cancellationToken);
    }

    public async Task<List<MemorySearchResult>> SearchMemoriesAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        // Generate query embedding
        var queryEmbedding = await _embeddingManager.GenerateEmbeddingAsync(request.Query, cancellationToken);
        
        // Create vector search request
        var vectorRequest = new VectorSearchRequest
        {
            QueryVector = queryEmbedding,
            UserId = request.UserId,
            AgentId = request.AgentId,
            RunId = request.RunId,
            Limit = request.Limit,
            ScoreThreshold = request.ScoreThreshold
        };
        
        // Search using session-scoped vector store
        var vectorResults = await _vectorStore.SearchAsync(session, vectorRequest, cancellationToken);
        
        // Convert to memory search results
        return vectorResults.Select(vr => new MemorySearchResult
        {
            Id = vr.Id,
            Content = vr.Content,
            Score = vr.Score,
            Metadata = vr.Metadata,
            CreatedAt = vr.CreatedAt
        }).ToList();
    }
}
```

### 2. Service Layer Integration

**Dependency Injection Configuration**:
```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVectorStorage(this IServiceCollection services, IConfiguration configuration)
    {
        // Register session factory
        services.AddSingleton<ISqliteSessionFactory>(provider =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            var logger = provider.GetRequiredService<ILogger<SqliteSessionFactory>>();
            return new SqliteSessionFactory(connectionString, logger);
        });

        // Register vector store components
        services.AddScoped<IVectorStore, SqliteVectorStore>();
        services.AddScoped<IVectorStoreFactory, VectorStoreFactory>();
        services.AddScoped<IEmbeddingManager, EmbeddingManager>();
        services.AddScoped<IMemoryRepository, MemoryRepository>();

        return services;
    }
}
```

## Testing Strategy with Session Pattern

### 1. Unit Testing with Test Sessions

```csharp
[TestClass]
public class VectorStoreTests
{
    private ISqliteSessionFactory _sessionFactory;
    private IVectorStore _vectorStore;

    [TestInitialize]
    public async Task Setup()
    {
        var logger = new Mock<ILogger<TestSqliteSessionFactory>>().Object;
        _sessionFactory = new TestSqliteSessionFactory(logger);
        
        var storeLogger = new Mock<ILogger<SqliteVectorStore>>().Object;
        _vectorStore = new SqliteVectorStore(storeLogger);
    }

    [TestMethod]
    public async Task InsertAsync_ShouldCreateVectorWithUniqueId()
    {
        // Arrange
        using var session = await _sessionFactory.CreateSessionAsync();
        
        var record = new VectorRecord
        {
            Content = "Test content",
            Embedding = new float[] { 0.1f, 0.2f, 0.3f },
            UserId = "user123"
        };

        // Act
        var id = await _vectorStore.InsertAsync(session, record);

        // Assert
        Assert.IsTrue(id > 0);
        
        var retrieved = await _vectorStore.GetAsync(session, id);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("Test content", retrieved.Content);
    }

    [TestMethod]
    public async Task SearchAsync_ShouldReturnRelevantResults()
    {
        // Arrange
        using var session = await _sessionFactory.CreateSessionAsync();
        
        // Insert test data
        var record1 = new VectorRecord
        {
            Content = "Machine learning algorithms",
            Embedding = new float[] { 0.8f, 0.1f, 0.1f },
            UserId = "user123"
        };
        
        var record2 = new VectorRecord
        {
            Content = "Cooking recipes",
            Embedding = new float[] { 0.1f, 0.8f, 0.1f },
            UserId = "user123"
        };
        
        await _vectorStore.InsertAsync(session, record1);
        await _vectorStore.InsertAsync(session, record2);

        // Act
        var searchRequest = new VectorSearchRequest
        {
            QueryVector = new float[] { 0.7f, 0.2f, 0.1f }, // Similar to record1
            UserId = "user123",
            Limit = 10
        };
        
        var results = await _vectorStore.SearchAsync(session, searchRequest);

        // Assert
        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual("Machine learning algorithms", results[0].Content);
        Assert.IsTrue(results[0].Score > 0.5); // Should have high similarity
    }
}
```

### 2. Integration Testing

```csharp
[TestClass]
public class VectorStoreIntegrationTests
{
    [TestMethod]
    public async Task EndToEndWorkflow_ShouldWorkCorrectly()
    {
        // Test complete workflow from memory addition to search
        // with session pattern ensuring proper cleanup
    }

    [TestMethod]
    public async Task ConcurrentOperations_ShouldNotInterfere()
    {
        // Test concurrent operations with session isolation
        // ensuring no cross-contamination between sessions
    }
}
```

## Conclusion

The enhanced Vector Storage system with Database Session Pattern provides a robust, reliable, and performant foundation for semantic similarity search operations. Key benefits include:

- **Simplified Deployment**: No external dependencies with SQLite and sqlite-vec
- **Reliable Resource Management**: Guaranteed connection cleanup and proper WAL checkpoint handling
- **Test Isolation**: Complete separation between test runs with automatic cleanup
- **Session-Scoped Operations**: All vector operations properly scoped within database sessions
- **Performance Optimization**: Optimized SQLite operations with proper indexing and caching
- **Production Ready**: Connection pooling, health monitoring, and comprehensive error handling

This architecture ensures that the Memory MCP Server can reliably handle vector storage operations in both development and production environments while maintaining high performance and data integrity.
<!-- End VectorStorage.md content -->
```

</details>
