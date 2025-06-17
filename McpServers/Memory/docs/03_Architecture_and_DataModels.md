# Architecture & Data Models

This consolidated document merges the following original files:

- DeepDesignDoc.md  
- DataModels.md  
- UnifiedSearchDesign.md

---

## Deep Design Document

<details>
<summary>Full Deep Design Document</summary>

```markdown
<!-- Begin DeepDesignDoc.md content -->
# Memory System - Deep Design Document

## Executive Summary

This document presents a comprehensive design for a sophisticated memory management system inspired by mem0, with a focus on simplicity through strategic provider selection. The system supports OpenAI and Anthropic as LLM providers and SQLite with the sqlite-vec extension as the storage solution, providing production-ready architecture with reduced complexity while maintaining advanced memory intelligence capabilities.

**ARCHITECTURE UPDATE**: This design has been enhanced with a Database Session Pattern to address SQLite connection management challenges, ensuring reliable resource cleanup, proper test isolation, and robust production deployment.

## System Architecture Overview

### High-Level Architecture (Enhanced with LLM Integration)

```txt
┌─────────────────────────────────────────────────────────────┐
│              Memory System (LLM-Integrated)                 │
├─────────────────────────────────────────────────────────────┤
│                    Public API Layer                         │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Memory    │  │   Hybrid    │  │   Session           │  │
│  │   Service   │  │   Search    │  │  Management         │  │
│  │ (Enhanced)  │  │  Service    │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│            LLM Integration Layer (NEW)                      │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   LmConfig  │  │ LmEmbeddings│  │   Intelligence      │  │
│  │Integration  │  │ Integration │  │     Engine          │  │
│  │   Service   │  │   Service   │  │ (Fact/Decision)     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│              Storage Layer (Enhanced)                       │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Database   │  │   Vector    │  │    SQLite           │  │
│  │  Session    │  │ Embeddings  │  │   Database          │  │
│  │  Pattern    │  │   Storage   │  │  (FTS5 + Vec)       │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Provider Integration Layer                   │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   OpenAI    │  │  Anthropic  │  │    LmCore           │  │
│  │ Provider    │  │  Provider   │  │   Foundation        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### Core Design Principles

**Simplicity Through Focus**: Rather than supporting 15+ providers like the original mem0, we focus on proven, reliable solutions that cover the majority of use cases while maintaining sophisticated functionality.

**Production-First Architecture**: Every component is designed with production deployment in mind, including comprehensive error handling, monitoring, performance optimization, and scalability considerations.

**Reliable Resource Management**: The new Database Session Pattern ensures proper SQLite connection lifecycle management, eliminating file locking issues and providing robust test isolation.

**Intelligent Memory Management**: Advanced fact extraction and decision-making capabilities that understand context, resolve conflicts, and maintain consistency across the memory system.

**Modular Design**: Clean separation of concerns with well-defined interfaces, enabling independent testing, development, and potential future extensions.

**Type Safety and Modern Patterns**: Full type annotations, dependency injection, factory patterns, and async-first design throughout the system.

## Data Flow Architecture

### Memory Addition Flow

```txt
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   User      │    │   Memory    │    │    Fact     │
│  Message    │───>│    Core     │───>│ Extraction  │
└─────────────┘    └─────────────┘    └─────────────┘
                           │                   │
                           ▼                   ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Vector    │<───│   Memory    │<───│   Memory    │
│  Storage    │    │   Update    │    │  Decision   │
└─────────────┘    └─────────────┘    └─────────────┘
```

**Flow Description**:
1. **Message Input**: User provides conversation messages or direct content
2. **Session Context**: Memory Core extracts and validates session identifiers
3. **Processing Mode Selection**: Choose between inference mode (AI-powered) or direct mode
4. **Fact Extraction**: LLM analyzes messages and extracts structured facts
5. **Memory Decision**: AI determines what operations to perform (ADD/UPDATE/DELETE)
6. **Vector Operations**: Generate embeddings and store/update in vector database
7. **History Tracking**: Log all operations for audit trail and debugging

### Memory Search Flow

```txt
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   Search    │    │   Memory    │    │  Embedding  │
│   Query     │───▶│    Core     │───▶│ Generation  │
└─────────────┘    └─────────────┘    └─────────────┘
                           │                   │
                           ▼                   ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│  Formatted  │◀───│   Result    │◀───│   Vector    │
│  Results    │    │ Processing  │    │   Search    │
└─────────────┘    └─────────────┘    └─────────────┘
```

**Flow Description**:
1. **Query Processing**: Convert search query to embeddings
2. **Filter Construction**: Build session-based and metadata filters
3. **Vector Search**: Execute similarity search with scoring
4. **Result Processing**: Format results with metadata and relevance scores
5. **Access Control**: Ensure session isolation and security

## LLM Libraries Integration

### LmConfig Integration

The Memory server now integrates with **LmConfig** for centralized model configuration management, replacing hard-coded provider selection with intelligent, capability-based model resolution.

**Key Benefits**:
- **Capability-Based Selection**: Automatically select optimal models based on required capabilities (chat, embedding, reasoning)
- **Cost Optimization**: Choose models based on cost constraints and performance requirements  
- **Dynamic Provider Management**: Runtime provider switching without configuration changes
- **Centralized Configuration**: Single source of truth for all model configurations

**Integration Components**:
- `ILmConfigService`: Centralized service for model management and provider creation
- Enhanced configuration structure supporting model metadata and cost constraints
- Automatic fallback strategies for provider failures

### LmEmbeddings Integration

The Memory server now leverages **LmEmbeddings** for semantic vector search capabilities, transforming it from a text-only search system into a sophisticated semantic memory platform.

**Key Benefits**:
- **Semantic Search**: Find relevant memories regardless of exact wording
- **Provider Abstraction**: Support multiple embedding providers through unified interface
- **Performance Optimization**: Built-in caching, batching, and reranking capabilities
- **Standardized API**: Consistent embedding operations across the ecosystem

**Integration Components**:
- `IMemoryEmbeddingService`: Memory-specific embedding operations with caching
- Enhanced database schema with vector storage and indexing
- Hybrid search combining text (FTS5) and semantic (vector) search
- Configurable search modes and result fusion strategies

### Enhanced Database Schema

```sql
-- New: Memory embeddings table
CREATE TABLE memory_embeddings (
    memory_id INTEGER PRIMARY KEY,
    embedding BLOB NOT NULL,
    embedding_model TEXT NOT NULL,
    embedding_dimension INTEGER NOT NULL,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,
    FOREIGN KEY (memory_id) REFERENCES memories (id) ON DELETE CASCADE
);

-- New: Embedding cache for performance optimization
CREATE TABLE embedding_cache (
    content_hash TEXT PRIMARY KEY,
    embedding BLOB NOT NULL,
    embedding_model TEXT NOT NULL,
    embedding_dimension INTEGER NOT NULL,
    access_count INTEGER NOT NULL DEFAULT 1,
    created_at DATETIME NOT NULL,
    last_accessed DATETIME NOT NULL
);

-- Optimized indexes for vector operations
CREATE INDEX idx_memory_embeddings_model ON memory_embeddings (embedding_model);
CREATE INDEX idx_embedding_cache_model ON embedding_cache (embedding_model);
CREATE INDEX idx_embedding_cache_accessed ON embedding_cache (last_accessed);
```

### Hybrid Search Architecture

The integration introduces sophisticated hybrid search capabilities that combine the strengths of both text-based and semantic search:

**Search Modes**:
- **Text Search**: Traditional FTS5 full-text search for exact matches
- **Semantic Search**: Vector similarity search for conceptual matches  
- **Hybrid Search**: Intelligent fusion of both approaches with configurable weights

**Search Flow**:
1. **Query Processing**: Convert search query to both text tokens and embedding vector
2. **Parallel Execution**: Run FTS5 and vector search concurrently
3. **Result Fusion**: Combine and rank results based on configurable weights
4. **Session Filtering**: Apply session context for proper isolation
5. **Response Formatting**: Return unified results with relevance scores

