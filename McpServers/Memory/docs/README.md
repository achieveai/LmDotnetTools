# Memory MCP Server - Documentation

## Overview

This directory contains comprehensive design documentation for a simplified yet sophisticated memory management system inspired by mem0. The design focuses on two primary LLM providers (OpenAI and Anthropic) and SQLite with sqlite-vec as the vector storage solution, providing a production-ready architecture with reduced complexity and enhanced reliability through the Database Session Pattern.

## Design Philosophy

**Simplicity Through Focus**: Rather than supporting 15+ providers like the original mem0, this design focuses on proven, reliable solutions that cover the majority of use cases while maintaining sophisticated functionality.

**Production-First Architecture**: Every component is designed with production deployment in mind, including comprehensive error handling, monitoring, performance optimization, and scalability considerations.

**Reliable Resource Management**: The Database Session Pattern ensures proper SQLite connection lifecycle management, eliminates file locking issues, provides proper resource cleanup, and enables robust test isolation.

**Intelligent Memory Management**: Advanced fact extraction and decision-making capabilities that understand context, resolve conflicts, and maintain consistency across the memory system.

**Type Safety and Modern Patterns**: Full type annotations, dependency injection, factory patterns, and async-first design throughout the system.

## Document Structure

### Core Design Documents

1. **[DeepDesignDoc.md](./DeepDesignDoc.md)** - Comprehensive system architecture
   - High-level system design and component interactions
   - Database Session Pattern architecture for reliable connection management
   - Data flow diagrams and processing pipelines
   - Integration patterns and deployment strategies
   - Performance optimization and monitoring approaches

2. **[FunctionalRequirements.md](./FunctionalRequirements.md)** - Detailed functional specifications
   - Complete MCP tool specifications with session pattern integration
   - Session management and isolation requirements
   - Database Session Pattern functional requirements (FR-DB-001 through FR-DB-020)
   - Performance, security, and reliability requirements
   - Testing and validation criteria

3. **[ExecutionPlan.md](./ExecutionPlan.md)** - Implementation roadmap
   - Phase-by-phase development plan including Database Session Pattern implementation
   - Phase 1.5: Critical Database Session Pattern implementation phase
   - Milestone definitions and success criteria
   - Risk assessment and mitigation strategies
   - Resource requirements and timeline

### Technical Specifications

4. **[SqliteAsGotoDb.md](./SqliteAsGotoDb.md)** - SQLite storage architecture
   - Database Session Pattern implementation details
   - SQLite with sqlite-vec integration for vector operations
   - Session-scoped connection management and resource cleanup
   - Test isolation mechanisms and production reliability
   - Schema design and performance optimization

5. **[MemoryCore.md](./MemoryCore.md)** - Core memory management
   - Memory and AsyncMemory class implementations with session pattern
   - Session-scoped database operations and transaction management
   - Dual processing modes (inference vs direct)
   - Component orchestration and error handling
   - Performance optimization and caching strategies

6. **[DataModels.md](./DataModels.md)** - Data structures and schemas
   - Database Session Pattern interfaces (ISqliteSession, ISqliteSessionFactory)
   - Session configuration and performance metrics models
   - Core memory entities with integer ID support
   - Session context and isolation models
   - Graph memory and relationship structures

7. **[VectorStorage.md](./VectorStorage.md)** - Vector storage system
   - SQLite with sqlite-vec integration for semantic similarity search
   - Session isolation and metadata filtering
   - Graph memory integration with relationship extraction
   - Performance optimization and monitoring

### Implementation Guides

8. **[LLMProviders.md](./LLMProviders.md)** - LLM provider integration
   - OpenAI and Anthropic provider implementations
   - Structured output handling and response parsing
   - Error handling and fallback mechanisms
   - Cost optimization and rate limiting

9. **[MemoryDecisionEngine.md](./MemoryDecisionEngine.md)** - Decision-making system
   - AI-powered memory operation decisions
   - Conflict resolution and consistency management
   - Integer ID mapping for LLM compatibility
   - Temporal reasoning and relationship analysis

10. **[FactExtraction.md](./FactExtraction.md)** - Fact extraction engine
    - LLM-powered fact extraction from conversations
    - Custom prompt configuration and domain adaptation
    - Multi-language support and cultural considerations
    - Quality validation and filtering mechanisms

