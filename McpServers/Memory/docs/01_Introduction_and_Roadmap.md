# Introduction & Roadmap

This consolidated document merges the following original files:

- README.md
- ExecutionPlan.md
- UberDesignDocument.md

---

## README.md

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

1.  **[03_Architecture_and_DataModels.md](./03_Architecture_and_DataModels.md)** - Comprehensive system architecture
    -   High-level system design and component interactions
    -   Database Session Pattern architecture for reliable connection management
    -   Data flow diagrams and processing pipelines
    -   Integration patterns and deployment strategies
    -   Performance optimization and monitoring approaches

2.  **[02_Requirements.md](./02_Requirements.md)** - Detailed functional specifications
    -   Complete MCP tool specifications with session pattern integration
    -   Session management and isolation requirements
    -   Database Session Pattern functional requirements (FR-DB-001 through FR-DB-020)
    -   Performance, security, and reliability requirements
    -   Testing and validation criteria

3.  **[01_Introduction_and_Roadmap.md](./01_Introduction_and_Roadmap.md)** - Implementation roadmap
    -   Phase-by-phase development plan including Database Session Pattern implementation
    -   Phase 1.5: Critical Database Session Pattern implementation phase
    -   Milestone definitions and success criteria
    -   Risk assessment and mitigation strategies
    -   Resource requirements and timeline

### Technical Specifications

4.  **[04_CoreMemoryEngine.md](./04_CoreMemoryEngine.md)** - SQLite storage architecture
    -   Database Session Pattern implementation details
    -   SQLite with sqlite-vec integration for vector operations
    -   Session-scoped connection management and resource cleanup
    -   Test isolation mechanisms and production reliability
    -   Schema design and performance optimization

5.  **[04_CoreMemoryEngine.md](./04_CoreMemoryEngine.md)** - Core memory management
    -   Memory and AsyncMemory class implementations with session pattern
    -   Session-scoped database operations and transaction management
    -   Dual processing modes (inference vs direct)
    -   Component orchestration and error handling
    -   Performance optimization and caching strategies

6.  **[03_Architecture_and_DataModels.md](./03_Architecture_and_DataModels.md)** - Data structures and schemas
    -   Database Session Pattern interfaces (ISqliteSession, ISqliteSessionFactory)
    -   Session configuration and performance metrics models
    -   Core memory entities with integer ID support
    -   Session context and isolation models
    -   Graph memory and relationship structures

7.  **[08_VectorStorage_and_Persistence.md](./08_VectorStorage_and_Persistence.md)** - Vector storage system
    -   SQLite with sqlite-vec integration for semantic similarity search
    -   Session isolation and metadata filtering
    -   Graph memory integration with relationship extraction
    -   Performance optimization and monitoring

### Implementation Guides

8.  **[05_LLM_Integration.md](./05_LLM_Integration.md)** - LLM provider integration
    -   OpenAI and Anthropic provider implementations
    -   Structured output handling and response parsing
    -   Error handling and fallback mechanisms
    -   Cost optimization and rate limiting

9.  **[04_CoreMemoryEngine.md](./04_CoreMemoryEngine.md)** - Decision-making system
    -   AI-powered memory operation decisions
    -   Conflict resolution and consistency management
    -   Integer ID mapping for LLM compatibility
    -   Temporal reasoning and relationship analysis

10. **[02_Requirements.md](./02_Requirements.md)** - Fact extraction engine
    -   LLM-powered fact extraction from conversations
    -   Custom prompt configuration and domain adaptation
    -   Multi-language support and cultural considerations
    -   Quality validation and filtering mechanisms

## Key Features

**Database Session Pattern**:
-   Reliable SQLite connection lifecycle management
-   Proper resource cleanup with automatic WAL checkpoint handling
-   Complete test isolation with unique database instances
-   Connection leak detection and prevention
-   Production-ready connection pooling and monitoring

**Multi-Provider Support**:
-   OpenAI and Anthropic LLM providers
-   SQLite with sqlite-vec for vector storage
-   Extensible architecture for future providers

**Advanced Capabilities**:
-   Intelligent fact extraction from conversations
-   AI-powered memory decision making (ADD/UPDATE/DELETE)
-   Semantic similarity search with vector embeddings
-   Full-text search with SQLite FTS5
-   Session-based memory isolation
-   Graph memory with entity and relationship extraction
-   Procedural memory for agent workflow documentation
-   Vision message processing for multimodal conversations