## Component Deep Dive

### 1. Memory Core Layer (Enhanced)

**Memory Class (Synchronous)**:
- Primary orchestration layer for all memory operations
- Session management and isolation enforcement
- Component coordination and error handling
- Support for both inference and direct processing modes
<!-- End DeepDesignDoc.md content -->

</details>  

---


# Data Models and Schemas - Enhanced with Database Session Pattern

## Overview

This document defines all data structures, schemas, and models used throughout the memory system. These specifications ensure consistency across components and provide clear contracts for implementation in any programming language.

**ARCHITECTURE ENHANCEMENT**: This document has been updated to include the Database Session Pattern interfaces and models that provide reliable SQLite connection management, ensuring proper resource cleanup, test isolation, and robust production deployment.

## Database Session Pattern Models

### Core Session Interfaces

```csharp
/// <summary>
/// Represents a database session that encapsulates SQLite connection lifecycle management
/// </summary>
public interface ISqliteSession : IAsyncDisposable
{
    /// <summary>
    /// Executes an operation with the session's connection
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">Operation to execute with the connection</param>
    /// <returns>Result of the operation</returns>
    Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation);
    
    /// <summary>
    /// Executes an operation within a transaction
    /// </summary>
    /// <typeparam name="T">Return type of the operation</typeparam>
    /// <param name="operation">Operation to execute with connection and transaction</param>
    /// <returns>Result of the operation</returns>
    Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> operation);
    
    /// <summary>
    /// Executes an operation with the session's connection (void return)
    /// </summary>
    /// <param name="operation">Operation to execute with the connection</param>
    Task ExecuteAsync(Func<SqliteConnection, Task> operation);
    
    /// <summary>
    /// Executes an operation within a transaction (void return)
    /// </summary>
    /// <param name="operation">Operation to execute with connection and transaction</param>
    Task ExecuteInTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task> operation);
    
    /// <summary>
    /// Gets session health information
    /// </summary>
    /// <returns>Session health status</returns>
    Task<SessionHealthStatus> GetHealthAsync();
}

/// <summary>
/// Factory for creating database sessions
/// </summary>
public interface ISqliteSessionFactory
{
    /// <summary>
    /// Creates a new database session with default connection string
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>New database session</returns>
    Task<ISqliteSession> CreateSessionAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new database session with specific connection string
    /// </summary>
    /// <param name="connectionString">SQLite connection string</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>New database session</returns>
    Task<ISqliteSession> CreateSessionAsync(string connectionString, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Initializes the database schema
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets factory performance metrics
    /// </summary>
    /// <returns>Performance metrics</returns>
    Task<SessionPerformanceMetrics> GetMetricsAsync();
}
```

### Session Configuration Models

```csharp
/// <summary>
/// Configuration for database session factory
/// </summary>
public class SessionFactoryConfiguration
{
    /// <summary>
    /// Default SQLite connection string
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether to enable WAL mode
    /// </summary>
    public bool EnableWalMode { get; set; } = true;
    
    /// <summary>
    /// SQLite cache size in pages
    /// </summary>
    public int CacheSize { get; set; } = 10000;
    
    /// <summary>
    /// Memory-mapped I/O size in bytes
    /// </summary>
    public long MmapSize { get; set; } = 268435456; // 256MB
    
    /// <summary>
    /// Connection timeout in seconds
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    
    /// <summary>
    /// Whether to enable foreign key constraints
    /// </summary>
    public bool EnableForeignKeys { get; set; } = true;
    
    /// <summary>
    /// Maximum number of retry attempts for failed operations
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
    
    /// <summary>
    /// Base delay for exponential backoff in milliseconds
    /// </summary>
    public int RetryBaseDelayMs { get; set; } = 100;
    
    /// <summary>
    /// Whether to enable connection leak detection
    /// </summary>
    public bool EnableConnectionLeakDetection { get; set; } = true;
    
    /// <summary>
    /// Connection leak detection interval in minutes
    /// </summary>
    public int LeakDetectionIntervalMinutes { get; set; } = 1;
}

/// <summary>
/// Test-specific session configuration
/// </summary>
public class TestSessionConfiguration : SessionFactoryConfiguration
{
    /// <summary>
    /// Directory for test database files
    /// </summary>
    public string TestDatabaseDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "MemoryMcpTests");
    
    /// <summary>
    /// Whether to clean up database files after test completion
    /// </summary>
    public bool AutoCleanup { get; set; } = true;
    
    /// <summary>
    /// Maximum time to wait for cleanup completion
    /// </summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(5);
    
    /// <summary>
    /// Whether to use unique database files per test
    /// </summary>
    public bool UseUniqueDbPerTest { get; set; } = true;
    
    /// <summary>
    /// Whether to enable detailed test logging
    /// </summary>
    public bool EnableDetailedTestLogging { get; set; } = true;
}
```

### Session Health and Performance Models

```csharp
/// <summary>
/// Database session health status
/// </summary>
public class SessionHealthStatus
{
    /// <summary>
    /// Whether the session is healthy
    /// </summary>
    public bool IsHealthy { get; set; }
    
    /// <summary>
    /// Connection state
    /// </summary>
    public ConnectionState ConnectionState { get; set; }
    
    /// <summary>
    /// Last successful operation timestamp
    /// </summary>
    public DateTime LastSuccessfulOperation { get; set; }
    
    /// <summary>
    /// Number of operations performed in this session
    /// </summary>
    public int OperationCount { get; set; }
    
    /// <summary>
    /// Session creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Any health issues detected
    /// </summary>
    public List<string> HealthIssues { get; set; } = new();
}

/// <summary>
/// Database session performance metrics
/// </summary>
public class SessionPerformanceMetrics
{
    /// <summary>
    /// Number of active database sessions
    /// </summary>
    public int ActiveSessions { get; set; }
    
    /// <summary>
    /// Total sessions created
    /// </summary>
    public long TotalSessionsCreated { get; set; }
    
    /// <summary>
    /// Total sessions disposed
    /// </summary>
    public long TotalSessionsDisposed { get; set; }
    
    /// <summary>
    /// Average session creation time in milliseconds
    /// </summary>
    public double AverageSessionCreationTimeMs { get; set; }
    
    /// <summary>
    /// Average session disposal time in milliseconds
    /// </summary>
    public double AverageSessionDisposalTimeMs { get; set; }
    
    /// <summary>
    /// Number of connection leaks detected
    /// </summary>
    public int ConnectionLeaksDetected { get; set; }
    
    /// <summary>
    /// WAL checkpoint frequency per hour
    /// </summary>
    public double WalCheckpointFrequency { get; set; }
    
    /// <summary>
    /// Average operation execution time in milliseconds
    /// </summary>
    public double AverageOperationTimeMs { get; set; }
    
    /// <summary>
    /// Number of failed operations
    /// </summary>
    public long FailedOperations { get; set; }
    
    /// <summary>
    /// Number of retried operations
    /// </summary>
    public long RetriedOperations { get; set; }
    
    /// <summary>
    /// Metrics collection timestamp
    /// </summary>
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Connection leak detection result
/// </summary>
public class ConnectionLeakInfo
{
    /// <summary>
    /// Session identifier
    /// </summary>
    public string SessionId { get; set; } = string.Empty;
    
    /// <summary>
    /// When the session was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// How long the session has been active
    /// </summary>
    public TimeSpan ActiveDuration { get; set; }
    
    /// <summary>
    /// Last operation performed
    /// </summary>
    public string LastOperation { get; set; } = string.Empty;
    
    /// <summary>
    /// Stack trace of session creation (for debugging)
    /// </summary>
    public string CreationStackTrace { get; set; } = string.Empty;
}
```

### Session-Enhanced Memory Context

