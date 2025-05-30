# Memory MCP Server - Execution Plan

## Overview

This document outlines the comprehensive execution plan for implementing the Memory MCP server in C#. The plan is structured in phases to ensure systematic development, testing, and deployment while maintaining quality and meeting all functional requirements.

**IMPORTANT UPDATE**: This execution plan has been updated to include a critical new phase for implementing the Database Session Pattern architecture to address SQLite connection management issues identified during development. This new architecture ensures reliable connection handling, proper resource cleanup, and robust test isolation.

## 1. Project Structure and Timeline

### 1.1 Development Phases

**Phase 1: Foundation and Infrastructure (Week 1-2)**
- Core infrastructure setup
- Database schema and SQLite integration
- Basic MCP protocol implementation
- Session management foundation

**Phase 1.5: Database Session Pattern Implementation (Week 2.5-3.5)**
- **NEW PHASE**: Critical architecture update for reliable SQLite connection management
- Implementation of Database Session Pattern
- Migration of existing code to new architecture
- Comprehensive testing of connection lifecycle
- Test environment isolation improvements

**Phase 2: Core Memory Operations (Week 4-5)**
- Memory storage and retrieval using new session pattern
- Integer ID management
- Basic search functionality
- Session isolation implementation

**Phase 3: Intelligence Layer (Week 6-7)**
- LLM provider integration
- Fact extraction engine
- Memory decision engine
- Advanced search capabilities

**Phase 4: Session Defaults and Advanced Features (Week 8-9)**
- HTTP header processing
- Session initialization
- Advanced MCP tools
- Performance optimization

**Phase 5: Testing and Quality Assurance (Week 10-11)**
- Comprehensive testing suite with session pattern validation
- Integration testing
- Performance testing
- Security validation

**Phase 6: Documentation and Deployment (Week 12-13)**
- API documentation
- Deployment configuration
- Monitoring setup
- Final validation

### 1.2 Key Milestones

- **M1**: Basic SQLite infrastructure working (End of Week 1)
- **M1.5**: Database Session Pattern implemented and tested (End of Week 3.5) **NEW MILESTONE**
- **M2**: Core memory operations functional with session pattern (End of Week 5)
- **M3**: LLM integration complete (End of Week 7)
- **M4**: Session defaults implemented (End of Week 9)
- **M5**: All tests passing with robust connection management (End of Week 11)
- **M6**: Production ready deployment (End of Week 13)

## 1.5. Phase 1.5: Database Session Pattern Implementation

### 1.5.1 Architecture Overview

**Problem Statement**: 
The current SQLite connection management approach leads to file locking issues during tests, particularly with WAL mode, connection pooling conflicts, and improper resource disposal. This causes test failures and potential production reliability issues.

**Solution**: 
Implement a Database Session Pattern that encapsulates connection lifecycle management, ensures proper resource cleanup, and provides test-friendly isolation mechanisms.

### 1.5.2 Core Components to Implement

#### ISqliteSession Interface
```csharp
public interface ISqliteSession : IAsyncDisposable
{
    Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation);
    Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation);
    Task ExecuteAsync(Func<SqliteConnection, Task> operation);
    Task ExecuteInTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> operation);
}
```

#### ISqliteSessionFactory Interface
```csharp
public interface ISqliteSessionFactory
{
    Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default);
    Task<ISqliteSession> CreateSessionAsync(string connectionString, CancellationToken cancellationToken = default);
}
```

### 1.5.3 Implementation Tasks

**Week 2.5: Core Session Implementation**
1. Create ISqliteSession and ISqliteSessionFactory interfaces
2. Implement SqliteSession with proper disposal and WAL checkpoint handling
3. Implement SqliteSessionFactory for production use
4. Create TestSqliteSessionFactory for test isolation
5. Add comprehensive error handling and retry logic

**Week 3: Repository Migration**
1. Update all repository classes to use ISqliteSessionFactory
2. Migrate GraphRepository to use session pattern
3. Update MemoryRepository to use session pattern
4. Remove direct SqliteManager dependencies from repositories
5. Implement proper transaction scoping through sessions

**Week 3.5: Service Layer Integration**
1. Update service layer dependency injection to use ISqliteSessionFactory
2. Migrate all database operations to use session pattern
3. Update error handling to work with session lifecycle
4. Implement session-based performance monitoring
5. Complete testing and validation of new architecture

