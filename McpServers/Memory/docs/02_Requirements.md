# Requirements

This consolidated document merges the following original files:

- FunctionalRequirements.md
- MemorySearchFunctionalRequirements.md
- FactExtraction.md

---

## Functional Requirements

<details>
<summary>Full Functional Requirements</summary>

<!-- Begin FunctionalRequirements.md content -->
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

## 1.3 Key Design Principles
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
<!-- End FunctionalRequirements.md content -->

</details>  

---

## Memory Search Functional Requirements

<details>
<summary>Full Memory Search Functional Requirements</summary>

<!-- Begin MemorySearchFunctionalRequirements.md content -->
# Memory Search Functional Requirements

## Overview

The Memory Search system provides semantic search capabilities over stored memories using a hybrid approach that combines Full-Text Search (FTS5) with vector similarity search. This document outlines the functional requirements, current implementation status, and future enhancements.

## Current Implementation Status: ✅ PRODUCTION READY

### Core Search Capabilities

#### 1. Hybrid Search Architecture
- **FTS5 Integration**: Traditional keyword-based search using SQLite FTS5
- **Vector Similarity**: Semantic search using sqlite-vec with 1024/1536-dimensional embeddings
- **Hybrid Results**: Combines both approaches for comprehensive search coverage
- **Score Normalization**: Consistent scoring across different search methods

#### 2. Search Query Types

##### Domain-Specific Expertise Matching ✅
- **Requirement**: Find memories based on professional domains and expertise
- **Implementation**: Excellent performance with exact matches
- **Test Results**:
  - "quantum physicist" → Perfect match (score: 0.35)
  - "machine learning researcher" → Excellent match (score: 0.68)
  - "cloud computing infrastructure" → Perfect match (score: 0.35)
  - "quantum computing algorithms" → Perfect match (score: 0.35)

##### Multi-Term Semantic Understanding ✅
- **Requirement**: Handle complex queries with multiple related concepts
- **Implementation**: Strong semantic understanding across domains
- **Test Results**:
  - "CERN Geneva particle physics" → Comprehensive match
  - "distributed systems container orchestration" → Perfect technical match
  - "neural networks deep learning" → Accurate specialization match

##### Cross-Domain Conceptual Search ✅
- **Requirement**: Find connections between different research domains
- **Implementation**: Excellent interdisciplinary search capabilities
- **Test Results**:
  - "interdisciplinary research quantum machine learning" → Found 2 relevant researchers (scores: 0.34, 0.34)
  - "MIT PhD Stanford" → Correctly identified educational connections

##### Institutional and Geographic Search ✅
- **Requirement**: Search by organizations, institutions, and locations
- **Implementation**: Perfect institutional recognition
- **Test Results**:
  - "Boston Dynamics" → Perfect match (score: 0.35)
  - All geographic searches work correctly (Geneva, Seattle, Palo Alto, etc.)

#### 3. Search Performance Metrics

##### Score Distribution Analysis ✅
- **High Relevance**: 0.35-0.68 for exact domain matches
- **Medium Relevance**: 0.17-0.34 for related/secondary matches
- **Consistent Scoring**: Similar concepts receive similar scores
- **Threshold Effectiveness**: 0.1 threshold captures relevant results without noise

##### Hybrid Search Effectiveness ✅
- **Traditional + Vector Results**: Optimal combination based on query type
- **Result Diversity**: Multiple result sources ensure comprehensive coverage
- **Performance Logs**:
  - "Kevin Chen": 3 traditional + 1 vector = 3 total results
  - "Sarah Johnson": 3 traditional + 0 vector = 3 total results
  - "Amanda Rodriguez": 3 traditional + 1 vector = 3 total results

#### 4. Technical Architecture

##### Database Integration ✅
- **Memory Storage**: Core memories table with version tracking
- **Vector Storage**: Dedicated embeddings table with BLOB storage
- **FTS5 Integration**: Full-text search index for keyword matching
- **Graph Integration**: Entity and relationship tables for knowledge graph

##### Session Management ✅
- **Multi-Tenant Support**: User/Agent/Run ID isolation
- **Session Context**: Proper filtering by session parameters
- **Data Isolation**: Complete separation between different sessions

##### API Interface ✅
- **MCP Tool Integration**: Standard Model Context Protocol interface
- **Parameter Validation**: Comprehensive input validation
- **Error Handling**: Graceful error responses with meaningful messages
- **Result Formatting**: Consistent JSON response structure

## Current Performance Rating: 8.5/10 ⭐