```csharp
/// <summary>
/// Enhanced memory context with session defaults support
/// </summary>
public class MemoryContext
{
    /// <summary>
    /// User identifier for session isolation
    /// </summary>
    public string? UserId { get; set; }
    
    /// <summary>
    /// Agent identifier for session isolation
    /// </summary>
    public string? AgentId { get; set; }
    
    /// <summary>
    /// Run identifier for session isolation
    /// </summary>
    public string? RunId { get; set; }
    
    /// <summary>
    /// Additional metadata for the memory context
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// Whether this context was derived from session defaults
    /// </summary>
    public bool IsFromSessionDefaults { get; set; }
    
    /// <summary>
    /// Source of the context (header, initialization, explicit)
    /// </summary>
    public ContextSource Source { get; set; } = ContextSource.Explicit;
    
    /// <summary>
    /// Timestamp when context was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Database session identifier for tracking
    /// </summary>
    public string? SessionId { get; set; }
}

/// <summary>
/// Source of memory context information
/// </summary>
public enum ContextSource
{
    /// <summary>
    /// Explicitly provided in the request
    /// </summary>
    Explicit,
    
    /// <summary>
    /// Derived from HTTP headers
    /// </summary>
    HttpHeaders,
    
    /// <summary>
    /// Set during session initialization
    /// </summary>
    SessionInitialization,
    
    /// <summary>
    /// System default values
    /// </summary>
    SystemDefaults
}
```

## Core Data Models

### 1. Memory Record (Enhanced for Session Pattern)

**Purpose**: Represents a stored memory with all associated metadata and content, enhanced with integer IDs for better LLM integration and session-scoped operations.

**Schema**:
```json
{
  "id": "integer (auto-incrementing)",
  "content": "string",
  "embedding": "array<float>",
  "metadata": {
    "user_id": "string (required for session isolation)",
    "agent_id": "string (optional)",
    "run_id": "string (optional)",
    "created_at": "string (ISO 8601)",
    "updated_at": "string (ISO 8601)",
    "memory_type": "string (enum: STANDARD, PROCEDURAL, GRAPH)",
    "category": "string (optional)",
    "tags": "array<string> (optional)",
    "custom_metadata": "object (optional)"
  },
  "score": "float (optional, for search results)",
  "version": "integer"
}
```

**Example**:
```json
{
  "id": 12345,
  "content": "User prefers Italian cuisine, especially pasta dishes",
  "embedding": [0.1, -0.2, 0.3, ...],
  "metadata": {
    "user_id": "user_123",
    "agent_id": "assistant_001",
    "run_id": "conversation_456",
    "created_at": "2024-01-15T10:30:00Z",
    "updated_at": "2024-01-15T10:30:00Z",
    "memory_type": "STANDARD",
    "category": "preferences",
    "tags": ["food", "cuisine", "italian"],
    "custom_metadata": {
      "confidence": 0.95,
      "source": "conversation"
    }
  },
  "version": 1
}
```

### 2. Session Context (Enhanced with Database Session Support)

**Purpose**: Defines the session scope for memory operations and access control, with support for database session management.

**Schema**:
```json
{
  "user_id": "string (required for session isolation)",
  "agent_id": "string (optional)",
  "run_id": "string (optional)",
  "session_type": "string (enum: USER, AGENT, RUN)",
  "permissions": {
    "read": "boolean",
    "write": "boolean",
    "delete": "boolean"
  },
  "metadata": "object (optional)",
  "session_defaults": {
    "connection_id": "string",
    "created_at": "string (ISO 8601)"
  }
}
```

**Example**:
```json
{
  "user_id": "user_123",
  "agent_id": "assistant_001",
  "run_id": "conversation_456",
  "session_type": "RUN",
  "permissions": {
    "read": true,
    "write": true,
    "delete": false
  },
  "metadata": {
    "conversation_topic": "travel_planning",
    "language": "en"
  },
  "session_defaults": {
    "connection_id": "mcp_conn_abc123",
    "created_at": "2024-01-15T10:00:00Z"
  }
}
```

### 3. Message Format

**Purpose**: Standardized format for conversation messages across all providers.

**Schema**:
```json
{
  "role": "string (enum: system, user, assistant)",
  "content": "string | array<content_block>",
  "name": "string (optional)",
  "timestamp": "string (ISO 8601, optional)",
  "metadata": "object (optional)"
}
```

**Content Block Schema** (for multimodal messages):
```json
{
  "type": "string (enum: text, image)",
  "text": "string (for text blocks)",
  "image_url": {
    "url": "string",
    "detail": "string (enum: low, high, auto)"
  }
}
```

**Examples**:

**Simple Text Message**:
```json
{
  "role": "user",
  "content": "I love Italian food, especially pasta",
  "timestamp": "2024-01-15T10:30:00Z"
}
```

**Multimodal Message**:
```json
{
  "role": "user",
  "content": [
    {
      "type": "text",
      "text": "What do you think of this restaurant?"
    },
    {
      "type": "image",
      "image_url": {
        "url": "https://example.com/restaurant.jpg",
        "detail": "high"
      }
    }
  ],
  "timestamp": "2024-01-15T10:30:00Z"
}
```

### 4. Extracted Facts

**Purpose**: Structured facts extracted from conversations by the fact extraction engine.

**Schema**:
```json
{
  "facts": "array<string>",
  "extraction_metadata": {
    "model_used": "string",
    "extraction_time": "string (ISO 8601)",
    "confidence_score": "float (0-1)",
    "language_detected": "string (optional)",
    "custom_prompt_used": "boolean"
  }
}
```

**Example**:
```json
{
  "facts": [
    "User prefers Italian cuisine",
    "User especially likes pasta dishes",
    "User is planning a trip to Italy in July 2024",
    "User works as a software engineer"
  ],
  "extraction_metadata": {
    "model_used": "gpt-4",
    "extraction_time": "2024-01-15T10:30:00Z",
    "confidence_score": 0.92,
    "language_detected": "en",
    "custom_prompt_used": false
  }
}
```

### 5. Memory Operations

**Purpose**: Defines the operations to be performed on memories as decided by the decision engine.

**Operation Schema**:
```json
{
  "id": "string (UUID, for UPDATE/DELETE operations)",
  "event": "string (enum: ADD, UPDATE, DELETE, NONE)",
  "text": "string (memory content)",
  "old_memory": "string (optional, for UPDATE operations)",
  "metadata": "object (optional)",
  "confidence": "float (0-1, optional)",
  "reasoning": "string (optional)"
}
```

**Operations List Schema**:
```json
{
  "memory": "array<operation>",
  "processing_metadata": {
    "total_operations": "integer",
    "decision_time": "string (ISO 8601)",
    "model_used": "string",
    "uuid_mapping_used": "boolean"
  }
}
```

**Example**:
```json
{
  "memory": [
    {
      "id": "0",
      "event": "UPDATE",
      "text": "User loves Italian cuisine, especially pasta dishes and pizza",
      "old_memory": "User likes Italian food",
      "confidence": 0.95,
      "reasoning": "Expanding existing preference with more specific details"
    },
    {
      "id": "1",
      "event": "ADD",
      "text": "User is planning a trip to Italy in July 2024",
      "confidence": 0.90,
      "reasoning": "New travel information not previously stored"
    },
    {
      "id": "2",
      "event": "NONE",
      "text": "User works as a software engineer",
      "confidence": 1.0,
      "reasoning": "Information already exists and is current"
    }
  ],
  "processing_metadata": {
    "total_operations": 3,
    "decision_time": "2024-01-15T10:30:00Z",
    "model_used": "gpt-4",
    "uuid_mapping_used": true
  }
}
```

### 6. Search Query and Results

**Search Query Schema**:
```json
{
  "query": "string",
  "filters": {
    "user_id": "string (optional)",
    "agent_id": "string (optional)",
    "run_id": "string (optional)",
    "memory_type": "string (optional)",
    "category": "string (optional)",
    "tags": "array<string> (optional)",
    "date_range": {
      "start": "string (ISO 8601, optional)",
      "end": "string (ISO 8601, optional)"
    },
    "custom_filters": "object (optional)"
  },
  "limit": "integer (default: 10)",
  "score_threshold": "float (default: 0.7)",
  "include_embeddings": "boolean (default: false)"
}
```