### 1.5.4 Migration Strategy

**Backward Compatibility**:
- Maintain existing SqliteManager for gradual migration
- Implement adapter pattern for smooth transition
- Ensure no breaking changes to public APIs
- Provide migration utilities for existing data

**Testing Strategy**:
- Comprehensive unit tests for session implementations
- Integration tests with real SQLite databases
- Performance benchmarks comparing old vs new approach
- Stress testing for connection leak detection
- Test isolation validation

**Risk Mitigation**:
- Parallel implementation allowing rollback if needed
- Extensive testing before migration
- Performance monitoring during transition
- Gradual rollout with feature flags

### 1.5.5 Expected Benefits

**Reliability Improvements**:
- Eliminates SQLite file locking issues in tests
- Proper WAL mode handling and cleanup
- Guaranteed connection disposal and resource cleanup
- Reduced connection pool conflicts

**Test Environment Benefits**:
- Deterministic test cleanup procedures
- Complete test isolation between runs
- Connection leak detection and prevention
- Faster test execution with proper resource management

**Production Benefits**:
- More reliable connection management
- Better error handling and recovery
- Improved performance monitoring
- Reduced resource leaks and memory usage

## 2. Phase 1: Foundation and Infrastructure

### 2.1 Project Setup and Structure

**Tasks**:
1. Create project structure and solution files
2. Set up dependency injection container
3. Configure logging and monitoring
4. Establish coding standards and CI/CD pipeline

**Deliverables**:
- `MemoryServer.csproj` with all required dependencies
- `Program.cs` with proper service registration including session factory
- `appsettings.json` with configuration schema
- Basic logging configuration

**Implementation Steps**:

#### Step 1.1: Create Project Structure
```bash
# Create the main server project
dotnet new webapi -n MemoryServer -o McpServers/Memory/MemoryServer

# Add required NuGet packages
cd McpServers/Memory/MemoryServer
dotnet add package Microsoft.Data.Sqlite
dotnet add package Microsoft.Extensions.Hosting
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Configuration
dotnet add package Microsoft.Extensions.Logging
dotnet add package System.Text.Json
dotnet add package Microsoft.Extensions.Caching.Memory
```

#### Step 1.2: Basic Service Registration with Session Factory
```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Add services with new session pattern
builder.Services.AddMemoryMcpServer(builder.Configuration);
builder.Services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();
builder.Services.AddLogging();
builder.Services.AddMemoryCache();

var app = builder.Build();

// Configure pipeline
app.UseRouting();
app.MapGet("/health", () => "Healthy");

app.Run();
```

### 2.2 SQLite Database Infrastructure with Session Pattern

**Tasks**:
1. Implement Database Session Pattern interfaces and implementations
2. Create database schema with integer IDs
3. Set up sqlite-vec extension loading through sessions
4. Implement database initialization and migration with session management

**Deliverables**:
- `ISqliteSession.cs` and `ISqliteSessionFactory.cs` interfaces
- `SqliteSession.cs` and `SqliteSessionFactory.cs` implementations
- `TestSqliteSessionFactory.cs` for test isolation
- Database schema SQL scripts
- Session-based extension loading mechanism
- Migration system using session pattern

**Implementation Steps**:

#### Step 2.1: Database Session Pattern Implementation
```csharp
public class SqliteSession : ISqliteSession
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSession> _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation)
    {
        await EnsureConnectionAsync();
        return await operation(_connection!);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation)
    {
        await EnsureConnectionAsync();
        using var transaction = _connection!.BeginTransaction();
        try
        {
            var result = await operation(_connection, transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null && !_disposed)
        {
            // Force WAL checkpoint before closing
            await ExecuteAsync(async conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                await cmd.ExecuteNonQueryAsync();
                return true;
            });
            
            await _connection.DisposeAsync();
            _connection = null;
        }
        _disposed = true;
    }

    private async Task EnsureConnectionAsync()
    {
        if (_connection == null)
        {
            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync();
            
            // Enable extensions and load sqlite-vec
            _connection.EnableExtensions(true);
            _connection.LoadExtension("vec0");
        }
    }
}
```

