# Memory MCP Server - Functional Requirements Document

## Overview

This document defines the functional requirements for implementing a Memory MCP (Model Context Protocol) server in C#. The server exposes memory management capabilities as MCP tools that can be consumed by AI applications and agents for sophisticated memory operations.

**ARCHITECTURE UPDATE**: These requirements have been enhanced to include the Database Session Pattern for reliable SQLite connection management, ensuring robust resource cleanup, proper test isolation, and production-ready reliability.

## 1. System Context

### 1.1 Purpose
The Memory MCP Server provides intelligent memory management capabilities through the Model Context Protocol, enabling AI applications to store, retrieve, and manage contextual information with advanced semantic understanding and reliable database operations.

### 1.2 Architecture Context
- **Protocol**: Model Context Protocol (MCP) with SSE (Server-Sent Events) transport
- **Language**: C# (.NET 9.0)
- **LLM Providers**: OpenAI, Anthropic (existing providers in workspace)
- **Storage**: SQLite with sqlite-vec extension for vector operations and FTS5 for full-text search
- **Database Management**: Database Session Pattern for reliable connection lifecycle management
- **Data Access**: Microsoft.Data.Sqlite with session-based connection management (no Entity Framework)
- **Design Pattern**: Modular architecture with clear separation of concerns and robust resource management

### 1.3 Key Design Principles
- **Session Isolation**: Multi-tenant memory spaces with strict data separation
- **Intelligent Processing**: AI-powered fact extraction and memory decision making
- **Production Ready**: Comprehensive error handling, monitoring, and performance optimization
- **Reliable Resource Management**: Database Session Pattern ensuring proper connection cleanup
- **Type Safety**: Full type annotations and validation throughout
- **SQLite-First**: Leverage SQLite's advanced features including vector search, full-text indexing, and graph traversals
- **LLM-Friendly IDs**: Use integer IDs instead of UUIDs for better LLM comprehension and token efficiency
- **Test Isolation**: Complete separation between test runs with automatic cleanup

## 1.5. Database Session Pattern Requirements

### 1.5.1 Core Session Management
**FR-DB-001**: The system must implement ISqliteSession interface for encapsulated connection lifecycle management
**FR-DB-002**: The system must implement ISqliteSessionFactory interface for creating database sessions
**FR-DB-003**: All database operations must be performed through session-scoped connections
**FR-DB-004**: Sessions must automatically handle WAL checkpoint operations during disposal
**FR-DB-005**: Sessions must provide both transactional and non-transactional operation methods

### 1.5.2 Resource Management
**FR-DB-006**: All SQLite connections must be properly disposed with guaranteed cleanup
**FR-DB-007**: WAL and SHM files must be properly cleaned up during session disposal
**FR-DB-008**: Connection leaks must be prevented through proper resource management
**FR-DB-009**: The system must detect and alert on connection leaks in monitoring
**FR-DB-010**: Session creation must complete within 100ms under normal conditions

### 1.5.3 Test Environment Isolation
**FR-DB-011**: Test sessions must use unique database files for complete isolation
**FR-DB-012**: Test database files must be automatically cleaned up after test completion
**FR-DB-013**: Tests must not interfere with each other through shared database resources
**FR-DB-014**: Test session factory must create isolated database instances
**FR-DB-015**: Test cleanup must be deterministic and complete within 1 second

### 1.5.4 Production Environment Reliability
**FR-DB-016**: Production sessions must support connection pooling for performance
**FR-DB-017**: Production sessions must handle connection failures with retry logic
**FR-DB-018**: Production sessions must support concurrent access with proper locking
**FR-DB-019**: Production sessions must monitor and report connection health
**FR-DB-020**: Production sessions must support graceful shutdown with resource cleanup

## 2. Session Management and Defaults

### 2.1 Session Context Hierarchy
The system supports a hierarchical approach to session context with the following precedence:
1. **Explicit Parameters**: Parameters provided directly in tool calls
2. **HTTP Headers**: Default session context provided via HTTP headers
3. **Session Initialization**: Default context set during MCP session establishment
4. **System Defaults**: Fallback defaults configured at the server level