**Search Results Schema**:
```json
{
  "results": "array<memory_record>",
  "total_count": "integer",
  "search_metadata": {
    "query_time": "string (ISO 8601)",
    "processing_time_ms": "integer",
    "embedding_time_ms": "integer",
    "vector_search_time_ms": "integer",
    "filters_applied": "object"
  }
}
```

**Example**:
```json
{
  "results": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "content": "User prefers Italian cuisine, especially pasta dishes",
      "score": 0.95,
      "metadata": {
        "user_id": "user_123",
        "created_at": "2024-01-15T10:30:00Z",
        "memory_type": "STANDARD",
        "category": "preferences"
      }
    }
  ],
  "total_count": 1,
  "search_metadata": {
    "query_time": "2024-01-15T10:35:00Z",
    "processing_time_ms": 45,
    "embedding_time_ms": 20,
    "vector_search_time_ms": 15,
    "filters_applied": {
      "user_id": "user_123",
      "score_threshold": 0.7
    }
  }
}
```

## Graph Memory Data Models

### 7. Entity Schema

**Purpose**: Represents entities extracted from conversations for knowledge graph construction.

**Schema**:
```json
{
  "id": "string (UUID)",
  "name": "string",
  "type": "string (optional)",
  "aliases": "array<string> (optional)",
  "metadata": {
    "user_id": "string",
    "agent_id": "string (optional)",
    "run_id": "string (optional)",
    "created_at": "string (ISO 8601)",
    "updated_at": "string (ISO 8601)",
    "confidence": "float (0-1)",
    "source_memory_ids": "array<string>"
  }
}
```

**Example**:
```json
{
  "id": "entity_001",
  "name": "USER_ID",
  "type": "person",
  "aliases": ["user", "I", "me"],
  "metadata": {
    "user_id": "user_123",
    "agent_id": "assistant_001",
    "created_at": "2024-01-15T10:30:00Z",
    "updated_at": "2024-01-15T10:30:00Z",
    "confidence": 1.0,
    "source_memory_ids": ["550e8400-e29b-41d4-a716-446655440000"]
  }
}
```

### 8. Relationship Schema

**Purpose**: Represents relationships between entities in the knowledge graph.

**Schema**:
```json
{
  "id": "string (UUID)",
  "source": "string (entity name)",
  "relationship": "string",
  "target": "string (entity name)",
  "metadata": {
    "user_id": "string",
    "agent_id": "string (optional)",
    "run_id": "string (optional)",
    "created_at": "string (ISO 8601)",
    "updated_at": "string (ISO 8601)",
    "confidence": "float (0-1)",
    "source_memory_id": "string (optional)",
    "temporal_context": "string (optional)"
  }
}
```

**Example**:
```json
{
  "id": "rel_001",
  "source": "USER_ID",
  "relationship": "prefers",
  "target": "Italian cuisine",
  "metadata": {
    "user_id": "user_123",
    "agent_id": "assistant_001",
    "created_at": "2024-01-15T10:30:00Z",
    "updated_at": "2024-01-15T10:30:00Z",
    "confidence": 0.95,
    "source_memory_id": "550e8400-e29b-41d4-a716-446655440000",
    "temporal_context": "current"
  }
}
```
}
```

### 9. Graph Update Instructions

**Purpose**: Instructions for updating relationships in the knowledge graph.

**Schema**:
```json
{
  "updates": "array<update_instruction>",
  "metadata": {
    "processing_time": "string (ISO 8601)",
    "model_used": "string",
    "total_updates": "integer"
  }
}
```

**Update Instruction Schema**:
```json
{
  "action": "string (enum: UPDATE, ADD, DELETE)",
  "source": "string",
  "target": "string",
  "relationship": "string",
  "old_relationship": "string (optional, for UPDATE)",
  "confidence": "float (0-1, optional)",
  "reasoning": "string (optional)"
}
```

**Example**:
```json
{
  "updates": [
    {
      "action": "UPDATE",
      "source": "USER_ID",
      "target": "Italian cuisine",
      "relationship": "loves",
      "old_relationship": "likes",
      "confidence": 0.90,
      "reasoning": "Stronger preference indicated by new information"
    },
    {
      "action": "ADD",
      "source": "USER_ID",
      "target": "pasta dishes",
      "relationship": "especially_enjoys",
      "confidence": 0.85,
      "reasoning": "Specific preference mentioned in conversation"
    }
  ],
  "metadata": {
    "processing_time": "2024-01-15T10:30:00Z",
    "model_used": "gpt-4",
    "total_updates": 2
  }
}
```

## Configuration Data Models

### 10. System Configuration Schema

**Purpose**: Defines the complete system configuration structure.

**Schema**:
```json
{
  "llm": {
    "provider": "string (enum: openai, anthropic)",
    "api_key": "string",
    "model": "string",
    "temperature": "float (0-2)",
    "max_tokens": "integer",
    "timeout": "integer (seconds)",
    "max_retries": "integer",
    "custom_fact_extraction_prompt": "string (optional)",
    "custom_update_memory_prompt": "string (optional)"
  },
  "vector_store": {
    "provider": "string (enum: qdrant)",
    "host": "string",
    "port": "integer",
    "api_key": "string (optional)",
    "collection_name": "string",
    "vector_size": "integer",
    "distance_metric": "string (enum: cosine, euclidean, dot)",
    "timeout": "integer (seconds)"
  },
  "embedder": {
    "provider": "string (enum: openai, huggingface)",
    "model": "string",
    "api_key": "string (optional)",
    "dimensions": "integer",
    "batch_size": "integer"
  },
  "graph_store": {
    "enabled": "boolean",
    "provider": "string (optional)",
    "connection_string": "string (optional)"
  },
  "performance": {
    "cache_size": "integer",
    "batch_size": "integer",
    "parallel_requests": "integer",
    "enable_caching": "boolean"
  },
  "monitoring": {
    "enabled": "boolean",
    "metrics_endpoint": "string (optional)",
    "log_level": "string (enum: DEBUG, INFO, WARN, ERROR)"
  }
}
```

### 11. Provider-Specific Configurations

**OpenAI Configuration**:
```json
{
  "provider": "openai",
  "api_key": "${OPENAI_API_KEY}",
  "model": "gpt-4",
  "temperature": 0.0,
  "max_tokens": 1000,
  "timeout": 30,
  "max_retries": 3,
  "organization": "string (optional)",
  "base_url": "string (optional)"
}
```

**Anthropic Configuration**:
```json
{
  "provider": "anthropic",
  "api_key": "${ANTHROPIC_API_KEY}",
  "model": "claude-3-sonnet-20240229",
  "temperature": 0.0,
  "max_tokens": 1000,
  "timeout": 30,
  "max_retries": 3,
  "base_url": "string (optional)"
}
```

**Qdrant Configuration**:
```json
{
  "provider": "qdrant",
  "host": "localhost",
  "port": 6333,
  "api_key": "${QDRANT_API_KEY}",
  "collection_name": "mem0_memories",
  "vector_size": 1536,
  "distance_metric": "cosine",
  "timeout": 30,
  "use_https": false,
  "verify_ssl": true
}
```

## Error and Response Models

### 12. Error Response Schema

**Purpose**: Standardized error response format across all operations.

**Schema**:
```json
{
  "error": {
    "code": "string",
    "message": "string",
    "details": "object (optional)",
    "timestamp": "string (ISO 8601)",
    "request_id": "string (optional)"
  }
}
```

**Error Codes**:
- `INVALID_SESSION`: Session context is invalid or missing
- `MEMORY_NOT_FOUND`: Requested memory does not exist
- `PERMISSION_DENIED`: Insufficient permissions for operation
- `PROVIDER_ERROR`: External provider (LLM, vector store) error
- `VALIDATION_ERROR`: Input validation failed
- `RATE_LIMIT_EXCEEDED`: Rate limit exceeded
- `INTERNAL_ERROR`: Unexpected system error

**Example**:
```json
{
  "error": {
    "code": "MEMORY_NOT_FOUND",
    "message": "Memory with ID '550e8400-e29b-41d4-a716-446655440000' not found",
    "details": {
      "memory_id": "550e8400-e29b-41d4-a716-446655440000",
      "user_id": "user_123"
    },
    "timestamp": "2024-01-15T10:30:00Z",
    "request_id": "req_123456"
  }
}
```

### 13. Success Response Schema

**Purpose**: Standardized success response format for operations.

**Schema**:
```json
{
  "success": true,
  "data": "object (operation-specific)",
  "metadata": {
    "timestamp": "string (ISO 8601)",
    "processing_time_ms": "integer",
    "request_id": "string (optional)"
  }
}
```

**Example**:
```json
{
  "success": true,
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "event": "ADD",
    "content": "User prefers Italian cuisine"
  },
  "metadata": {
    "timestamp": "2024-01-15T10:30:00Z",
    "processing_time_ms": 150,
    "request_id": "req_123456"
  }
}
```

## Validation Rules

### 14. Data Validation Specifications

**Memory Content Validation**:
- Minimum length: 1 character
- Maximum length: 10,000 characters
- Must not contain only whitespace
- Must be valid UTF-8 encoding

**Session Context Validation**:
- `user_id`: Required, alphanumeric + underscore, 1-100 characters
- `agent_id`: Optional, alphanumeric + underscore, 1-100 characters
- `run_id`: Optional, alphanumeric + underscore + hyphen, 1-100 characters

**UUID Validation**:
- Must follow UUID v4 format
- Case-insensitive matching
- Hyphens required in standard positions

**Embedding Validation**:
- Must be array of floats
- Length must match configured vector dimensions
- All values must be finite numbers
- No NaN or infinite values allowed

**Metadata Validation**:
- Maximum nesting depth: 5 levels
- Maximum key length: 100 characters
- Maximum string value length: 1,000 characters
- Reserved keys: `user_id`, `agent_id`, `run_id`, `created_at`, `updated_at`, `memory_type`

## API Request/Response Examples

### 15. Complete API Examples

**Add Memory Request**:
```json
{
  "messages": [
    {
      "role": "user",
      "content": "I love Italian food, especially pasta dishes"
    }
  ],
  "user_id": "user_123",
  "agent_id": "assistant_001",
  "run_id": "conversation_456",
  "metadata": {
    "category": "preferences",
    "tags": ["food", "cuisine"]
  }
}
```

**Add Memory Response**:
```json
{
  "success": true,
  "data": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "event": "ADD",
      "memory": "User loves Italian food, especially pasta dishes"
    }
  ],
  "metadata": {
    "timestamp": "2024-01-15T10:30:00Z",
    "processing_time_ms": 250,
    "facts_extracted": 1,
    "operations_performed": 1
  }
}
```

**Search Memory Request**:
```json
{
  "query": "What food does the user like?",
  "user_id": "user_123",
  "limit": 5,
  "filters": {
    "category": "preferences"
  }
}
```

**Search Memory Response**:
```json
{
  "success": true,
  "data": {
    "results": [
      {
        "id": "550e8400-e29b-41d4-a716-446655440000",
        "content": "User loves Italian food, especially pasta dishes",
        "score": 0.95,
        "metadata": {
          "user_id": "user_123",
          "created_at": "2024-01-15T10:30:00Z",
          "category": "preferences"
        }
      }
    ],
    "total_count": 1
  },
  "metadata": {
    "timestamp": "2024-01-15T10:35:00Z",
    "processing_time_ms": 45,
    "search_metadata": {
      "embedding_time_ms": 20,
      "vector_search_time_ms": 15
    }
  }
}
```

# Memory System Data Models - Enhanced with Database Session Pattern

## Overview

This document defines the comprehensive data models for the Memory MCP Server, including core memory entities, session management, graph structures, and the new Database Session Pattern interfaces. All models are designed to work seamlessly with SQLite storage and provide type-safe operations throughout the system.

**ARCHITECTURE ENHANCEMENT**: This document has been updated to include the Database Session Pattern interfaces and models that provide reliable SQLite connection management, ensuring proper resource cleanup, test isolation, and robust production deployment.

## Core Memory Models

### Memory Entity

```csharp
/// <summary>
/// Core memory entity representing stored information
/// </summary>
public class Memory
{
    /// <summary>
    /// Unique integer identifier for the memory
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// The actual content/text of the memory
    /// </summary>
    public string Content { get; set; } = string.Empty;
    