#### Step 2.2: Session Factory Implementation
```csharp
public class SqliteSessionFactory : ISqliteSessionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionFactory> _logger;

    public async Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = new SqliteSession(_connectionString, _logger);
        return session;
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        using var session = await CreateSessionAsync(cancellationToken);
        
        await session.ExecuteAsync(async connection =>
        {
            // Execute schema creation scripts
            await ExecuteSchemaScriptsAsync(connection, cancellationToken);
            return true;
        });
    }
}
```

#### Step 2.3: Test Session Factory for Isolation
```csharp
public class TestSqliteSessionFactory : ISqliteSessionFactory
{
    private readonly string _testDatabasePath;
    private readonly ILogger<TestSqliteSessionFactory> _logger;

    public async Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        // Create unique database file for each test session
        var uniqueDbPath = Path.Combine(Path.GetTempPath(), $"test_memory_{Guid.NewGuid()}.db");
        var connectionString = $"Data Source={uniqueDbPath};Mode=ReadWriteCreate;";
        
        var session = new TestSqliteSession(connectionString, _logger, uniqueDbPath);
        await session.InitializeAsync();
        return session;
    }
}

public class TestSqliteSession : SqliteSession
{
    private readonly string _databasePath;

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        
        // Clean up test database file
        try
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
            
            // Clean up WAL and SHM files
            var walPath = _databasePath + "-wal";
            var shmPath = _databasePath + "-shm";
            
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up test database files");
        }
    }
}
```

## 3. Phase 2: Core Memory Operations (Updated for Session Pattern)

### 3.1 Integer ID Management with Session Pattern

**Tasks**:
1. Implement MemoryIdGenerator using session pattern
2. Create secure ID generation through sessions
3. Set up ID validation
4. Implement ID caching for performance

**Deliverables**:
- `MemoryIdGenerator.cs` with session-based secure generation
- ID validation utilities
- Performance optimizations

**Implementation Steps**:

#### Step 3.1.1: Memory ID Generator with Session Pattern
```csharp
public class MemoryIdGenerator
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<MemoryIdGenerator> _logger;

    public async Task<int> GenerateNextIdAsync(CancellationToken cancellationToken = default)
    {
        using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            
            command.CommandText = @"
                INSERT INTO memory_id_sequence DEFAULT VALUES;
                SELECT last_insert_rowid();";
            
            var result = await command.ExecuteScalarAsync(cancellationToken);
            var id = Convert.ToInt32(result);
            
            // Validate ID range
            if (id <= 0 || id > int.MaxValue - 1000)
            {
                throw new InvalidOperationException("Generated ID is out of acceptable range");
            }
            
            return id;
        });
    }
}
```

### 3.2 Repository Pattern Updates for Session Management

**Tasks**:
1. Update all repository classes to use ISqliteSessionFactory
2. Implement session-scoped operations
3. Remove direct SqliteManager dependencies
4. Add proper transaction management through sessions

**Deliverables**:
- Updated `GraphRepository.cs` using session pattern
- Updated `MemoryRepository.cs` using session pattern
- Session-based transaction management
- Improved error handling and resource cleanup

**Implementation Example**:
```csharp
public class GraphRepository : IGraphRepository
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<GraphRepository> _logger;

    public async Task<int> AddEntityAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            // Generate new ID
            var id = await GenerateEntityIdAsync(connection, transaction);
            
            // Insert entity
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO entities (id, name, type, aliases, user_id, agent_id, run_id, confidence, metadata)
                VALUES (@id, @name, @type, @aliases, @userId, @agentId, @runId, @confidence, @metadata)";
            
            // Add parameters...
            await command.ExecuteNonQueryAsync(cancellationToken);
            
            return id;
        });
    }
}
```

## 4. Phase 3: Intelligence Layer

### 4.1 LLM Provider Integration

**Tasks**:
1. Integrate with existing OpenAI provider
2. Integrate with existing Anthropic provider
3. Implement provider factory pattern
4. Set up fallback mechanisms

**Deliverables**:
- `LlmProviderFactory.cs` integration
- Provider-specific implementations
- Fallback and retry logic
- Configuration management

### 4.2 Fact Extraction Engine

**Tasks**:
1. Implement fact extraction prompts
2. Create structured output parsing
3. Set up custom prompt support
4. Implement extraction validation

**Deliverables**:
- `FactExtractionEngine.cs`
- Prompt templates
- Output validation
- Custom prompt handling