### Strengths
- ✅ Excellent domain-specific expertise matching
- ✅ Strong multi-term semantic understanding
- ✅ Perfect institutional and location recognition
- ✅ Effective cross-domain conceptual searches
- ✅ Robust hybrid FTS5 + vector similarity integration
- ✅ Production-ready performance and reliability

### Areas for Enhancement
- 🔄 Abstract relationship queries need improvement
- 🔄 Collaborative pattern recognition requires enhancement
- 🔄 Complex multi-hop relationship searches need development

## Yet to Implement - Future Enhancements

### 1. Relationship-Aware Search 🚧

#### Problem Statement
Current search struggles with abstract relationship queries:
- "researchers who collaborate" → No results (should find multiple researchers)
- "collaboration research papers" → No results (should find cross-references)

#### Proposed Solution
- **Graph-Integrated Search**: Leverage the rich graph data (43 entities, 26 relationships)
- **Relationship Query Parser**: Detect relationship-focused queries
- **Multi-Hop Search**: Traverse entity relationships for complex queries
- **Collaboration Detection**: Identify collaborative patterns in stored memories

#### Implementation Requirements
- Extend search query parser to detect relationship keywords
- Implement graph traversal algorithms for relationship queries
- Create relationship-specific scoring mechanisms
- Add relationship result formatting
<!-- End MemorySearchFunctionalRequirements.md content -->

</details>  

---

## Fact Extraction

<details>
<summary>Full Fact Extraction Requirements</summary>

<!-- Begin FactExtraction.md content -->
# Fact Extraction Engine - Enhanced with Database Session Pattern

## Overview

The Fact Extraction Engine is responsible for intelligently parsing conversations and extracting meaningful facts that should be stored in memory. It uses sophisticated LLM prompting strategies to identify personal information, preferences, plans, and other relevant details from natural language conversations. Enhanced with Database Session Pattern integration, it ensures reliable resource management and session-scoped fact extraction.

**ARCHITECTURE ENHANCEMENT**: This design has been updated to integrate with the Database Session Pattern, providing session-aware fact extraction operations and reliable resource management for AI-powered conversation analysis.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│             Fact Extraction Engine (Enhanced)               │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Message   │  │   Fact      │  │     Prompt          │  │
│  │  Processor  │  │ Extractor   │  │   Manager           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Session Integration Layer                    │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ Session     │  │ Context     │  │   Memory            │  │
│  │ Scoped      │  │ Resolver    │  │  Repository         │  │
│  │ Extraction  │  │             │  │  Integration        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Processing Pipeline                      │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Parse     │  │  Extract    │  │     Validate        │  │
│  │  Messages   │  │   Facts     │  │     Facts           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Fact Categories                          │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Personal   │  │ Preferences │  │      Plans          │  │
│  │   Details   │  │             │  │   & Intentions      │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Core Components

### 1. FactExtractor (Main Class) with Session Support

**Purpose**: Orchestrates the entire fact extraction process from raw conversation messages to validated, structured facts with session-scoped database operations.

**Core Responsibilities**:
- Message preprocessing and normalization with session context
- LLM-based fact extraction with sophisticated prompting and session awareness
- Fact validation and quality control within session boundaries
- Context-aware extraction based on session information and historical data
- Multi-language support and localization with session preferences
- Integration with Database Session Pattern for reliable resource management

**Session-Enhanced Interface**:
```csharp
public interface IFactExtractor
{
    Task<FactExtractionResult> ExtractFactsAsync(
        ISqliteSession session,
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default);
    
    Task<FactExtractionResult> ExtractFactsWithHistoryAsync(
        ISqliteSession session,
        IEnumerable<Message> messages,
        MemoryContext sessionContext,
        int historyLimit = 10,
        CancellationToken cancellationToken = default);
    
    Task<ValidationResult> ValidateFactsAsync(
        ISqliteSession session,
        IEnumerable<string> facts,
        MemoryContext sessionContext,
        CancellationToken cancellationToken = default);
}
```

**Processing Flow with Session Pattern**:
1. **Message Validation**: Ensure input messages are properly formatted and contain extractable content
2. **Session Context Building**: Incorporate session context, user preferences, and historical data from database session
3. **Prompt Construction**: Build sophisticated prompts with session-aware examples and guidelines
4. **LLM Interaction**: Execute structured fact extraction using configured LLM provider with session context
5. **Response Processing**: Parse and validate LLM responses for fact lists
6. **Session-Aware Quality Assurance**: Apply validation rules and filtering within session scope
<!-- End FactExtraction.md content -->

</details>  