    /// <summary>
    /// User identifier for session isolation
    /// </summary>
    public string? UserId { get; set; }
    
    /// <summary>
    /// Agent identifier for session isolation
    /// </summary>
    public string? AgentId { get; set; }
    
    /// <summary>
    /// Run identifier for session isolation
    /// </summary>
    public string? RunId { get; set; }
    
    /// <summary>
    /// Additional metadata as JSON
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// When the memory was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// When the memory was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
    
    /// <summary>
    /// Version number for optimistic concurrency
    /// </summary>
    public int Version { get; set; } = 1;
    
    /// <summary>
    /// Vector embedding for semantic search (not stored in main table)
    /// </summary>
    [JsonIgnore]
    public float[]? Embedding { get; set; }
}
```

### Memory Context

```csharp
/// <summary>
/// Context information for memory operations with session defaults support
/// </summary>
public class MemoryContext
{
    /// <summary>
    /// User identifier for session isolation
    /// </summary>
    public string? UserId { get; set; }
    
    /// <summary>
    /// Agent identifier for session isolation
    /// </summary>
    public string? AgentId { get; set; }
    
    /// <summary>
    /// Run identifier for session isolation
    /// </summary>
    public string? RunId { get; set; }
    
    /// <summary>
    /// Additional context metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// Whether this context was derived from session defaults
    /// </summary>
    public bool IsFromSessionDefaults { get; set; }
    
    /// <summary>
    /// Source of the context (header, initialization, explicit)
    /// </summary>
    public ContextSource Source { get; set; } = ContextSource.Explicit;
    
    /// <summary>
    /// Timestamp when context was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Database session identifier for tracking
    /// </summary>
    public string? SessionId { get; set; }
}

/// <summary>
/// Source of memory context information
/// </summary>
public enum ContextSource
{
    /// <summary>
    /// Explicitly provided in the request
    /// </summary>
    Explicit,
    
    /// <summary>
    /// Derived from HTTP headers
    /// </summary>
    HttpHeaders,
    
    /// <summary>
    /// Set during session initialization
    /// </summary>
    SessionInitialization,
    
    /// <summary>
    /// System default values
    /// </summary>
    SystemDefaults
}
```

### Session Management Models

```csharp
/// <summary>
/// Session defaults for MCP connections
/// </summary>
public class SessionDefaults
{
    /// <summary>
    /// MCP connection identifier
    /// </summary>
    public string ConnectionId { get; set; } = string.Empty;
    
    /// <summary>
    /// Default user identifier
    /// </summary>
    public string? UserId { get; set; }
    
    /// <summary>
    /// Default agent identifier
    /// </summary>
    public string? AgentId { get; set; }
    
    /// <summary>
    /// Default run identifier
    /// </summary>
    public string? RunId { get; set; }
    
    /// <summary>
    /// Default metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// When the session defaults were created
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// HTTP header mapping for session defaults
/// </summary>
public class SessionHeaders
{
    public const string UserIdHeader = "X-Memory-User-ID";
    public const string AgentIdHeader = "X-Memory-Agent-ID";
    public const string RunIdHeader = "X-Memory-Run-ID";
    public const string MetadataHeader = "X-Memory-Session-Metadata";
    