### 4.3 Memory Decision Engine

**Tasks**:
1. Implement decision-making logic
2. Create integer ID mapping for LLMs
3. Set up operation generation
4. Implement decision validation

**Deliverables**:
- `MemoryDecisionEngine.cs`
- Integer mapping utilities
- Decision validation
- Operation processing

### 4.4 Advanced Search Features

**Tasks**:
1. Implement semantic similarity scoring
2. Set up relevance ranking
3. Create search result optimization
4. Implement search analytics

**Deliverables**:
- Enhanced search algorithms
- Relevance scoring
- Performance optimizations
- Search metrics

## 5. Phase 4: Session Defaults and Advanced Features

### 5.1 HTTP Header Processing

**Tasks**:
1. Implement header extraction
2. Create header validation
3. Set up session default storage
4. Implement security measures

**Deliverables**:
- `HeaderProcessor.cs`
- Header validation utilities
- Security implementations
- Error handling

### 5.2 Session Initialization Tool

**Tasks**:
1. Implement InitSession tool
2. Create session context resolution
3. Set up precedence handling
4. Implement session management

**Deliverables**:
- `InitSessionTool.cs`
- `SessionContextResolver.cs`
- Precedence logic
- Session lifecycle management

### 5.3 Advanced MCP Tools

**Tasks**:
1. Implement UpdateMemory tool
2. Create DeleteMemory tools
3. Set up GetHistory tool
4. Implement GetStats tool

**Deliverables**:
- `UpdateMemoryTool.cs`
- `DeleteMemoryTool.cs` and `DeleteAllMemoriesTool.cs`
- `GetHistoryTool.cs`
- `GetStatsTool.cs`

### 5.4 Performance Optimization

**Tasks**:
1. Implement caching strategies
2. Optimize database queries
3. Set up connection pooling
4. Implement performance monitoring

**Deliverables**:
- Caching implementations
- Query optimizations
- Performance metrics
- Monitoring setup

## 6. Phase 5: Testing and Quality Assurance

### 6.1 Unit Testing Suite

**Tasks**:
1. Create comprehensive unit tests
2. Implement test data factories
3. Set up mock providers
4. Create test utilities

**Deliverables**:
- Complete unit test coverage
- Test data generators
- Mock implementations
- Test utilities library

**Test Categories**:
- Memory operations testing
- Session management testing
- Integer ID generation testing
- LLM provider integration testing
- Search functionality testing

### 6.2 Integration Testing

**Tasks**:
1. Create end-to-end test scenarios
2. Set up database integration tests
3. Implement MCP protocol tests
4. Create performance benchmarks

**Deliverables**:
- Integration test suite
- Database test scenarios
- MCP protocol validation
- Performance benchmarks

### 6.3 Security Testing

**Tasks**:
1. Validate session isolation
2. Test input sanitization
3. Verify access control
4. Test header validation

**Deliverables**:
- Security test suite
- Penetration testing results
- Access control validation
- Security documentation

## 7. Phase 6: Documentation and Deployment

### 7.1 API Documentation

**Tasks**:
1. Create comprehensive API documentation
2. Document all MCP tools
3. Create usage examples
4. Set up interactive documentation

**Deliverables**:
- Complete API documentation
- Tool reference guide
- Usage examples
- Interactive docs

### 7.2 Deployment Configuration

**Tasks**:
1. Create Docker configuration
2. Set up environment configurations
3. Implement health checks
4. Create deployment scripts

**Deliverables**:
- Dockerfile and docker-compose
- Environment configurations
- Health check endpoints
- Deployment automation

### 7.3 Monitoring and Observability

**Tasks**:
1. Set up application metrics
2. Implement logging standards
3. Create monitoring dashboards
4. Set up alerting

**Deliverables**:
- Metrics collection
- Structured logging
- Monitoring dashboards
- Alert configurations

## 8. Risk Management and Mitigation (Updated)

### 8.1 Technical Risks

**Risk**: Database Session Pattern implementation complexity
**Mitigation**: Comprehensive testing, gradual migration, rollback capability
**Timeline Impact**: Additional 1 week for implementation and testing

**Risk**: SQLite connection management issues (RESOLVED)
**Mitigation**: Database Session Pattern implementation addresses this completely
**Timeline Impact**: None, built into new Phase 1.5