## Key Features

**Database Session Pattern**:
- Reliable SQLite connection lifecycle management
- Proper resource cleanup with automatic WAL checkpoint handling
- Complete test isolation with unique database instances
- Connection leak detection and prevention
- Production-ready connection pooling and monitoring

**Multi-Provider Support**:
- OpenAI and Anthropic LLM providers
- SQLite with sqlite-vec for vector storage
- Extensible architecture for future providers

**Advanced Capabilities**:
- Intelligent fact extraction from conversations
- AI-powered memory decision making (ADD/UPDATE/DELETE)
- Semantic similarity search with vector embeddings
- Full-text search with SQLite FTS5
- Session-based memory isolation
- Graph memory with entity and relationship extraction
- Procedural memory for agent workflow documentation
- Vision message processing for multimodal conversations

**Production Features**:
- Comprehensive error handling and recovery
- Performance monitoring and optimization
- Secure session management and access control
- Scalable architecture with horizontal scaling support
- Database session pattern for reliable resource management

## Architecture Highlights

### Simplified Provider Selection

**LLM Providers**: Focus on OpenAI (GPT-4, GPT-3.5) and Anthropic (Claude) for reliable, high-quality language understanding and generation.

**Vector Storage**: SQLite with sqlite-vec extension as the primary vector database, offering excellent performance, rich filtering capabilities, and simplified deployment without external dependencies.

**Embedding Providers**: Support for OpenAI embeddings with caching and batch processing for cost optimization.

### Database Session Pattern Benefits

**Reliability**: Eliminates SQLite file locking issues and connection leaks through proper resource management.

**Test Isolation**: Complete separation between test runs with automatic cleanup.

**Resource Management**: Guaranteed connection disposal and WAL checkpoint handling.

**Performance**: Optimized connection usage with monitoring and leak detection.

**Production Ready**: Connection pooling and health monitoring for robust deployment.

## Implementation Phases

### Phase 1: Foundation (Weeks 1-2)
**Foundation Components**
- LLM provider implementations (OpenAI, Anthropic)
- SQLite storage with Database Session Pattern integration
- Memory core classes with session management
- Basic configuration and error handling

**Deliverables**:
- Working LLM provider factory with structured output
- Functional SQLite vector storage with session-scoped CRUD operations
- Memory class with basic add/search functionality using session pattern
- Comprehensive test suite for core components with test isolation

### Phase 1.5: Database Session Pattern (Weeks 2.5-3.5) **NEW PHASE**
**Session Pattern Implementation**
- ISqliteSession and ISqliteSessionFactory interfaces
- Production and test session implementations
- Repository migration to session pattern
- Comprehensive testing and validation

**Deliverables**:
- Reliable SQLite connection management
- Test isolation and cleanup mechanisms
- Eliminated file locking issues
- Improved resource management and monitoring

### Phase 2: Core Operations (Weeks 4-5)
**Memory Operations**
- Session-scoped memory storage and retrieval
- Integer ID management for LLM compatibility
- Basic search functionality with session isolation
- Session defaults and HTTP header processing

**Deliverables**:
- Complete memory CRUD operations with session pattern
- Session isolation and security
- Integer ID generation and mapping
- HTTP header processing for session defaults

### Phase 3: Intelligence (Weeks 6-7)
**AI-Powered Features**
- Fact extraction engine with custom prompts
- Memory decision engine with conflict resolution
- Advanced search with semantic similarity
- Vision message processing

**Deliverables**:
- Intelligent memory operations (ADD/UPDATE/DELETE/NONE)
- Question-answering capabilities
- Vision and multimodal support
- Custom prompt configuration

### Phase 4: Advanced Features (Weeks 8-9)
**Production Features**
- Graph memory with entity and relationship extraction
- Procedural memory for agent workflows
- Performance optimization and caching
- Monitoring and observability

**Deliverables**:
- Full graph memory capabilities
- Agent workflow documentation
- Performance optimization
- Production monitoring

## Quality Standards

### Database Session Pattern Quality
**Resource Management**: 100% connection disposal rate with automatic cleanup validation.