    /// <summary>
    /// Extracts session defaults from HTTP headers
    /// </summary>
    /// <param name="headers">HTTP headers collection</param>
    /// <returns>Session defaults extracted from headers</returns>
    public static SessionDefaults FromHeaders(IHeaderDictionary headers)
    {
        var defaults = new SessionDefaults();
        
        if (headers.TryGetValue(UserIdHeader, out var userId))
        {
            defaults.UserId = userId.FirstOrDefault();
        }
        
        if (headers.TryGetValue(AgentIdHeader, out var agentId))
        {
            defaults.AgentId = agentId.FirstOrDefault();
        }
        
        if (headers.TryGetValue(RunIdHeader, out var runId))
        {
            defaults.RunId = runId.FirstOrDefault();
        }
        
        if (headers.TryGetValue(MetadataHeader, out var metadata))
        {
            try
            {
                var metadataJson = metadata.FirstOrDefault();
                if (!string.IsNullOrEmpty(metadataJson))
                {
                    defaults.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) 
                        ?? new Dictionary<string, object>();
                }
            }
            catch (JsonException)
            {
                // Invalid JSON in metadata header, use empty dictionary
                defaults.Metadata = new Dictionary<string, object>();
            }
        }
        
        return defaults;
    }
}
```

## Graph Database Models

### Entity Model

```csharp
/// <summary>
/// Graph entity representing a person, place, concept, etc.
/// </summary>
public class Entity
{
    /// <summary>
    /// Unique integer identifier
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Primary name of the entity
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Type/category of the entity (Person, Place, Organization, etc.)
    /// </summary>
    public string? Type { get; set; }
    
    /// <summary>
    /// Alternative names or aliases
    /// </summary>
    public List<string> Aliases { get; set; } = new();
    
    /// <summary>
    /// User identifier for session isolation
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>
    /// Agent identifier for session isolation
    /// </summary>
    public string? AgentId { get; set; }
    
    /// <summary>
    /// Run identifier for session isolation
    /// </summary>
    public string? RunId { get; set; }
    
    /// <summary>
    /// When the entity was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// When the entity was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
    
    /// <summary>
    /// Confidence score for the entity (0.0 to 1.0)
    /// </summary>
    public double Confidence { get; set; } = 1.0;
    
    /// <summary>
    /// Source memory IDs that contributed to this entity
    /// </summary>
    public List<string> SourceMemoryIds { get; set; } = new();
    
    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}
```

### Relationship Model

```csharp
/// <summary>
/// Graph relationship between entities
/// </summary>
public class Relationship
{
    /// <summary>
    /// Unique integer identifier
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Name of the source entity
    /// </summary>
    public string SourceEntityName { get; set; } = string.Empty;
    
    /// <summary>
    /// Type of relationship (works_at, lives_in, knows, etc.)
    /// </summary>
    public string RelationshipType { get; set; } = string.Empty;
    
    /// <summary>
    /// Name of the target entity
    /// </summary>
    public string TargetEntityName { get; set; } = string.Empty;
    
    /// <summary>
    /// User identifier for session isolation
    /// </summary>
    public string UserId { get; set; } = string.Empty;
    
    /// <summary>
    /// Agent identifier for session isolation
    /// </summary>
    public string? AgentId { get; set; }
    
    /// <summary>
    /// Run identifier for session isolation
    /// </summary>
    public string? RunId { get; set; }
    
    /// <summary>
    /// When the relationship was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// When the relationship was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
    
    /// <summary>
    /// Confidence score for the relationship (0.0 to 1.0)
    /// </summary>
    public double Confidence { get; set; } = 1.0;
    
    /// <summary>
    /// Source memory ID that established this relationship
    /// </summary>
    public string? SourceMemoryId { get; set; }
    
    /// <summary>
    /// Temporal context (when, duration, etc.)
    /// </summary>
    public string? TemporalContext { get; set; }
    
    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}
```

## Search and Query Models

### Search Request Models

```csharp
/// <summary>
/// Search request for memory operations
/// </summary>
public class MemorySearchRequest
{
    /// <summary>
    /// Search query text
    /// </summary>
    public string Query { get; set; } = string.Empty;
    
    /// <summary>
    /// Memory context for session filtering
    /// </summary>
    public MemoryContext? Context { get; set; }
    
    /// <summary>
    /// Search options
    /// </summary>
    public SearchOptions Options { get; set; } = new();
}

/// <summary>
/// Search options for controlling search behavior
/// </summary>
public class SearchOptions
{
    /// <summary>
    /// Maximum number of results to return
    /// </summary>
    public int Limit { get; set; } = 10;
    
    /// <summary>
    /// Offset for pagination
    /// </summary>
    public int Offset { get; set; } = 0;
    
    /// <summary>
    /// Type of search to perform
    /// </summary>
    public SearchType SearchType { get; set; } = SearchType.Hybrid;
    
    /// <summary>
    /// Whether to include metadata in results
    /// </summary>
    public bool IncludeMetadata { get; set; } = true;
    
    /// <summary>
    /// Minimum similarity threshold for vector search
    /// </summary>
    public double MinSimilarity { get; set; } = 0.0;
}

/// <summary>
/// Types of search available
/// </summary>
public enum SearchType
{
    /// <summary>
    /// Vector similarity search only
    /// </summary>
    Vector,
    
    /// <summary>
    /// Full-text search only
    /// </summary>
    Text,
    
    /// <summary>
    /// Combined vector and text search
    /// </summary>
    Hybrid
}
```

### Search Result Models

```csharp
/// <summary>
/// Result of a memory search operation
/// </summary>
public class MemorySearchResult
{
    /// <summary>
    /// Memory ID
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Memory content
    /// </summary>
    public string Content { get; set; } = string.Empty;
    
    /// <summary>
    /// Memory metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// When the memory was created
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// When the memory was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }
    
    /// <summary>
    /// Similarity score for vector search (0.0 to 1.0)
    /// </summary>
    public double Similarity { get; set; }
    
    /// <summary>
    /// Relevance score for text search
    /// </summary>
    public double Relevance { get; set; }
    
    /// <summary>
    /// Combined score for hybrid search
    /// </summary>
    public double Score { get; set; }
    
    /// <summary>
    /// Type of search that found this result
    /// </summary>
    public SearchType SearchType { get; set; }
}

/// <summary>
/// Complete search result with metadata
/// </summary>
public class SearchResult
{
    /// <summary>
    /// Whether the search was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Search results
    /// </summary>
    public List<MemorySearchResult> Results { get; set; } = new();
    
    /// <summary>
    /// Original search query
    /// </summary>
    public string Query { get; set; } = string.Empty;
    
    /// <summary>
    /// Total number of results found
    /// </summary>
    public int TotalCount { get; set; }
    
    /// <summary>
    /// Whether there are more results available
    /// </summary>
    public bool HasMore { get; set; }
    
    /// <summary>
    /// Search execution time in milliseconds
    /// </summary>
    public long ExecutionTimeMs { get; set; }
    
    /// <summary>
    /// Error message if search failed
    /// </summary>
    public string? Error { get; set; }
}
```

## Operation Result Models

### Memory Operation Results

```csharp
/// <summary>
/// Result of a memory operation (add, update, delete)
/// </summary>
public class MemoryResult
{
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// IDs of affected memories
    /// </summary>
    public List<int> MemoryIds { get; set; } = new();
    
    /// <summary>
    /// Success or error message
    /// </summary>
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Error details if operation failed
    /// </summary>
    public string? Error { get; set; }
    
    /// <summary>
    /// Operation execution time in milliseconds
    /// </summary>
    public long ExecutionTimeMs { get; set; }
    
    /// <summary>
    /// Additional operation metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Bulk operation result for multiple memories
/// </summary>
public class BulkMemoryResult
{
    /// <summary>
    /// Whether the overall operation was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Number of successfully processed memories
    /// </summary>
    public int SuccessCount { get; set; }
    
    /// <summary>
    /// Number of failed memory operations
    /// </summary>
    public int FailureCount { get; set; }
    
    /// <summary>
    /// Individual operation results
    /// </summary>
    public List<MemoryResult> Results { get; set; } = new();
    