**Risk**: Performance impact of session pattern
**Mitigation**: Performance benchmarking, optimization during implementation
**Timeline Impact**: Potential optimization phase extension

**Risk**: SQLite-vec extension compatibility issues
**Mitigation**: Early testing with extension, fallback to alternative vector storage
**Timeline Impact**: Potential 1-week delay

**Risk**: LLM provider API changes
**Mitigation**: Use existing workspace providers, implement adapter pattern
**Timeline Impact**: Minimal, existing infrastructure

### 8.2 Integration Risks

**Risk**: MCP protocol specification changes
**Mitigation**: Follow stable MCP specification, implement flexible protocol layer
**Timeline Impact**: Potential 2-3 day delay

**Risk**: Workspace provider integration issues
**Mitigation**: Early integration testing, close collaboration with existing code
**Timeline Impact**: Minimal, leveraging existing patterns

### 8.3 Quality Risks

**Risk**: Insufficient test coverage
**Mitigation**: Dedicated testing phase, continuous testing throughout development
**Timeline Impact**: None, built into schedule

**Risk**: Security vulnerabilities
**Mitigation**: Security review in each phase, dedicated security testing
**Timeline Impact**: None, built into schedule

## 9. Success Criteria and Validation (Updated)

### 9.1 Functional Validation

- [ ] Database Session Pattern implemented and working correctly
- [ ] All SQLite connection issues resolved with proper cleanup
- [ ] Test isolation working reliably with no file locking issues
- [ ] All MCP tools implemented and functional with session pattern
- [ ] Session isolation working correctly with SQLite
- [ ] Integer IDs providing better LLM integration
- [ ] Session defaults working via HTTP headers and initialization
- [ ] Vector and text search providing accurate results
- [ ] Performance requirements met (search < 500ms, add < 1000ms)

### 9.2 Quality Validation

- [ ] Database Session Pattern has >95% test coverage
- [ ] No connection leaks detected in stress testing
- [ ] All tests pass reliably without file locking issues
- [ ] Code coverage > 80% overall
- [ ] All security requirements validated
- [ ] Performance benchmarks met with session pattern
- [ ] Integration tests passing
- [ ] Documentation complete and accurate

### 9.3 Integration Validation

- [ ] MCP server discoverable by clients
- [ ] Seamless integration with existing LLM providers
- [ ] SQLite operations reliable and performant
- [ ] Session management robust and secure

## 10. Resource Requirements

### 10.1 Development Resources

- **Primary Developer**: Full-time for 12 weeks
- **Code Review**: 2-3 hours per week
- **Testing Support**: 1-2 days per phase
- **Documentation**: 1 week dedicated time

### 10.2 Infrastructure Resources

- **Development Environment**: Local development setup
- **Testing Environment**: Isolated test environment
- **CI/CD Pipeline**: Automated build and test
- **Monitoring Tools**: Application performance monitoring

### 10.3 External Dependencies

- **SQLite-vec Extension**: Download and integration
- **Existing LLM Providers**: Workspace integration
- **MCP Specification**: Protocol compliance
- **Testing Frameworks**: xUnit, Moq, TestContainers

## 11. Delivery Schedule (Updated)

### Week 1-2: Foundation
- Project setup and infrastructure
- Basic SQLite integration and schema
- Basic MCP protocol implementation

### Week 2.5-3.5: Database Session Pattern (NEW)
- Implementation of Database Session Pattern
- Migration of existing code to new architecture
- Comprehensive testing and validation
- Performance optimization

### Week 4-5: Core Operations (Updated)
- Memory storage and retrieval using session pattern
- Integer ID management with sessions
- Basic search functionality with session isolation

### Week 6-7: Intelligence (Updated)
- LLM provider integration with session pattern
- Fact extraction and decision engines
- Advanced search capabilities

### Week 8-9: Advanced Features
- Session defaults implementation
- Advanced MCP tools
- Performance optimization

### Week 10-11: Testing (Updated)
- Comprehensive testing suite including session pattern validation
- Integration and performance testing
- Security validation

### Week 12-13: Deployment (Updated)
- Documentation completion
- Deployment configuration
- Final validation and delivery

This updated execution plan provides a comprehensive roadmap for implementing the Memory MCP server with the new Database Session Pattern architecture, ensuring reliable SQLite connection management and robust test isolation. 