**Production Features**:
-   Comprehensive error handling and recovery
-   Performance monitoring and optimization
-   Secure session management and access control
-   Scalable architecture with horizontal scaling support
-   Database session pattern for reliable resource management

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
-   LLM provider implementations (OpenAI, Anthropic)
-   SQLite storage with Database Session Pattern integration
-   Memory core classes with session management
-   Basic configuration and error handling

**Deliverables**:
-   Working LLM provider factory with structured output
-   Functional SQLite vector storage with session-scoped CRUD operations
-   Memory class with basic add/search functionality using session pattern
-   Comprehensive test suite for core components with test isolation

### Phase 1.5: Database Session Pattern (Weeks 2.5-3.5) **NEW PHASE**
**Session Pattern Implementation**
-   ISqliteSession and ISqliteSessionFactory interfaces
-   Production and test session implementations
-   Repository migration to session pattern
-   Comprehensive testing and validation

**Deliverables**:
-   Reliable SQLite connection management
-   Test isolation and cleanup mechanisms
-   Eliminated file locking issues
-   Improved resource management and monitoring

### Phase 2: Core Operations (Weeks 4-5)
**Memory Operations**
-   Session-scoped memory storage and retrieval
-   Integer ID management for LLM compatibility
-   Basic search functionality with session isolation
-   Session defaults and HTTP header processing

**Deliverables**:
-   Complete memory CRUD operations with session pattern
-   Session isolation and security
-   Integer ID generation and mapping
-   HTTP header processing for session defaults

### Phase 3: Intelligence (Weeks 6-7)
**AI-Powered Features**
-   Fact extraction engine with custom prompts
-   Memory decision engine with conflict resolution
-   Advanced search with semantic similarity
-   Vision message processing

**Deliverables**:
-   Intelligent memory operations (ADD/UPDATE/DELETE/NONE)
-   Question-answering capabilities
-   Vision and multimodal support
-   Custom prompt configuration

### Phase 4: Advanced Features (Weeks 8-9)
**Production Features**
-   Graph memory with entity and relationship extraction
-   Procedural memory for agent workflows
-   Performance optimization and caching
-   Monitoring and observability

**Deliverables**:
-   Full graph memory capabilities
-   Agent workflow documentation
-   Performance optimization
-   Production monitoring

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
-   .NET 9.0 SDK
-   SQLite with sqlite-vec extension support
-   OpenAI or Anthropic API key