### 2.2 HTTP Header Support
**Supported Headers**:
- `X-Memory-User-ID`: Default user identifier for the session
- `X-Memory-Agent-ID`: Default agent identifier for the session  
- `X-Memory-Run-ID`: Default run identifier for the session
- `X-Memory-Session-Metadata`: JSON object with additional session metadata

**Header Processing Requirements**:
- FR-001: HTTP headers must be processed during MCP connection establishment
- FR-002: Header values must be validated using the same rules as explicit parameters
- FR-003: Headers must be cached for the duration of the MCP session
- FR-004: Invalid header values must result in connection rejection with clear error messages

### 2.3 Session Initialization
**Initialization Tool**: `memory_init_session`
**Description**: Establishes default session context for subsequent operations

**Parameters**:
- `user_id` (optional): Default user identifier for the session
- `agent_id` (optional): Default agent identifier for the session
- `run_id` (optional): Default run identifier for the session
- `metadata` (optional): Additional session metadata

**Returns**:
- Session configuration confirmation
- Active defaults summary

**Functional Requirements**:
- FR-005: Session initialization must persist defaults for the MCP connection lifetime
- FR-006: Subsequent tool calls can override defaults with explicit parameters
- FR-007: Session defaults must be included in audit logs and operation tracking

## 3. Core MCP Tools

### 3.1 Memory Storage Tools

#### 3.1.1 AddMemory Tool
**Tool Name**: `memory_add`
**Description**: Adds new memories from conversation messages or direct content