    /// <summary>
    /// Overall operation message
    /// </summary>
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Total execution time in milliseconds
    /// </summary>
    public long TotalExecutionTimeMs { get; set; }
}
```

## Statistics and Analytics Models

```csharp
/// <summary>
/// Memory usage statistics
/// </summary>
public class MemoryStatistics
{
    /// <summary>
    /// Total number of memories
    /// </summary>
    public int TotalMemories { get; set; }
    
    /// <summary>
    /// Number of memories with embeddings
    /// </summary>
    public int EmbeddingCount { get; set; }
    
    /// <summary>
    /// Database storage size in bytes
    /// </summary>
    public long StorageSize { get; set; }
    
    /// <summary>
    /// Average memory content length
    /// </summary>
    public double AverageContentLength { get; set; }
    
    /// <summary>
    /// Most recent memory creation time
    /// </summary>
    public DateTime? LastMemoryCreated { get; set; }
    
    /// <summary>
    /// Session-specific statistics
    /// </summary>
    public Dictionary<string, int> SessionCounts { get; set; } = new();
    
    /// <summary>
    /// Database session performance metrics
    /// </summary>
    public SessionPerformanceMetrics SessionMetrics { get; set; } = new();
}

/// <summary>
/// Database session performance metrics
/// </summary>
public class SessionPerformanceMetrics
{
    /// <summary>
    /// Number of active database sessions
    /// </summary>
    public int ActiveSessions { get; set; }
    
    /// <summary>
    /// Total sessions created
    /// </summary>
    public long TotalSessionsCreated { get; set; }
    
    /// <summary>
    /// Average session creation time in milliseconds
    /// </summary>
    public double AverageSessionCreationTimeMs { get; set; }
    
    /// <summary>
    /// Average session disposal time in milliseconds
    /// </summary>
    public double AverageSessionDisposalTimeMs { get; set; }
    
    /// <summary>
    /// Number of connection leaks detected
    /// </summary>
    public int ConnectionLeaksDetected { get; set; }
    
    /// <summary>
    /// WAL checkpoint frequency per hour
    /// </summary>
    public double WalCheckpointFrequency { get; set; }
}
```

## Configuration Models

```csharp
/// <summary>
/// Complete memory system configuration
/// </summary>
public class MemoryConfiguration
{
    /// <summary>
    /// Database session factory configuration
    /// </summary>
    public SessionFactoryConfiguration SessionFactory { get; set; } = new();
    
    /// <summary>
    /// LLM provider configuration
    /// </summary>
    public LlmProviderConfiguration LlmProvider { get; set; } = new();
    
    /// <summary>
    /// Embedding provider configuration
    /// </summary>
    public EmbeddingProviderConfiguration EmbeddingProvider { get; set; } = new();
    
    /// <summary>
    /// Search configuration
    /// </summary>
    public SearchConfiguration Search { get; set; } = new();
    
    /// <summary>
    /// Session management configuration
    /// </summary>
    public SessionManagementConfiguration SessionManagement { get; set; } = new();
}

/// <summary>
/// Session management configuration
/// </summary>
public class SessionManagementConfiguration
{
    /// <summary>
    /// Whether to enable HTTP header processing for session defaults
    /// </summary>
    public bool EnableHeaderProcessing { get; set; } = true;
    
    /// <summary>
    /// Session default cache timeout in minutes
    /// </summary>
    public int SessionCacheTimeoutMinutes { get; set; } = 60;
    
    /// <summary>
    /// Maximum number of cached session defaults
    /// </summary>
    public int MaxCachedSessions { get; set; } = 1000;
    
    /// <summary>
    /// Whether to require user ID for all operations
    /// </summary>
    public bool RequireUserId { get; set; } = true;
}
```

## JSON Serialization Examples

### Memory Entity JSON

```json
{
  "id": 12345,
  "content": "John Smith works at Acme Corporation as a software engineer.",
  "user_id": "user_123",
  "agent_id": "assistant_456",
  "run_id": "conversation_789",
  "metadata": {
    "source": "conversation",
    "confidence": 0.95,
    "extracted_facts": ["employment", "job_title"]
  },
  "created_at": "2024-01-15T10:30:00Z",
  "updated_at": "2024-01-15T10:30:00Z",
  "version": 1
}
```

### Session Defaults JSON

```json
{
  "connection_id": "mcp_conn_abc123",
  "user_id": "user_123",
  "agent_id": "assistant_456",
  "run_id": "conversation_789",
  "metadata": {
    "client_type": "cursor",
    "session_type": "development"
  },
  "created_at": "2024-01-15T10:00:00Z"
}
```

### Search Result JSON

```json
{
  "success": true,
  "results": [
    {
      "id": 12345,
      "content": "John Smith works at Acme Corporation as a software engineer.",
      "metadata": {
        "source": "conversation",
        "confidence": 0.95
      },
      "created_at": "2024-01-15T10:30:00Z",
      "updated_at": "2024-01-15T10:30:00Z",
      "similarity": 0.87,
      "relevance": 0.92,
      "score": 0.895,
      "search_type": "Hybrid"
    }
  ],
  "query": "software engineer at Acme",
  "total_count": 1,
  "has_more": false,
  "execution_time_ms": 45
}
```

## Database Schema Mapping

### SQLite Table Definitions

```sql
-- Database session pattern doesn't require additional tables,
-- but enhances connection management for existing tables

-- Main memories table with integer primary key
CREATE TABLE IF NOT EXISTS memories (
    id INTEGER PRIMARY KEY,
    content TEXT NOT NULL,
    user_id TEXT,
    agent_id TEXT,
    run_id TEXT,
    metadata TEXT, -- JSON
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    version INTEGER DEFAULT 1
);

-- Vector embeddings using sqlite-vec
CREATE VIRTUAL TABLE IF NOT EXISTS memory_embeddings USING vec0(
    memory_id INTEGER PRIMARY KEY,
    embedding BLOB
);

-- FTS5 virtual table for full-text search
CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
    memory_id UNINDEXED,
    content,
    metadata,
    content='memories',
    content_rowid='id'
);

-- Session defaults storage
CREATE TABLE IF NOT EXISTS session_defaults (
    connection_id TEXT PRIMARY KEY,
    user_id TEXT,
    agent_id TEXT,
    run_id TEXT,
    metadata TEXT, -- JSON
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

-- Graph entities table
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
    confidence REAL DEFAULT 1.0,
    source_memory_ids TEXT, -- JSON array
    metadata TEXT -- JSON
);

-- Graph relationships table
CREATE TABLE IF NOT EXISTS relationships (
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
);

-- Performance indexes
CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(user_id, agent_id, run_id);
CREATE INDEX IF NOT EXISTS idx_memories_created ON memories(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_entities_session ON entities(user_id, agent_id, run_id);
CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);
CREATE INDEX IF NOT EXISTS idx_relationships_session ON relationships(user_id, agent_id, run_id);
CREATE INDEX IF NOT EXISTS idx_relationships_entities ON relationships(source_entity_name, target_entity_name);
CREATE INDEX IF NOT EXISTS idx_session_defaults_created ON session_defaults(created_at DESC);
```

## Validation and Constraints

### Model Validation Rules

```csharp
/// <summary>
/// Validation extensions for memory models
/// </summary>
public static class ModelValidation
{
    /// <summary>
    /// Validates a memory entity
    /// </summary>
    public static ValidationResult ValidateMemory(this Memory memory)
    {
        var errors = new List<string>();
        
        if (string.IsNullOrWhiteSpace(memory.Content))
            errors.Add("Content cannot be empty");
            
        if (memory.Content.Length > 10000)
            errors.Add("Content cannot exceed 10,000 characters");
            
        if (string.IsNullOrWhiteSpace(memory.UserId))
            errors.Add("UserId is required for session isolation");
            
        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }
    
    /// <summary>
    /// Validates a memory context
    /// </summary>
    public static ValidationResult ValidateContext(this MemoryContext context)
    {
        var errors = new List<string>();
        
        if (string.IsNullOrWhiteSpace(context.UserId))
            errors.Add("UserId is required for session isolation");
            
        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }
}