### Quick Start Process
1.  **Environment Setup**: Configure API keys and SQLite database
2.  **Basic Configuration**: Set up minimal configuration for testing
3.  **Component Testing**: Validate individual components work correctly
4.  **Integration Testing**: Test complete memory workflows
5.  **Session Pattern Validation**: Verify database session management and cleanup

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
    "Production": {
      "ConnectionString": "DefaultConnection",
      "EnableWalCheckpoint": true,
      "WalCheckpointIntervalSeconds": 300,
      "EnableConnectionPooling": true,
      "MaxPoolSize": 100
    },
    "Test": {
      "UseInMemoryDatabase": false,
      "TestDatabasePrefix": "test_memory_",
      "AutoDeleteTestDatabase": true,
      "EnableWalCheckpoint": true,
      "WalCheckpointIntervalSeconds": 5
    }
  },
  "MemoryServerOptions": {
    "DefaultUserId": "default_user",
    "DefaultAgentId": "default_agent",
    "DefaultRunId": "default_run",
    "EnableGraphProcessing": true,
    "GraphProcessingIntervalSeconds": 60,
    "MaxItemsPerSearchResult": 10,
    "EmbeddingBatchSize": 50,
    "EmbeddingCacheDurationMinutes": 1440
  }
}
```

### Advanced Configuration

- **Session Management**: Configure session defaults, isolation levels, and timeout settings.
- **LLM Providers**: Customize models, API endpoints, and retry policies.
- **Vector Storage**: Adjust embedding dimensions, similarity metrics, and indexing parameters.
- **Logging & Monitoring**: Integrate with existing logging frameworks and monitoring tools.

## Usage

### MCP Tools

The server exposes functionality through MCP tools. Key tools include:

- `memory_add`: Adds a new memory item.
- `memory_search`: Searches for memories.
- `memory_get_all`: Retrieves all memories for a session.
- `memory_delete`: Deletes a specific memory.
- `memory_init_session`: Initializes a session with default context.

### Example Workflow

1. **Initialize Session**: Client sends `memory_init_session` with `userId`, `agentId`, `runId`.
2. **Add Memory**: Client sends `memory_add` with conversation content.
3. **Search Memory**: Client sends `memory_search` with a query.
4. **Retrieve Results**: Server returns relevant memories.

## Development

### Running Tests
```bash
dotnet test
```

### Code Style
- Follow standard C# coding conventions.
- Use async/await for all I/O-bound operations.
- Implement comprehensive error handling.

### Contributing
[Contribution guidelines]

## License

[License information]

---

This documentation provides comprehensive guidance for implementing and using the Memory MCP Server with the enhanced Database Session Pattern architecture, ensuring reliable resource management and robust production deployment. 
<!-- End README.md content -->
---

## Execution Plan
# Memory MCP Server - Execution Plan

## Project Overview

The Memory MCP Server is an intelligent memory management system that provides persistent storage and retrieval of conversation memories using the Model Context Protocol (MCP). The system features session isolation, knowledge graph capabilities, and integer-based memory IDs for better LLM integration.

## Current Status: 99-100% Complete ✅

### 🚧 FUTURE ENHANCEMENTS - Memory Search Optimization

Based on comprehensive testing and analysis, the following enhancements have been identified to transform the already excellent search system into an exceptional knowledge discovery platform:

## **MASTER CHECKLIST - Phases 6-8 Overview**

### **Phase 6: Unified Multi-Source Search Engine (2-3 weeks)**
- [ ] **Week 1**: Database schema changes, enhanced GraphRepository with FTS5 and vector search for entities/relationships
- [ ] **Week 2**: UnifiedSearchEngine implementation with parallel execution of 6 search operations
- [ ] **Week 3**: Service integration, performance optimization, comprehensive testing

### **Phase 7: Intelligent Reranking System (1-2 weeks)**  
- [ ] **Week 1**: RerankingEngine with multi-dimensional scoring and hierarchical weighting (Memory 1.0, Entity 0.8, Relationship 0.7)
- [ ] **Week 2**: Integration with reranking services, advanced scoring features, comprehensive testing

### **Phase 8: Smart Deduplication & Result Enrichment (1-2 weeks)**
- [ ] **Week 1**: DeduplicationEngine with hybrid detection (content similarity + source relationships)
- [ ] **Week 2**: ResultEnricher with minimal enrichment (max 2 items), full pipeline integration and testing

### **Success Criteria for All Phases**
- [ ] Search accuracy improved from 8.5/10 to 9.5/10 target
- [ ] All 245+ existing tests continue to pass
- [ ] Performance maintained at <500ms response time
- [ ] Graph data (43 entities, 26 relationships) fully searchable
- [ ] Natural search experience without query type considerations
- [ ] Clean results with intelligent deduplication

---
<!-- End ExecutionPlan.md content -->
---


## Uber Design Document

This section outlines the comprehensive design for the Memory MCP Server, focusing on its architecture, core components, data flow, and key design principles. It aims to provide a high-level understanding of the system's structure and rationale.

### Executive Summary

**What:**
*   A .NET 9.0-based MCP (Model Context Protocol) server.
*   Persists and retrieves conversation memories.
*   Exposes MCP tools (e.g., `add_memory`, `search_memory`) over SSE (Server-Sent Events) and STDIO transports.
*   Utilizes a hybrid search approach combining semantic (vector) search via `sqlite-vec` and full-text search (FTS5).
*   Employs a Database Session Pattern for robust SQLite connection management.
*   Features session isolation based on `user_id`, `agent_id`, and `run_id`.
*   Uses integer IDs for memory items to optimize LLM token usage.

**Why:**
*   **.NET 9.0:** Offers modern performance, cross-platform capabilities, and strong built-in support for dependency injection, logging, and asynchronous programming.
*   **MCP Tools:** Enable Language Model (LLM) agents to interact with memory as first-class functions, simplifying integration and complex workflows.
*   **Hybrid Search:** Provides a balance between the nuanced understanding of semantic search and the comprehensive recall of full-text search, leading to more relevant and complete results.
*   **Database Session Pattern:** Ensures reliable SQLite connection handling, prevents file locking issues, guarantees resource cleanup, and facilitates isolated testing environments.
*   **Integer IDs:** Reduces the token footprint when memory items are included in LLM prompts, making interactions more efficient and cost-effective.

---

### System Architecture Overview

#### High-Level Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    Memory MCP Server                        │
├─────────────────────────────────────────────────────────────┤
│                    MCP Protocol Layer                       │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ SSE Server  │  │ Tool Router │  │   Tool Registry     │  │
│  │ Transport   │  │             │  │ (MCP Tools)         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Memory Core Layer                        │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Memory    │  │ AsyncMemory │  │   Session           │  │
│  │   Manager   │  │   Manager   │  │  Management         │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Intelligence Layer                           │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │    Fact     │  │   Memory    │  │      LLM            │  │
│  │ Extraction  │  │  Decision   │  │   Provider          │  │
│  │   Engine    │  │   Engine    │  │   Factory           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Storage Layer (SQLite-based)                 │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Database   │  │   SQLite    │  │      FTS5           │  │
│  │  Session    │  │ with        │  │   Full-Text         │  │
│  │  Pattern    │  │ sqlite-vec  │  │    Search           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Infrastructure Layer                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │Configuration│  │  Logging &  │  │     Error           │  │
│  │  Manager    │  │ Monitoring  │  │   Handling          │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

#### Database Session Pattern Architecture

**Rationale:** The Database Session Pattern is crucial for managing SQLite connections reliably. SQLite, being file-based, can suffer from file locking (`SQLITE_BUSY`) and resource leakage if connections are not handled meticulously, especially in concurrent environments or during testing. This pattern ensures:
*   **Connection Lifecycle Management:** Controlled acquisition, usage, and disposal of `SqliteConnection` objects.
*   **Test Isolation:** Each test can operate on a clean, isolated database instance (e.g., in-memory or a unique file), preventing interference.
*   **Resource Cleanup:** Guarantees that connections are closed and resources (like WAL files) are properly managed.
*   **WAL Mode Benefits:** Enables Write-Ahead Logging for better concurrency and performance, with mechanisms for periodic checkpointing.

**Key Components:**
*   `ISqliteConnectionProvider`: Interface for obtaining SQLite connections.
*   `SqliteConnectionProvider`: Concrete implementation managing connection strings and creation.
*   `ISqliteSession`: Represents an active session with a dedicated SQLite connection.
*   `SqliteSession`: Implements `ISqliteSession`, handling transactions and command execution.
*   `ISqliteSessionFactory`: Interface for creating `ISqliteSession` instances.
*   `SqliteSessionFactory`: Manages session creation, often tied to specific configurations (e.g., read-only, read-write).

#### Core Design Principles

*   **SQLite-First & `sqlite-vec`:** Leverage SQLite for its simplicity, ubiquity, and zero-configuration nature. The `sqlite-vec` extension adds powerful vector search capabilities directly within SQLite, avoiding the need for a separate vector database for many use cases.
*   **Database Session Pattern:** As detailed above, this is fundamental for reliability and testability.
*   **MCP Protocol Compliance:** Adherence to the Model Context Protocol for standardized communication with LLM agents and other MCP-compliant systems.
*   **Session Isolation:** Strict data separation using `user_id`, `agent_id`, and `run_id` to ensure that one user/agent cannot access another's data. This is enforced at the database query level.
*   **Integer IDs for Memories:** Using auto-incrementing integer primary keys for memory items instead of GUIDs or other string-based IDs. This significantly reduces the number of tokens consumed when these IDs are referenced in LLM prompts.
*   **LLM-Optimized Design:** Features like integer IDs, efficient data retrieval, and structured tool outputs are designed to work seamlessly and efficiently with Large Language Models.
*   **Production Readiness:** Emphasis on robust error handling, comprehensive logging, metrics for monitoring, and a design that supports scalability.
*   **Asynchronous Operations:** Primarily `async/await` based to ensure non-blocking I/O and efficient resource utilization.
*   **Dependency Injection:** Extensive use of DI for loose coupling, testability, and maintainability.

---

### Component Design Highlights

#### 1. Database Session Pattern Layer

*   **Interfaces:**
    *   `ISqliteConnectionProvider`: Provides `SqliteConnection` instances.
    *   `ISqliteSession`: Represents an active, isolated database session. Manages a single `SqliteConnection`, transaction state, and provides methods for executing commands.
    *   `ISqliteSessionFactory`: Creates instances of `ISqliteSession`.
*   **Implementations:**
    *   `SqliteConnectionProvider`: Manages connection strings and options.
    *   `SqliteSession`: Implements `IDisposable` for resource cleanup. Handles `BeginTransaction`, `Commit`, `Rollback`.
    *   `SqliteSessionFactory`: Creates sessions, potentially with different configurations (e.g., read-only vs. read-write, specific database file per session for testing).
*   **Rationale:** This layer is foundational for all data persistence, ensuring that SQLite is used in a safe, reliable, and testable manner. It abstracts away the complexities of connection management from the upper layers.

#### 2. MCP Protocol Layer

*   **SSE Transport (`McpSseServer`):** Implements Server-Sent Events for real-time, bidirectional communication with MCP clients. Handles connection management, message serialization/deserialization, and event streaming.
*   **Tool Registry (`ToolRegistry`):** Discovers and registers all available MCP tools (e.g., `AddMemoryTool`, `SearchMemoryTool`). Tools are typically implemented as classes adhering to an `IMcpTool` interface.
*   **Tool Router (`ToolRouter`):** Receives tool invocation requests from the transport layer, identifies the correct tool from the `ToolRegistry`, and dispatches the request to it.
*   **Session Context Resolution (`ISessionContextResolver`, `HeaderProcessor`):**
    *   `HeaderProcessor`: Extracts session identifiers (`X-Memory-User-ID`, `X-Memory-Agent-ID`, `X-Memory-Run-ID`) from incoming request headers (e.g., HTTP headers for SSE).
    *   `SessionContextResolver`: Uses these identifiers to establish a `SessionContext` for the current operation. This context is then used throughout the request lifecycle to ensure data isolation.
*   **Rationale:** This layer provides the primary interface to the Memory MCP Server, enabling LLMs and other clients to interact with its memory capabilities through a standardized protocol.

#### 3. Memory Core Layer

*   **Memory Manager (`IMemoryManager`, `MemoryManager`):**
    *   The central façade for all memory operations.
    *   Provides methods like `AddMemoryAsync`, `SearchMemoryAsync`, `UpdateMemoryAsync`, `DeleteMemoryAsync`, `GetMemoryByIdAsync`, `GetAllMemoriesAsync`.
    *   Orchestrates interactions between the intelligence layer (for decision making) and the storage layer (for persistence).
    *   Ensures that all operations are performed within the correct `SessionContext` and utilize the Database Session Pattern.
*   **Integer ID Management (`MemoryIdGenerator`):**
    *   Responsible for generating unique, sequential integer IDs for new memory items. Typically uses SQLite's `last_insert_rowid()` or a dedicated sequence table.
*   **Session Management Models (`SessionContext`, `SessionDefaults`):**
    *   `SessionContext`: Holds the `UserId`, `AgentId`, and `RunId` for the current operation, ensuring data isolation.
    *   `SessionDefaults`: Allows pre-configuring default session identifiers if not provided explicitly in a request, often set at the connection level.
*   **Rationale:** This layer encapsulates the core business logic of memory management, providing a clean API for higher-level components and ensuring that operations are session-aware and use integer IDs.

#### 4. Intelligence Layer

*   **Memory Decision Engine (`MemoryDecisionEngine`):**
    *   (Conceptual) An advanced component responsible for making intelligent decisions about memory operations. For example, it might decide whether to create a new memory, update an existing one, or ignore redundant information based on LLM analysis or predefined rules.
    *   Crucially, it would operate with integer IDs when interacting with LLMs to determine relationships or similarity with existing memories.
    *   Provides methods for adding embeddings and performing similarity searches (e.g., k-NN search).
    *   All operations are session-scoped and use integer IDs to link embeddings back to their parent memory items.
*   **Full-Text Search (FTS5):**
    *   Utilizes SQLite's FTS5 extension for efficient keyword-based search on memory content.
    *   An FTS5 virtual table (e.g., `memory_fts`) is created, populated with text content from memories.
    *   Provides methods for querying this table using FTS5 syntax (e.g., `MATCH` operator).
*   **Data Access Components (Repositories/DAOs):**
    *   Classes responsible for direct database interaction (CRUD operations) for memory items, metadata, and other related entities.
    *   These components use `ISqliteSession` from the Database Session Pattern layer to execute SQL commands.
*   **Schema Definition:** Includes tables for:
    *   `memories` (id INTEGER PRIMARY KEY, content TEXT, metadata TEXT, created_at DATETIME, updated_at DATETIME, session_user_id TEXT, session_agent_id TEXT, session_run_id TEXT)
    *   `memory_embeddings` (memory_id INTEGER, embedding BLOB, FOREIGN KEY(memory_id) REFERENCES memories(id))
    *   `memory_fts` (FTS5 virtual table mirroring content from `memories`)
    *   `memory_id_sequence` (if using a sequence table for ID generation)
*   **Rationale:** This layer provides the actual persistence and retrieval mechanisms, combining the power of semantic vector search with traditional full-text search, all within the SQLite ecosystem and respecting session isolation.

---

### Data Flow Architecture Examples

#### 1. Session Initialization and Setting Defaults (e.g., via HTTP Headers)

1.  Client connects (e.g., SSE connection request).
2.  `HeaderProcessor` (MCP Protocol Layer) extracts `X-Memory-User-ID`, `X-Memory-Agent-ID` from request headers.
3.  These are stored as `SessionDefaults` associated with the connection ID by the `SessionManager`.
4.  Subsequent tool calls on this connection will use these defaults if specific session parameters are not provided in the tool call payload.

#### 2. Adding a New Memory (`add_memory` tool)

1.  Client invokes `add_memory` tool via MCP transport (e.g., SSE).
2.  `ToolRouter` routes the request to `AddMemoryTool`.
3.  `AddMemoryTool` uses `ISessionContextResolver` to determine the `SessionContext` (from request payload or connection defaults).
4.  `AddMemoryTool` calls `IMemoryManager.AddMemoryAsync(request, sessionContext)`.
5.  `MemoryManager`:
    a.  (Optional) Interacts with `FactExtractionEngine` or `MemoryDecisionEngine` (Intelligence Layer) if advanced processing is needed.
    b.  Uses `MemoryIdGenerator` to get the next integer ID.
    c.  Uses `ISqliteSessionFactory` to get an `ISqliteSession`.
    d.  Within the session, inserts the new memory data (content, metadata, session identifiers, new integer ID) into the `memories` table.
    e.  If embeddings are generated (e.g., by an LLM or locally):
        i.  Inserts the embedding into the `memory_embeddings` table, linked by the new integer ID.
    f.  Populates the `memory_fts` table.
    g.  Commits the transaction via `ISqliteSession`.
6.  The result (including the new integer memory ID) is returned up the chain to the client.

---

### Configuration and Deployment

*   **Configuration (`appsettings.json`):**
    *   Database connection strings (e.g., path to SQLite file).
    *   LLM provider API keys and model preferences.
    *   Logging levels.
    *   Server port for SSE.
    *   Default session parameters.
*   **Dependency Injection Setup:**
    *   `IServiceCollection` extensions register all services (Managers, Factories, Repositories, Tools, etc.) for DI.
*   **Deployment:**
    *   Can be deployed as a standalone .NET application.
    *   Containerization (e.g., Docker) is recommended for consistent environments.

---

### Testing Strategy

*   **Unit Testing:**
    *   Focus on individual components (Managers, Services, Tools) using mocking (e.g., Moq) for dependencies like `ISqliteSession`, `ILlmProviderFactory`.
    *   Validate logic with integer IDs and session context propagation.
*   **Integration Testing:**
    *   Test interactions between components, especially with a real SQLite database (often using in-memory SQLite or unique test database files managed by the Database Session Pattern).
    *   Verify session isolation by ensuring data from one session context is not visible in another.
    *   Test the full flow of MCP tool calls through to database operations.
*   **End-to-End Testing:**
    *   Test the server as a black box, making requests via its transport layer (e.g., an MCP client connecting over SSE) and verifying responses.

---

### Security Considerations

*   **Session Data Isolation:** This is the primary security mechanism for multi-tenancy. Thoroughly test that session identifiers in WHERE clauses prevent data leakage.
*   **Input Validation:** Validate all inputs, especially session identifiers from headers or payloads, to prevent injection or manipulation. Use regex and length checks.
*   **Authentication/Authorization for Transports:** While MCP itself might not define auth, the transport layer (e.g., HTTP for SSE) should implement appropriate authentication (e.g., API keys, JWT) to protect access to the server. (This was covered in `AuthSupport.md`).
*   **Secure ID Generation:** If using database sequences for integer IDs, ensure they are robust and not guessable if external exposure is a concern (though typically these IDs are internal).
*   **Principle of Least Privilege:** Ensure database users and application roles have only the necessary permissions.

This detailed design provides a solid foundation for building a robust, efficient, and intelligent Memory MCP Server.