**Test Isolation**: Complete separation between test runs with deterministic cleanup.

**Performance**: Session creation <100ms, disposal <500ms including WAL checkpoint.

**Reliability**: Zero tolerance for connection leaks in production environments.

### Type Safety
**Full Type Annotations**: Complete type coverage with validation throughout the system.

**Error Handling**: Comprehensive exception handling with proper logging and recovery.

**Testing**: >80% code coverage with unit, integration, and performance tests.

## Deployment Considerations

### Infrastructure Requirements
**Compute Resources**: Moderate CPU and memory requirements with horizontal scaling support.

**External Dependencies**: OpenAI/Anthropic API access and SQLite with sqlite-vec extension support.

**Storage Requirements**: Local SQLite database files with proper backup and recovery mechanisms.

### Scaling Strategies
**Horizontal Scaling**: Stateless design enables easy horizontal scaling of application instances.

**Database Scaling**: SQLite read replicas and connection pooling for improved performance.

**Caching**: Multi-level caching for embeddings, responses, and metadata.

## Getting Started

### Prerequisites
- .NET 9.0 SDK
- SQLite with sqlite-vec extension support
- OpenAI or Anthropic API key

### Quick Start Process
1. **Environment Setup**: Configure API keys and SQLite database
2. **Basic Configuration**: Set up minimal configuration for testing
3. **Component Testing**: Validate individual components work correctly
4. **Integration Testing**: Test complete memory workflows
5. **Session Pattern Validation**: Verify database session management and cleanup

This documentation provides a comprehensive guide for implementing a production-ready memory management system with simplified architecture, enhanced reliability through the Database Session Pattern, and sophisticated AI-powered capabilities.

---

## Overview

The Memory MCP Server is a sophisticated memory management system that provides intelligent storage, retrieval, and management of contextual information through the Model Context Protocol (MCP). Built with C# and .NET 9.0, it leverages SQLite with sqlite-vec for vector operations and FTS5 for full-text search, enhanced with a robust Database Session Pattern for reliable connection management.

**ARCHITECTURE ENHANCEMENT**: This implementation features a Database Session Pattern that ensures reliable SQLite connection lifecycle management, eliminates file locking issues, provides proper resource cleanup, and enables robust test isolation.

## Key Features

### Core Capabilities
- **Memory Storage**: Intelligent storage of conversation context and facts
- **Semantic Search**: Vector-based similarity search for relevant memory retrieval
- **Session Isolation**: Multi-tenant memory spaces with strict data separation
- **Integer IDs**: LLM-friendly integer identifiers instead of UUIDs
- **Session Defaults**: HTTP header and initialization-based default context

### Advanced Features
- **Fact Extraction**: LLM-powered extraction of structured information from conversations
- **Memory Decision Engine**: Intelligent conflict resolution and memory updates
- **Vector Storage**: High-performance semantic similarity search using sqlite-vec
- **Full-Text Search**: Advanced text search capabilities with FTS5

## Architecture Overview

### Database Session Pattern
```
┌─────────────────────────────────────────────────────────────┐
│                Database Session Pattern                     │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ISqliteSession│  │ISqliteSession│  │   Session          │  │
│  │ Interface   │  │  Factory    │  │  Implementations    │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Production Implementation                     │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ SqliteSession│  │SqliteSession│  │   Connection        │  │
│  │             │  │  Factory    │  │   Lifecycle         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Test Implementation                          │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │TestSqlite   │  │TestSqlite   │  │   Test Database     │  │
│  │ Session     │  │SessionFactory│  │   Isolation         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### System Components
```
┌─────────────────────────────────────────────────────────────┐
│                    Memory MCP Server                        │
├─────────────────────────────────────────────────────────────┤
│                    MCP Protocol Layer                       │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Memory    │  │   Search    │  │   Session           │  │
│  │   Tools     │  │   Tools     │  │  Management         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Intelligence Layer                           │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │    Fact     │  │   Memory    │  │      LLM            │  │
│  │ Extraction  │  │  Decision   │  │   Providers         │  │
│  │   Engine    │  │   Engine    │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Storage Layer (Enhanced)                     │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Database   │  │   SQLite    │  │    Embedding        │  │
│  │  Session    │  │  Storage    │  │    Manager          │  │
│  │  Pattern    │  │ (sqlite-vec)│  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Prerequisites