**Parameters**:
- `messages` (required): Array of conversation messages or string content
- `user_id` (optional): User identifier for session isolation (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session isolation (uses session default if not provided)
- `run_id` (optional): Run identifier for session isolation (uses session default if not provided)
- `metadata` (optional): Additional metadata to attach to memories
- `custom_prompt` (optional): Custom prompt for fact extraction
- `mode` (optional): Processing mode ("inference" or "direct", default: "inference")

**Returns**:
- Array of created memory objects with integer IDs, content, and metadata
- Success/failure status and error details if applicable

**Functional Requirements**:
- FR-008: Extract facts from conversation messages using LLM providers
- FR-009: Generate embeddings for semantic similarity search using sqlite-vec
- FR-010: Store memories with session isolation in SQLite tables using Database Session Pattern
- FR-011: Support both inference mode (AI-powered) and direct mode
- FR-012: Validate input parameters and handle malformed data gracefully
- FR-013: Return structured response with created memory integer IDs
- FR-014: Index memory content in FTS5 virtual table for full-text search
- FR-015: Use session defaults when explicit session parameters are not provided
- **FR-016**: All database operations must use session-scoped connections with proper cleanup

#### 3.1.2 SearchMemory Tool
**Tool Name**: `memory_search`
**Description**: Searches for relevant memories using semantic similarity and full-text search

**Parameters**:
- `query` (required): Search query text
- `user_id` (optional): User identifier for session filtering (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session filtering (uses session default if not provided)
- `run_id` (optional): Run identifier for session filtering (uses session default if not provided)
- `limit` (optional): Maximum number of results (default: 100, max: 100)
- `include_metadata` (optional): Include memory metadata in results (default: true)
- `search_type` (optional): Search type ("vector", "text", "hybrid", default: "hybrid")

**Returns**:
- Array of relevant memory objects with relevance scores
- Memory content, metadata, and creation timestamps
- Total count and search performance metrics

**Functional Requirements**:
- FR-017: Generate embeddings for search query using configured embedding provider
- FR-018: Perform semantic similarity search using sqlite-vec K-NN operations
- FR-019: Perform full-text search using FTS5 MATCH operations
- FR-020: Support hybrid search combining vector and text search results
- FR-021: Return results ranked by relevance score with session filtering
- FR-022: Include memory metadata and timestamps
- FR-023: Enforce session isolation in search results using SQL WHERE clauses
- FR-024: Handle empty search results gracefully
- FR-025: Use session defaults when explicit session parameters are not provided
- **FR-026**: All search operations must use session-scoped database connections

#### 3.1.3 GetAllMemories Tool
**Tool Name**: `memory_get_all`
**Description**: Retrieves all memories for a specific session

**Parameters**:
- `user_id` (optional): User identifier for session filtering (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session filtering (uses session default if not provided)
- `run_id` (optional): Run identifier for session filtering (uses session default if not provided)
- `limit` (optional): Maximum number of results (default: 100, max: 100)
- `offset` (optional): Pagination offset (default: 0)

**Returns**:
- Array of all memory objects in the session
- Pagination metadata (total count, has_more)

**Functional Requirements**:
- FR-027: Retrieve all memories within session boundaries using SQL queries
- FR-028: Support pagination using LIMIT and OFFSET clauses
- FR-029: Return memories sorted by creation timestamp (newest first)
- FR-030: Include memory metadata and session information
- FR-031: Use session defaults when explicit session parameters are not provided
- **FR-032**: All retrieval operations must use session-scoped database connections

### 3.2 Memory Management Tools

#### 3.2.1 UpdateMemory Tool
**Tool Name**: `memory_update`
**Description**: Updates existing memory content with intelligent merging

**Parameters**:
- `memory_id` (required): Integer ID of the memory to update
- `data` (required): New content or update instructions
- `user_id` (optional): User identifier for session validation (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session validation (uses session default if not provided)
- `run_id` (optional): Run identifier for session validation (uses session default if not provided)

**Returns**:
- Updated memory object with new content and metadata
- Update operation details and success status

**Functional Requirements**:
- FR-033: Validate memory ownership within session boundaries using SQL joins
- FR-034: Use LLM providers to intelligently merge content
- FR-035: Update embeddings in sqlite-vec when content changes significantly
- FR-036: Update FTS5 index when memory content changes
- FR-037: Maintain update history for audit trail in SQLite tables
- FR-038: Handle concurrent update conflicts using SQLite transactions
- FR-039: Use session defaults when explicit session parameters are not provided
- **FR-040**: All update operations must use transactional session-scoped connections

#### 3.2.2 DeleteMemory Tool
**Tool Name**: `memory_delete`
**Description**: Removes specific memories from the system

**Parameters**:
- `memory_id` (required): Integer ID of the memory to delete
- `user_id` (optional): User identifier for session validation (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session validation (uses session default if not provided)
- `run_id` (optional): Run identifier for session validation (uses session default if not provided)

**Returns**:
- Deletion confirmation with memory details
- Success/failure status

**Functional Requirements**:
- FR-041: Validate memory ownership before deletion using SQL queries
- FR-042: Remove memory from SQLite tables including vector and FTS5 indexes
- FR-043: Log deletion operations for audit trail
- FR-044: Handle deletion of non-existent memories gracefully
- FR-045: Use session defaults when explicit session parameters are not provided
- **FR-046**: All deletion operations must use transactional session-scoped connections

#### 3.2.3 DeleteAllMemories Tool
**Tool Name**: `memory_delete_all`
**Description**: Removes all memories for a specific session

**Parameters**:
- `user_id` (optional): User identifier for session targeting (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session targeting (uses session default if not provided)
- `run_id` (optional): Run identifier for session targeting (uses session default if not provided)
- `confirm` (required): Confirmation flag to prevent accidental deletion

**Returns**:
- Deletion summary with count of removed memories
- Success/failure status

**Functional Requirements**:
- FR-047: Require explicit confirmation for bulk deletion
- FR-048: Delete all memories within session boundaries using SQL WHERE clauses
- FR-049: Provide summary of deletion operation
- FR-050: Log bulk deletion operations for audit trail
- FR-051: Use session defaults when explicit session parameters are not provided
- **FR-052**: All bulk deletion operations must use transactional session-scoped connections

### 3.3 History and Analytics Tools

#### 3.3.1 GetHistory Tool
**Tool Name**: `memory_get_history`
**Description**: Retrieves memory operation history for debugging and analysis

**Parameters**:
- `user_id` (optional): User identifier for session filtering (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session filtering (uses session default if not provided)
- `run_id` (optional): Run identifier for session filtering (uses session default if not provided)
- `limit` (optional): Maximum number of history entries (default: 50, max: 100)

**Returns**:
- Array of memory operations with timestamps and details
- Operation types (add, update, delete, search)

**Functional Requirements**:
- FR-053: Track all memory operations in SQLite audit tables
- FR-054: Include operation type, timestamp, and affected memory IDs
- FR-055: Support filtering by session identifiers using SQL queries
- FR-056: Provide operation status and error details
- FR-057: Use session defaults when explicit session parameters are not provided
- **FR-058**: All history operations must use session-scoped database connections

#### 3.3.2 GetStats Tool
**Tool Name**: `memory_get_stats`
**Description**: Provides memory usage statistics and analytics

**Parameters**:
- `user_id` (optional): User identifier for session filtering (uses session default if not provided)
- `agent_id` (optional): Agent identifier for session filtering (uses session default if not provided)
- `run_id` (optional): Run identifier for session filtering (uses session default if not provided)

**Returns**:
- Memory count statistics and storage usage
- Operation frequency and performance metrics

**Functional Requirements**:
- FR-059: Calculate memory count per session using SQL aggregate functions
- FR-060: Provide storage usage statistics from SQLite database size
- FR-061: Include operation performance metrics from audit tables
- FR-062: Support session-level analytics using SQL GROUP BY operations
- FR-063: Use session defaults when explicit session parameters are not provided
- **FR-064**: All statistics operations must use session-scoped database connections

## 4. Non-Functional Requirements

### 4.1 Performance Requirements
- **NFR-001**: Memory search operations must complete within 500ms for typical queries
- **NFR-002**: Memory addition operations must complete within 1000ms
- **NFR-003**: System must support 100 concurrent memory operations
- **NFR-004**: Vector embeddings must be cached to reduce API costs
- **NFR-005**: SQLite database must be optimized with appropriate indexes and PRAGMA settings
- **NFR-006**: Integer IDs must provide better performance than UUIDs for LLM operations
- **NFR-007**: Database session creation must complete within 100ms under normal conditions
- **NFR-008**: Session disposal must complete within 500ms including WAL checkpoint
- **NFR-009**: Connection leak detection must identify leaks within 1 minute

### 4.2 Reliability Requirements
- **NFR-010**: System must gracefully handle LLM provider API failures
- **NFR-011**: SQLite database failures must not cause data loss
- **NFR-012**: Invalid input must be validated and rejected with clear error messages
- **NFR-013**: All operations must support cancellation tokens
- **NFR-014**: SQLite transactions must ensure data consistency
- **NFR-015**: Session defaults must be reliably maintained throughout MCP connection lifetime
- **NFR-016**: Database sessions must guarantee resource cleanup on disposal
- **NFR-017**: WAL checkpoint operations must complete successfully during session disposal
- **NFR-018**: Connection failures must trigger automatic retry with exponential backoff
- **NFR-019**: Test isolation must be 100% reliable with no cross-test interference

### 4.3 Security Requirements
- **NFR-020**: Session isolation must be strictly enforced through SQL queries
- **NFR-021**: Memory access must be validated against session identifiers
- **NFR-022**: Sensitive data must be handled according to privacy requirements
- **NFR-023**: API keys and configuration must be securely managed
- **NFR-024**: SQLite database file must be protected with appropriate file permissions
- **NFR-025**: HTTP headers containing session defaults must be validated and sanitized
- **NFR-026**: Database connections must use parameterized queries to prevent SQL injection
- **NFR-027**: Connection strings must be securely stored and encrypted where necessary

### 4.4 Monitoring Requirements
- **NFR-028**: All operations must be logged with appropriate detail levels
- **NFR-029**: Performance metrics must be tracked and available
- **NFR-030**: Error rates and failure patterns must be monitored
- **NFR-031**: LLM provider usage and costs must be tracked
- **NFR-032**: SQLite database performance metrics must be monitored
- **NFR-033**: Session default usage and override patterns must be tracked
- **NFR-034**: Database connection lifecycle must be monitored and logged
- **NFR-035**: Connection leak detection must be implemented with alerting
- **NFR-036**: WAL checkpoint frequency and performance must be monitored

## 5. Integration Requirements

### 5.1 LLM Provider Integration
- **INT-001**: Integrate with existing OpenAI provider in workspace
- **INT-002**: Integrate with existing Anthropic provider in workspace
- **INT-003**: Support fallback between providers for reliability
- **INT-004**: Implement structured output parsing for both providers
- **INT-005**: Optimize prompts for integer ID usage instead of UUIDs

### 5.2 SQLite Storage Integration
- **INT-006**: Integrate with SQLite using Microsoft.Data.Sqlite
- **INT-007**: Load and configure sqlite-vec extension for vector operations
- **INT-008**: Configure FTS5 for full-text search capabilities
- **INT-009**: Implement Database Session Pattern for connection management
- **INT-010**: Support database creation and schema migration
- **INT-011**: Implement backup and recovery mechanisms
- **INT-012**: Use integer primary keys for optimal performance and LLM compatibility
- **INT-013**: Implement session factory pattern for production and test environments
- **INT-014**: Support WAL mode with proper checkpoint management

### 5.3 MCP Protocol Integration
- **INT-015**: Implement SSE-based MCP server transport
- **INT-016**: Support standard MCP tool discovery and listing
- **INT-017**: Handle MCP client connections and disconnections
- **INT-018**: Provide proper MCP error responses and status codes
- **INT-019**: Process HTTP headers for session defaults during connection establishment
- **INT-020**: Support session initialization through dedicated MCP tool

## 6. Data Requirements

### 6.1 Memory Data Model
- **DATA-001**: Memories must have unique integer identifiers stored in SQLite
- **DATA-002**: Memories must include content, embeddings, and metadata in relational tables
- **DATA-003**: Memories must track creation and update timestamps
- **DATA-004**: Memories must be associated with session identifiers for isolation
- **DATA-005**: Vector embeddings must be stored in sqlite-vec virtual tables
- **DATA-006**: Memory content must be indexed in FTS5 virtual tables
- **DATA-007**: Integer IDs must be auto-incrementing for optimal LLM usage

### 6.2 Session Data Model
- **DATA-008**: Sessions must support user_id, agent_id, and run_id in SQLite tables
- **DATA-009**: Session boundaries must be strictly enforced through SQL constraints
- **DATA-010**: Session metadata must be tracked and available
- **DATA-011**: Historical session data must be preserved in audit tables
- **DATA-012**: Session defaults must be stored and retrievable per MCP connection
- **DATA-013**: HTTP header values must be validated and stored securely

### 6.3 Configuration Data Model
- **DATA-014**: LLM provider configurations must be externalized
- **DATA-015**: SQLite database configuration must be environment-specific
- **DATA-016**: Embedding model selection must be configurable
- **DATA-017**: Prompt templates must be customizable
- **DATA-018**: SQLite PRAGMA settings must be configurable for performance tuning
- **DATA-019**: Session default policies must be configurable
- **DATA-020**: Database session configuration must support production and test environments

## 7. Error Handling Requirements

### 7.1 Input Validation
- **ERR-001**: Invalid parameters must return descriptive error messages
- **ERR-002**: Missing required parameters must be clearly identified
- **ERR-003**: Parameter type mismatches must be gracefully handled
- **ERR-004**: Session validation failures must be reported appropriately
- **ERR-005**: Invalid HTTP headers must result in connection rejection
- **ERR-006**: Integer ID validation must ensure positive values and reasonable ranges

### 7.2 External Service Failures
- **ERR-007**: LLM provider failures must trigger fallback mechanisms
- **ERR-008**: SQLite database failures must be retried with exponential backoff
- **ERR-009**: Network timeouts must be handled gracefully
- **ERR-010**: Rate limiting must be respected and handled appropriately
- **ERR-011**: sqlite-vec extension loading failures must be handled gracefully
- **ERR-012**: Database session creation failures must be retried with backoff
- **ERR-013**: Connection disposal failures must be logged but not block operations

### 7.3 Data Consistency
- **ERR-014**: Concurrent access conflicts must be detected and resolved using SQLite transactions
- **ERR-015**: Partial failures must be rolled back appropriately using SQLite transactions
- **ERR-016**: Data corruption must be detected and reported using SQLite integrity checks
- **ERR-017**: Inconsistent state must trigger recovery procedures
- **ERR-018**: Session default conflicts must be resolved with clear precedence rules
- **ERR-019**: Database session disposal must handle WAL checkpoint failures gracefully
- **ERR-020**: Connection leak detection must trigger alerts and cleanup procedures

## 8. Testing Requirements

### 8.1 Unit Testing
- **TEST-001**: All core memory operations must have unit tests
- **TEST-002**: Error conditions must be tested thoroughly
- **TEST-003**: Session isolation must be validated in tests
- **TEST-004**: Mock providers must be used for LLM and embedding services
- **TEST-005**: SQLite operations must be tested with in-memory databases
- **TEST-006**: Session default handling must be thoroughly tested
- **TEST-007**: HTTP header processing must be validated in tests
- **TEST-008**: Database session lifecycle must be tested with proper cleanup validation
- **TEST-009**: Connection leak detection must be tested and validated
- **TEST-010**: WAL checkpoint operations must be tested in session disposal

### 8.2 Integration Testing
- **TEST-011**: End-to-end MCP tool invocation must be tested
- **TEST-012**: Real LLM provider integration must be validated
- **TEST-013**: SQLite vector and text search operations must be tested with real data
- **TEST-014**: Performance benchmarks must be established for SQLite operations
- **TEST-015**: Session default precedence must be tested across all tools
- **TEST-016**: Integer ID generation and usage must be validated
- **TEST-017**: Database session pattern must be tested with concurrent operations
- **TEST-018**: Test isolation must be validated with parallel test execution

### 8.3 Load Testing
- **TEST-019**: Concurrent operation handling must be tested
- **TEST-020**: Memory usage under load must be validated
- **TEST-021**: Provider rate limiting must be tested
- **TEST-022**: System stability under stress must be verified
- **TEST-023**: SQLite database performance under concurrent access must be tested
- **TEST-024**: Session default caching performance must be validated
- **TEST-025**: Database session creation and disposal performance must be benchmarked
- **TEST-026**: Connection leak prevention must be tested under high load

## 9. Success Criteria

### 9.1 Functional Success
- All MCP tools are implemented and functional
- Session isolation is working correctly with SQLite
- Memory operations complete within performance requirements
- Integration with existing LLM providers is successful
- Vector search and full-text search are working effectively
- Session defaults work seamlessly across all tools
- Integer IDs provide better LLM integration than UUIDs
- Database Session Pattern eliminates connection management issues
- Test isolation is 100% reliable with automatic cleanup

### 9.2 Quality Success
- Code coverage exceeds 80% for core functionality
- All error conditions are handled gracefully
- Performance requirements are met consistently
- Security requirements are validated and enforced
- SQLite database operations are reliable and performant
- Session default handling is robust and reliable
- Database session pattern has >95% test coverage
- No connection leaks detected in stress testing
- All tests pass reliably without file locking issues

### 9.3 Integration Success
- MCP server can be discovered and used by MCP clients
- Memory tools integrate seamlessly with AI applications
- Existing workspace LLM providers are leveraged successfully
- SQLite storage operations are reliable and performant
- Vector and text search provide accurate and fast results
- HTTP headers and session initialization work as expected
- Database sessions provide reliable connection management
- Test environment provides complete isolation between runs

## 10. Future Considerations

### 10.1 Extensibility
- Support for additional LLM providers
- Advanced memory analytics and insights using SQLite analytics
- Graph-based memory relationships using SQLite recursive CTEs
- Custom memory processing pipelines
- Enhanced session management with role-based access control
- Database session pooling optimization for high-throughput scenarios

### 10.2 Scalability
- SQLite database optimization for large datasets
- Distributed caching layers for improved performance
- Read replicas for scaling read operations
- Backup and archival strategies for long-term storage
- Horizontal scaling of session default management
- Database session factory optimization for multi-tenant scenarios

This enhanced functional requirements document provides the foundation for implementing a sophisticated Memory MCP server that uses SQLite as the primary storage solution with the Database Session Pattern for reliable connection management. The system leverages sqlite-vec for vector operations and FTS5 for full-text search while integrating with existing workspace infrastructure, ensuring robust resource management and complete test isolation. 