/// <summary>
/// Validation result
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}
```

## Conclusion

This enhanced data model specification provides a comprehensive foundation for the Memory MCP Server with Database Session Pattern integration. The models support:

- **Reliable Database Operations**: Session-scoped connection management with proper resource cleanup
- **Session Isolation**: Multi-tenant data separation with strict boundaries
- **Type Safety**: Full type annotations and validation throughout
- **Flexible Search**: Vector, text, and hybrid search capabilities
- **Graph Relationships**: Entity and relationship modeling for complex data
- **Session Management**: HTTP header processing and session defaults
- **Performance Optimization**: Efficient SQLite operations with proper indexing
- **Test Support**: Complete isolation and cleanup for testing environments

All models are designed to work seamlessly with SQLite storage while providing the flexibility needed for sophisticated memory management operations in production environments. 
```

</details>  

---

## Unified Search Design

<details>
<summary>Full Unified Search Design Document</summary>

<!-- Begin UnifiedSearchDesign.md content -->
# Unified Multi-Source Search Design Document

## Executive Summary

This document outlines the design principles for transforming the Memory MCP Server's search capabilities from good (8.5/10) to exceptional (9.5/10) through a **Unified Multi-Source Search Architecture** with intelligent reranking. The design focuses on creating a seamless search experience that leverages all available data sources without requiring users to understand the underlying data structure.

## Current State Analysis

### Existing Infrastructure ✅
- **Memory Search**: Hybrid FTS5 + vector search working excellently
- **Graph Data**: 43 entities, 26 relationships available but underutilized in search
- **Vector Storage**: Complete sqlite-vec integration with semantic search
- **Reranking**: LmEmbeddings package provides rerank API integration
- **Session Isolation**: Proper multi-tenant support across all data types

### Current Search Limitations 🔄
- **Single Source**: Only searches memories, ignoring rich graph data
- **No Cross-Source Ranking**: Can't compare relevance between memories, entities, and relationships
- **Duplication**: No deduplication between related results from different sources
- **Underutilized Graph**: 43 entities and 26 relationships not searchable

### Target Improvement
- **From**: 8.5/10 search accuracy with single-source results
- **To**: 9.5/10 search accuracy with unified multi-source results and intelligent ranking

## Design Principles

### 1. Unified Search Philosophy
The system treats search as a single, comprehensive operation rather than separate queries against different data types. Users express their information need once, and the system determines the best results regardless of where that information is stored.

### 2. Multi-Source Architecture
Every search query simultaneously searches across **6 search operations**:
1. **Memory FTS5** - Full-text search on memory content
2. **Memory Vector** - Semantic similarity on memory embeddings  
3. **Entity FTS5** - Full-text search on entity names, types, aliases
4. **Entity Vector** - Semantic similarity on entity embeddings
5. **Relationship FTS5** - Full-text search on relationship types, source/target
6. **Relationship Vector** - Semantic similarity on relationship embeddings

### 3. Hierarchical Scoring Design
Results follow a weighted hierarchy to reflect information value:
- **Memories**: Base weight 1.0 (primary information source)
- **Entities**: Base weight 0.8 (extracted concepts)
- **Relationships**: Base weight 0.7 (derived connections)

### 4. Comprehensive Result Coordination
The system waits for all search operations to complete before processing results, ensuring comprehensive coverage over speed optimization.

### 5. Intelligent Deduplication Strategy
Uses hybrid approach combining:
- **Content Similarity**: Text-based similarity detection
- **Source Relationships**: Tracing entities/relationships back to originating memories

### 6. Minimal Enrichment Principle
Results include only the most directly related context (1-2 items) to maintain clarity and performance.

### 7. Dual Representation Model
Results provide both:
- **Common Interface**: Unified access pattern for all result types
- **Original Structure**: Access to source-specific fields and metadata

## Key Benefits of This Approach

### 1. User Experience Excellence
- **Natural Search**: Users don't need to think about query types - just search naturally
- **Comprehensive Results**: Every query leverages ALL available data (memories, entities, relationships)
- **Intelligent Ranking**: Best results surface regardless of source
- **Rich Context**: Results include relationship context and explanations

### 2. Technical Excellence
- **Reranking Before Cutoffs**: As requested, reranking happens before applying limits
- **Intelligent Deduplication**: Avoids returning both entity and the memory that mentions it
- **Parallel Execution**: All 6 searches run simultaneously for performance
- **Graceful Degradation**: Falls back gracefully if any component fails

### 3. Data Utilization
- **Graph Data Maximization**: 43 entities and 26 relationships become searchable
- **Cross-Source Relevance**: Can find the best result whether it's a memory, entity, or relationship
- **Context Preservation**: Relationships provide context for entity results

## Core Design Components

### Multi-Source Search Engine
**Purpose**: Orchestrate parallel searches across all data sources
**Design Principles**:
- **Parallel Execution**: All 6 search operations run simultaneously
- **Result Normalization**: Convert heterogeneous results to unified format
- **Comprehensive Coverage**: Wait for all searches to complete
- **Dual Representation**: Provide both common interface and original structure

### Intelligent Reranking System
**Purpose**: Apply semantic and multi-dimensional scoring to surface best results
**Design Principles**:
- **Semantic Relevance**: Use reranking services for cross-source comparison
- **Hierarchical Weighting**: Apply base weights (Memory 1.0, Entity 0.8, Relationship 0.7)
- **Multi-Dimensional Scoring**: Combine relevance, quality, recency, and confidence
- **Pre-Cutoff Application**: Rerank before applying result limits

### Smart Deduplication Engine
**Purpose**: Remove overlapping results while preserving valuable context
**Design Principles**:
- **Hybrid Detection**: Combine content similarity and source relationship analysis
- **Hierarchical Preference**: Prefer memories over entities over relationships
- **Context Preservation**: Retain duplicates that provide unique value
- **Minimal Enrichment**: Add only 1-2 most directly related items

## Data Flow Design

### Search Pipeline
1. **Query Input**: Single search query from user
2. **Parallel Execution**: 6 simultaneous searches across all sources
3. **Result Aggregation**: Collect and normalize results from all sources
4. **Reranking**: Apply semantic and multi-dimensional scoring
5. **Deduplication**: Remove overlaps using hybrid detection
6. **Enrichment**: Add minimal context (1-2 related items)
7. **Final Results**: Return top-ranked, deduplicated results

### Result Structure Design

```pseudo
UnifiedSearchResult {
    // Common Interface
    Id, Content, Type, Scores, Source
    
    // Original Structure Access
    OriginalEntity, OriginalRelationship, OriginalMemory
    
    // Enrichment Data
    RelatedItems (max 2), RelevanceExplanation
}

```

## Configuration Design

### Hierarchical Weights
- **Memory Results**: 1.0 (primary source)
- **Entity Results**: 0.8 (extracted concepts)  
- **Relationship Results**: 0.7 (derived connections)

### Deduplication Thresholds
- **Content Similarity**: Configurable threshold (default 85%)
- **Source Relationship**: Trace-back to originating memories
- **Context Value**: Preserve unique perspectives

### Performance Parameters
- **Search Coordination**: Wait for all sources (comprehensive over speed)
- **Result Limits**: Configurable per source
- **Enrichment Scope**: Maximum 2 related items per result

## Success Metrics

### Quantitative Targets
- **Search Accuracy**: 8.5/10 → 9.5/10
- **Result Relevance**: 90%+ of top 5 results highly relevant
- **Coverage**: 95%+ of relationship queries return results
- **Performance**: Maintain <500ms response time

### Qualitative Goals
- **Natural Search**: No query type considerations required
- **Comprehensive Results**: All data sources leveraged
- **Intelligent Ranking**: Best results surface regardless of source
- **Clean Results**: No redundant overlapping content

## Conclusion

This design transforms the Memory MCP Server into a comprehensive knowledge discovery platform through unified multi-source search with intelligent reranking. The approach prioritizes user experience simplicity while maximizing data utilization and result quality.
<!-- End UnifiedSearchDesign.md content -->

</details>
