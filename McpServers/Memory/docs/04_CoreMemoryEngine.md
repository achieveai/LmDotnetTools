# Core Memory Engine

This consolidated document merges the following original files:

- MemoryCore.md  
- MemoryDecisionEngine.md  
- SqliteAsGotoDb.md

---

## Memory Core

<details>
<summary>Full Memory Core</summary>

<!-- Begin MemoryCore.md content -->
# Memory Management Core - Detailed Design

## Overview

The Memory Management Core is the primary orchestration layer that coordinates all memory operations. It provides the main `Memory` and `AsyncMemory` classes that serve as the public API for the memory system.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Memory Core                              │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Memory    │  │ AsyncMemory │  │   MemoryBase        │  │
│  │   (Sync)    │  │   (Async)   │  │  (Abstract)         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Core Operations                          │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │     Add     │  │   Search    │  │      Update         │  │
│  │  Operation  │  │ Operation   │  │    Operation        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Delete    │  │   History   │  │      Reset          │  │
│  │ Operation   │  │ Operation   │  │    Operation        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Class Design

### 1. MemoryBase (Abstract Base Class)

**Purpose**: Defines the contract for all memory implementations, ensuring consistent interfaces across synchronous and asynchronous variants.

**Key Methods**:
- `add()`: Process messages and create new memories
- `search()`: Find relevant memories using semantic similarity
- `get()`: Retrieve specific memory by ID
- `get_all()`: List memories with filtering
- `update()`: Modify existing memory content
- `delete()`: Remove specific memory
- `delete_all()`: Clear memories for a session
- `history()`: Track memory changes over time
- `reset()`: Complete system reset

**Design Considerations**:
- All methods accept session parameters (user_id, agent_id, run_id) for isolation
- Flexible message input format (string or structured conversation)
- Optional metadata support for rich context
- Consistent return formats across operations

### 2. Memory (Synchronous Implementation)

**Initialization Strategy**:
- Component factory pattern for provider instantiation
- Configuration-driven setup with sensible defaults
- Lazy initialization of optional components (graph store)
- Connection validation during startup

**Core Operation Flow**:

**Add Operation**:
1. **Session Context Building**: Extract and validate session identifiers
2. **Message Processing**: Normalize input format and validate content
3. **Processing Mode Selection**:
   - **Inference Mode**: Use LLM for fact extraction and decision making
   - **Direct Mode**: Store messages without AI processing
4. **Memory Creation**: Generate embeddings and store with metadata
5. **History Tracking**: Log all operations for audit trail

#### Add Operation Flow

_Adds a new memory by building session context, validating permissions, normalizing input, generating embeddings, and logging the operation. See source code for full implementation._

```
FUNCTION add_memory(messages, user_id, agent_id, run_id, metadata, mode):
    // 1. Session Context Building
    session_context = build_session_context(user_id, agent_id, run_id)
    validate_session_permissions(session_context)
    
    // 2. Message Processing
    normalized_messages = normalize_message_format(messages)
    validate_message_content(normalized_messages)
    
    // 3. Processing Mode Selection
    IF mode == "inference":
        // AI-powered processing
        extracted_facts = fact_extractor.extract_facts(
            messages=normalized_messages,
            context=session_context
        )
        
        existing_memories = vector_store.search_similar(
            facts=extracted_facts,
            session_filter=session_context,
            limit=20
        )
        
        memory_operations = decision_engine.decide_operations(
            existing_memories=existing_memories,
            new_facts=extracted_facts,
            context=session_context
        )
        
        results = execute_memory_operations(memory_operations, session_context)
        
    ELSE IF mode == "direct":
        // Direct storage without AI processing
        memory_content = format_direct_memory(normalized_messages, metadata)
        embedding = embedding_provider.generate_embedding(memory_content)
        
        memory_record = create_memory_record(
            content=memory_content,
            embedding=embedding,
            session=session_context,
            metadata=metadata
        )
        
        memory_id = vector_store.insert(memory_record)
        results = [{"id": memory_id, "event": "ADD"}]
    
    // 4. History Tracking
    log_operation_history(
        operation="add",
        session=session_context,
        results=results,
        input_messages=normalized_messages
    )
    
    RETURN results
END FUNCTION
```

**Search Operation**:
1. **Query Processing**: Generate embeddings for search query
2. **Filter Construction**: Build session-based and metadata filters
3. **Vector Search**: Execute similarity search with scoring
4. **Result Formatting**: Structure results with metadata and scores
5. **Access Control**: Ensure session isolation

#### Search Operation Flow

_Performs a search by building session context, validating permissions, generating query embeddings, filtering, running vector/text search, and formatting results. See source code for full implementation._