- .NET 9.0 SDK
- SQLite with sqlite-vec extension support
- OpenAI or Anthropic API key

### Installation

1. **Clone the repository**
   ```bash
   git clone <repository-url>
   cd LmDotnetTools/McpServers/Memory
   ```

2. **Install dependencies**
   ```bash
   dotnet restore
   ```

3. **Configure environment**
   ```bash
   # Copy example configuration
   cp .env.example .env
   
   # Edit configuration with your API keys
   # OPENAI_API_KEY=your_openai_key
   # ANTHROPIC_API_KEY=your_anthropic_key
   ```

4. **Initialize database**
   ```bash
   dotnet run -- --init-db
   ```

5. **Run the server**
   ```bash
   dotnet run
   ```

## Configuration

### Basic Configuration
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=memory.db;Mode=ReadWriteCreate;"
  },
  "LlmProviders": {
    "OpenAI": {
      "ApiKey": "your_openai_key",
      "Model": "gpt-4"
    },
    "Anthropic": {
      "ApiKey": "your_anthropic_key",
      "Model": "claude-3-sonnet-20240229"
    }
  },
  "SessionFactory": {
    "EnableWalMode": true,
    "CacheSize": 10000,
    "ConnectionTimeoutSeconds": 30,
    "EnableConnectionLeakDetection": true
  }
}
```

### Database Session Configuration
```json
{
  "SessionFactory": {
    "ConnectionString": "Data Source=memory.db;Mode=ReadWriteCreate;",
    "EnableWalMode": true,
    "CacheSize": 10000,
    "MmapSize": 268435456,
    "ConnectionTimeoutSeconds": 30,
    "EnableForeignKeys": true,
    "MaxRetryAttempts": 3,
    "RetryBaseDelayMs": 100,
    "EnableConnectionLeakDetection": true,
    "LeakDetectionIntervalMinutes": 1
  }
}
```

## MCP Tools

### Memory Management Tools

#### memory_add
Adds new memories from conversation messages or direct content.

**Parameters**:
- `messages` (required): Array of conversation messages or string content
- `user_id` (optional): User identifier for session isolation
- `agent_id` (optional): Agent identifier for session isolation
- `run_id` (optional): Run identifier for session isolation
- `metadata` (optional): Additional metadata to attach to memories
- `mode` (optional): Processing mode ("inference" or "direct", default: "inference")

**Returns**: Array of created memory objects with integer IDs

#### memory_search
Searches for relevant memories using semantic similarity and full-text search.

**Parameters**:
- `query` (required): Search query text
- `user_id` (optional): User identifier for session filtering
- `agent_id` (optional): Agent identifier for session filtering
- `run_id` (optional): Run identifier for session filtering
- `limit` (optional): Maximum number of results (default: 100, max: 100)
- `search_type` (optional): Search type ("vector", "text", "hybrid", default: "hybrid")

**Returns**: Array of relevant memory objects with relevance scores

#### memory_get_all
Retrieves all memories for a specific session.

**Parameters**:
- `user_id` (optional): User identifier for session filtering
- `agent_id` (optional): Agent identifier for session filtering
- `run_id` (optional): Run identifier for session filtering
- `limit` (optional): Maximum number of results (default: 100, max: 100)
- `offset` (optional): Pagination offset (default: 0)

**Returns**: Array of all memory objects in the session

#### memory_update
Updates existing memory content with intelligent merging.

**Parameters**:
- `memory_id` (required): Integer ID of the memory to update
- `data` (required): New content or update instructions
- `user_id` (optional): User identifier for session validation
- `agent_id` (optional): Agent identifier for session validation
- `run_id` (optional): Run identifier for session validation

**Returns**: Updated memory object with new content and metadata

#### memory_delete
Removes specific memories from the system.

**Parameters**:
- `memory_id` (required): Integer ID of the memory to delete
- `user_id` (optional): User identifier for session validation
- `agent_id` (optional): Agent identifier for session validation
- `run_id` (optional): Run identifier for session validation

**Returns**: Deletion confirmation with memory details

#### memory_delete_all
Removes all memories for a specific session.

**Parameters**:
- `user_id` (optional): User identifier for session targeting
- `agent_id` (optional): Agent identifier for session targeting
- `run_id` (optional): Run identifier for session targeting
- `confirm` (required): Confirmation flag to prevent accidental deletion

**Returns**: Deletion summary with count of removed memories

### Session Management Tools

#### memory_init_session
Establishes default session context for subsequent operations.

**Parameters**:
- `user_id` (optional): Default user identifier for the session
- `agent_id` (optional): Default agent identifier for the session
- `run_id` (optional): Default run identifier for the session
- `metadata` (optional): Additional session metadata

**Returns**: Session configuration confirmation and active defaults summary

### Analytics Tools

#### memory_get_history
Retrieves memory operation history for debugging and analysis.

**Parameters**:
- `user_id` (optional): User identifier for session filtering
- `agent_id` (optional): Agent identifier for session filtering
- `run_id` (optional): Run identifier for session filtering
- `limit` (optional): Maximum number of history entries (default: 50, max: 100)

**Returns**: Array of memory operations with timestamps and details

#### memory_get_stats
Provides memory usage statistics and analytics.

**Parameters**:
- `user_id` (optional): User identifier for session filtering
- `agent_id` (optional): Agent identifier for session filtering
- `run_id` (optional): Run identifier for session filtering

**Returns**: Memory count statistics, storage usage, and performance metrics

## Session Management

### HTTP Headers
The server supports session defaults via HTTP headers:
- `X-Memory-User-ID`: Default user identifier
- `X-Memory-Agent-ID`: Default agent identifier
- `X-Memory-Run-ID`: Default run identifier
- `X-Memory-Session-Metadata`: JSON object with additional metadata

### Session Precedence
1. **Explicit Parameters**: Parameters provided directly in tool calls
2. **HTTP Headers**: Default session context from headers
3. **Session Initialization**: Context set via `memory_init_session`
4. **System Defaults**: Fallback defaults configured at server level

## Database Session Pattern

### Benefits
- **Reliability**: Eliminates SQLite file locking issues and connection leaks
- **Test Isolation**: Complete separation between test runs with automatic cleanup
- **Resource Management**: Guaranteed connection disposal and WAL checkpoint handling
- **Error Recovery**: Proper transaction rollback and error handling
- **Performance**: Optimized connection usage and monitoring

### Usage Example
```csharp
// Service registration
services.AddSingleton<ISqliteSessionFactory, SqliteSessionFactory>();