```
FUNCTION search_memories(query, user_id, agent_id, run_id, filters, limit):
    // 1. Session Context & Access Control
    session_context = build_session_context(user_id, agent_id, run_id)
    validate_search_permissions(session_context)
    
    // 2. Query Processing
    query_embedding = embedding_provider.generate_embedding(query)
    
    // 3. Filter Construction
    search_filters = build_search_filters(
        session=session_context,
        user_filters=filters,
        access_controls=get_access_controls(session_context)
    )
    
    // 4. Vector Search Execution
    raw_results = vector_store.similarity_search(
        query_vector=query_embedding,
        filters=search_filters,
        limit=limit,
        score_threshold=0.7
    )
    
    // 5. Result Processing & Formatting
    formatted_results = []
    FOR result IN raw_results:
        formatted_result = format_search_result(
            memory=result,
            query=query,
            session=session_context
        )
        formatted_results.append(formatted_result)
    
    // 6. Access Control Validation
    validated_results = apply_access_control(formatted_results, session_context)
    
    // 7. Result Enhancement
    enhanced_results = enhance_results_with_metadata(validated_results)
    
    RETURN enhanced_results
END FUNCTION
```

**Update/Delete Operations**:
1. **Existence Validation**: Verify memory exists and belongs to session
2. **Change Tracking**: Capture before/after states
3. **Vector Updates**: Regenerate embeddings if content changes
4. **History Logging**: Record operation details and timestamps

#### Update Operation Flow

_Updates a memory by validating existence, tracking changes, updating vectors, and logging the operation. See source code for full implementation._

```sql
FUNCTION update_memory(memory_id, new_content, user_id, agent_id, run_id):
    // 1. Session Context & Validation
    session_context = build_session_context(user_id, agent_id, run_id)
    
    // 2. Existence & Permission Validation
<!-- End MemoryCore.md content -->
```

</details>  

---

## Memory Decision Engine

<details>
<summary>Full Memory Decision Engine</summary>

<!-- Begin MemoryDecisionEngine.md content -->
# Memory Decision Engine - Enhanced with Database Session Pattern

## Overview

The Memory Decision Engine is responsible for intelligently deciding what operations to perform on memories when new facts are extracted. It uses sophisticated LLM-powered logic to determine whether to ADD new memories, UPDATE existing ones, DELETE outdated information, or take no action. Enhanced with Database Session Pattern integration, it ensures reliable resource management and session-scoped decision making.

**ARCHITECTURE ENHANCEMENT**: This design has been updated to integrate with the Database Session Pattern, providing session-aware memory decision operations and reliable resource management for AI-powered memory management.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│            Memory Decision Engine (Enhanced)                │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Decision   │  │   Memory    │  │     Operation       │  │
│  │  Analyzer   │  │ Comparator  │  │   Generator         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Session Integration Layer                    │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ Session     │  │ Context     │  │   Memory            │  │
│  │ Scoped      │  │ Resolver    │  │  Repository         │  │
│  │ Decisions   │  │             │  │  Integration        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Decision Types                           │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │     ADD     │  │   UPDATE    │  │      DELETE         │  │
│  │ New Memory  │  │  Existing   │  │   Outdated          │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Advanced Logic                           │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ Conflict    │  │ Similarity  │  │    Temporal         │  │
│  │ Resolution  │  │  Analysis   │  │   Reasoning         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Core Components

### 1. MemoryDecisionEngine (Main Class) with Session Support

**Purpose**: Orchestrates the entire decision-making process for memory operations, combining LLM intelligence with analytical logic and session-scoped database operations.

**Core Responsibilities**:
- Analyze relationships between existing memories and new facts within session scope
- Generate sophisticated prompts for LLM-based decision making with session context
- Parse and validate LLM responses for operation instructions
- Apply conflict resolution and quality assurance logic
- Coordinate with memory analyzer and validation components using database sessions
- Ensure session isolation and proper resource cleanup

**Session-Enhanced Interface**:
```csharp
public interface IMemoryDecisionEngine
{
    Task<MemoryOperations> DecideOperationsAsync(
        ISqliteSession session,
        IEnumerable<string> facts,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default);
    
    Task<MemoryOperations> DecideOperationsWithExistingAsync(
        ISqliteSession session,
        IEnumerable<string> facts,
        IEnumerable<ExistingMemory> existingMemories,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default);
    
    Task<ConflictResolutionResult> ResolveConflictsAsync(
        ISqliteSession session,
        MemoryOperations operations,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default);
}
```

**Decision Process Flow with Session Pattern**:
1. **Session-Scoped Memory Analysis**: Examine existing memories and new facts for relationships within session boundaries
2. **Context Building**: Construct rich context including session information and similarity analysis
3. **LLM Consultation**: Generate sophisticated prompts with session context and obtain structured decisions
4. **Response Validation**: Parse and validate LLM responses for operation feasibility
5. **Session-Aware Conflict Resolution**: Apply advanced logic to resolve conflicting operations within session scope
6. **Quality Assurance**: Ensure all operations meet quality and consistency standards with session validation