// Repository usage
public class MemoryRepository
{
    private readonly ISqliteSessionFactory _sessionFactory;
    
    public async Task<int> AddMemoryAsync(Memory memory)
    {
        using var session = await _sessionFactory.CreateSessionAsync();
        
        return await session.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            // Database operations with automatic cleanup
            // ...
            return memoryId;
        });
    }
}
```

## Testing

### Unit Tests
```bash
dotnet test tests/MemoryServer.Tests
```

### Integration Tests
```bash
dotnet test tests/McpIntegrationTests
```

### Test Isolation
The Database Session Pattern ensures complete test isolation:
- Each test gets a unique database file
- Automatic cleanup after test completion
- No interference between parallel tests
- Deterministic test results

## Performance

### Benchmarks
- Memory addition: <1000ms
- Memory search: <500ms
- Session creation: <100ms
- Session disposal: <500ms (including WAL checkpoint)

### Optimization
- Connection pooling for production environments
- Embedding caching with LRU eviction
- Batch operations for high-volume scenarios
- SQLite performance tuning with optimal PRAGMA settings

## Monitoring

### Health Checks
- Database connection health
- Session factory metrics
- Connection leak detection
- Performance monitoring

### Metrics
- Operation latency and throughput
- Error rates and types
- Resource utilization
- Cache hit rates

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes with proper tests
4. Ensure all tests pass with session pattern validation
5. Submit a pull request

## License

[License information]

---

This documentation provides comprehensive guidance for implementing and using the Memory MCP Server with the enhanced Database Session Pattern architecture, ensuring reliable resource management and robust production deployment. 