**Implementation with Session Pattern**:
```csharp
public class MemoryDecisionEngine : IMemoryDecisionEngine
{
    private readonly ILlmProvider _llmProvider;
    private readonly IMemoryRepository _memoryRepository;
    private readonly ILogger<MemoryDecisionEngine> _logger;

    public async Task<MemoryOperations> DecideOperationsAsync(
        ISqliteSession session,
        IEnumerable<string> facts,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        // Get existing memories for session using database session
        var existingMemories = await _memoryRepository.GetMemoriesForSessionAsync(
            session, sessionContext, cancellationToken);

        return await DecideOperationsWithExistingAsync(
            session, facts, existingMemories, sessionContext, cancellationToken);
    }

    public async Task<MemoryOperations> DecideOperationsWithExistingAsync(
        ISqliteSession session,
        IEnumerable<string> facts,
        IEnumerable<ExistingMemory> existingMemories,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default)
    {
        // Create integer mapping for LLM clarity
        var idMapping = CreateIntegerMapping(existingMemories);
        
        // Build session-aware decision prompt
        var prompt = BuildDecisionPrompt(facts, idMapping, sessionContext);
        
        // Get decision from LLM provider with session context
        var operations = await _llmProvider.DecideMemoryOperationsAsync(
            facts, existingMemories, sessionContext, cancellationToken);
        
        // Validate operations within session scope
        var validatedOperations = await ValidateOperationsAsync(
            session, operations, sessionContext, cancellationToken);
        
        // Resolve conflicts with session awareness
        var resolvedOperations = await ResolveConflictsAsync(
            session, validatedOperations, sessionContext, cancellationToken);
        
        _logger.LogDebug("Generated {OperationCount} memory operations for session {UserId}/{AgentId}/{RunId}",
            resolvedOperations.Operations.Count, sessionContext.UserId, sessionContext.AgentId, sessionContext.RunId);
        
        return resolvedOperations;
    }

    private Dictionary<int, int> CreateIntegerMapping(IEnumerable<ExistingMemory> memories)
    {
        // Create simple 1-based mapping for LLM clarity
        var mapping = new Dictionary<int, int>();
        var index = 1;
        
        foreach (var memory in memories)
        {
            mapping[index] = memory.Id;
            index++;
        }
        
        return mapping;
    }

    private string BuildDecisionPrompt(
        IEnumerable<string> facts, 
        Dictionary<int, int> idMapping, 
        MemoryContext sessionContext)
    {
        var existingMemoriesText = string.Join("\n", 
            idMapping.Select(kvp => $"{kvp.Key}. {GetMemoryContent(kvp.Value)}"));

        return $@"
You are a smart memory manager for a session-aware memory system.

Session Context:
- User ID: {sessionContext.UserId ?? "unknown"}
- Agent ID: {sessionContext.AgentId ?? "unknown"}
- Run ID: {sessionContext.RunId ?? "unknown"}

You can perform four operations: (1) ADD, (2) UPDATE, (3) DELETE, and (4) NONE.

New facts to process:
{string.Join("\n", facts.Select((f, i) => $"- {f}"))}

Existing memories for this session:
{existingMemoriesText}

Decide what operations to perform. Use simple numbers (1, 2, 3, etc.) to reference existing memories.
Consider the session context when making decisions - memories should be relevant to this specific user/agent/run.

Return operations in JSON format with integer IDs.";
    }
}
```

### 2. Memory Decision Prompts

**Prompt Engineering Strategy**:
- Sophisticated system prompts with comprehensive decision guidelines
- Rich context integration including existing memories and analytical insights
- Few-shot examples demonstrating complex decision scenarios
- Clear output format specification for structured responses

#### Example: Memory Decision Prompt Template
<!-- End MemoryDecisionEngine.md content -->

</details>  

---

## SQLite as Goto DB

<details>
<summary>Full SQLite as Goto DB</summary>

<!-- Begin SqliteAsGotoDb.md content -->
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
            _logger.LogWarning(ex, "Failed to load sqlite-vec extension. Vector search may not be available.");
        }

        // Apply performance and reliability PRAGMAs
        var pragmas = new[]
        {
            "PRAGMA journal_mode=WAL;",       // Write-Ahead Logging for concurrency
            "PRAGMA synchronous=NORMAL;",     // Good balance of safety and speed
            "PRAGMA cache_size=-2000;",       // 2MB cache (SQLite default is 2000 pages, page size often 1KB or 4KB)
            "PRAGMA foreign_keys=ON;",        // Enforce foreign key constraints
            "PRAGMA busy_timeout=5000;"       // Wait 5 seconds if DB is locked
        };

        foreach (var pragma in pragmas)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = pragma;
            await cmd.ExecuteNonQueryAsync();
        }
        
        _logger.LogDebug("SQLite connection configured with performance pragmas");
    }
<!-- End SqliteAsGotoDb.md content -->

> **Note:** The full implementation of `SqliteSession` and other large classes has been removed for clarity. See the source code for details.


</details>
