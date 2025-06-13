# Memory Server - LLM Libraries Integration Design

## Executive Summary

This document outlines the comprehensive integration of the Memory server with the existing LmDotnetTools ecosystem, specifically focusing on **LmConfig** for centralized model configuration and **LmEmbeddings** for semantic vector search capabilities. This integration transforms the Memory server from a simple text-based search system into a sophisticated semantic memory system with hybrid search capabilities.

## Current State Analysis

### Existing Memory Server Architecture

The Memory server currently implements:
- **Text-based search**: SQLite FTS5 full-text search and LIKE queries
- **Basic LLM integration**: Direct use of OpenAIProvider and AnthropicProvider for graph extraction
- **Hard-coded configuration**: Provider selection through simple configuration switches
- **Session-based isolation**: Multi-tenant memory storage with session context
- **Database Session Pattern**: Reliable SQLite connection management

### Integration Gaps

1. **No Vector Embeddings**: Memory search is purely text-based, missing semantic similarity
2. **No Centralized Configuration**: Model selection is hard-coded, not using LmConfig capabilities
3. **Limited Search Quality**: Cannot find semantically similar memories with different wording
4. **Missed Standardization**: Not leveraging existing embedding infrastructure

## Integration Architecture

### High-Level Integration Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                Memory Server (Enhanced)                     │
├─────────────────────────────────────────────────────────────┤
│                    API Layer                                │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Memory    │  │   Hybrid    │  │   Session           │  │
│  │   Service   │  │   Search    │  │  Management         │  │
│  │             │  │  Service    │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│            LLM Integration Layer (NEW)                      │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   LmConfig  │  │ LmEmbeddings│  │      Agent          │  │
│  │Integration  │  │ Integration │  │   Management        │  │
│  │   Service   │  │   Service   │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│              Storage Layer (Enhanced)                       │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  Memory     │  │   Vector    │  │    SQLite           │  │
│  │ Repository  │  │ Embeddings  │  │   Database          │  │
│  │ (Enhanced)  │  │   Table     │  │  (FTS5 + Vec)       │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                Provider Layer                               │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   OpenAI    │  │  Anthropic  │  │    LmCore           │  │
│  │ Provider    │  │  Provider   │  │   Foundation        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Component Integration Design

### 1. LmConfig Integration

**Purpose**: Centralize model configuration and provider selection

**Integration Points**:
- Replace hard-coded provider selection with LmConfig-based model resolution
- Support dynamic model switching based on capabilities and cost
- Integrate model metadata for optimal provider selection

**New Components**:

#### 1.1 LmConfigService
```csharp
public interface ILmConfigService
{
    Task<ModelConfig?> GetOptimalModelAsync(string capability, CancellationToken cancellationToken = default);
    Task<IAgent> CreateAgentAsync(string capability, CancellationToken cancellationToken = default);
    Task<IEmbeddingService> CreateEmbeddingServiceAsync(CancellationToken cancellationToken = default);
    AppConfig GetConfiguration();
}
```

**Key Features**:
- Model capability-based selection (e.g., "chat", "embedding", "reasoning")
- Cost-aware model selection with fallback strategies
- Dynamic provider instantiation based on configuration
- Integration with existing agent creation patterns

#### 1.2 Enhanced Configuration Structure
```yaml
# Enhanced appsettings.json structure
MemoryServer:
  LmConfig:
    ConfigPath: "config/models.json"
    DefaultCapabilities:
      Chat: "gpt-4o"
      Embedding: "text-embedding-3-small"
      Reasoning: "claude-3-haiku"
    FallbackStrategy: "cost-optimized" # or "performance-first"
    CostLimits:
      MaxCostPerRequest: 0.01
      DailyCostLimit: 10.00
```

### 2. LmEmbeddings Integration

**Purpose**: Add semantic vector search capabilities to memory operations

**Integration Points**:
- Generate embeddings for all memory content during storage
- Implement vector similarity search alongside existing FTS5 search
- Support multiple embedding providers through LmEmbeddings abstraction

**New Components**:

#### 2.1 Memory Embedding Service
```csharp
public interface IMemoryEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default);
    Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> contents, CancellationToken cancellationToken = default);
    Task<List<VectorSearchResult>> SearchSimilarAsync(float[] queryEmbedding, SessionContext sessionContext, int limit = 10, float threshold = 0.7f, CancellationToken cancellationToken = default);
    int EmbeddingDimensions { get; }
}
```

#### 2.2 Enhanced Database Schema
```sql
-- Add vector embeddings table
CREATE TABLE memory_embeddings (
    memory_id INTEGER PRIMARY KEY,
    embedding BLOB NOT NULL,
    embedding_model TEXT NOT NULL,
    created_at DATETIME NOT NULL,
    FOREIGN KEY (memory_id) REFERENCES memories (id) ON DELETE CASCADE
);

-- Add vector index using sqlite-vec
-- Note: sqlite-vec extension supports vector similarity search
CREATE INDEX idx_memory_embeddings_vector ON memory_embeddings USING vec_search(embedding);
```

### 3. Hybrid Search Service

**Purpose**: Combine text-based and semantic search for optimal memory retrieval

**Integration Strategy**:
- Parallel execution of FTS5 and vector search
- Intelligent result fusion based on search context
- Configurable search strategies (text-only, vector-only, hybrid)

#### 3.1 Hybrid Search Implementation
```csharp
public interface IHybridSearchService
{
    Task<List<Memory>> SearchAsync(SearchRequest request, SessionContext sessionContext, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchTextAsync(string query, SessionContext sessionContext, SearchOptions options, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchSemanticAsync(string query, SessionContext sessionContext, SearchOptions options, CancellationToken cancellationToken = default);
    Task<List<Memory>> SearchHybridAsync(string query, SessionContext sessionContext, SearchOptions options, CancellationToken cancellationToken = default);
}

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public SearchMode Mode { get; set; } = SearchMode.Hybrid;
    public int Limit { get; set; } = 10;
    public float SemanticThreshold { get; set; } = 0.7f;
    public float TextRelevanceWeight { get; set; } = 0.3f;
    public float SemanticRelevanceWeight { get; set; } = 0.7f;
}

public enum SearchMode
{
    Text,
    Semantic,
    Hybrid
}
```

## Database Design Updates

### Enhanced Schema Design

```sql
-- Existing memories table (unchanged)
CREATE TABLE memories (
    id INTEGER PRIMARY KEY,
    content TEXT NOT NULL,
    user_id TEXT NOT NULL,
    agent_id TEXT,
    run_id TEXT,
    metadata TEXT,
    created_at DATETIME NOT NULL,
    updated_at DATETIME NOT NULL,
    version INTEGER NOT NULL DEFAULT 1
);

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

-- New: Embedding cache table (for performance)
CREATE TABLE embedding_cache (
    content_hash TEXT PRIMARY KEY,
    embedding BLOB NOT NULL,
    embedding_model TEXT NOT NULL,
    embedding_dimension INTEGER NOT NULL,
    access_count INTEGER NOT NULL DEFAULT 1,
    created_at DATETIME NOT NULL,
    last_accessed DATETIME NOT NULL
);

-- Indexes for optimal performance
CREATE INDEX idx_memories_session ON memories (user_id, agent_id, run_id);
CREATE INDEX idx_memories_updated ON memories (updated_at);
CREATE INDEX idx_embedding_cache_model ON embedding_cache (embedding_model);
CREATE INDEX idx_embedding_cache_accessed ON embedding_cache (last_accessed);

-- FTS5 virtual table (existing)
CREATE VIRTUAL TABLE memories_fts USING fts5(
    content,
    content_rowid=id,
    tokenize='porter'
);
```

## Success Metrics

### Performance Metrics
- **Search Quality**: Measure precision and recall improvements with semantic search
- **Response Time**: Monitor latency impact of embedding generation and search
- **Cost Efficiency**: Track LLM provider costs and optimize through LmConfig
- **System Reliability**: Monitor error rates and recovery capabilities

### Business Metrics
- **User Satisfaction**: Measure improvement in memory search relevance
- **Feature Adoption**: Track usage of new hybrid search capabilities
- **System Scalability**: Monitor performance under increased load
- **Developer Experience**: Assess ease of configuration and maintenance

## Implementation Strategy

### Phase 1: Project Dependencies (Week 1)
1. **Update MemoryServer.csproj**: Add LmConfig and LmEmbeddings references
2. **Configuration Setup**: Integrate LmConfig loading and validation
3. **Service Registration**: Update DI container with new services

### Phase 2: Database Schema Enhancement (Week 2)
1. **Migration Scripts**: Add embedding tables and indexes
2. **Repository Updates**: Enhance MemoryRepository with embedding operations
3. **Session Pattern Integration**: Ensure embedding operations use Database Session Pattern

### Phase 3: Service Layer Integration (Week 3-4)
1. **LmConfig Service**: Implement centralized model management
2. **Embedding Service**: Integrate LmEmbeddings with memory operations
3. **Hybrid Search**: Implement combined text and semantic search

### Phase 4: Testing and Optimization (Week 5)
1. **Unit Tests**: Comprehensive testing of new integration points
2. **Integration Tests**: End-to-end testing with real providers
3. **Performance Optimization**: Embedding caching and batch processing

## Conclusion

This integration design transforms the Memory server from a basic text search system into a sophisticated semantic memory platform. By leveraging LmConfig for intelligent model management and LmEmbeddings for semantic search capabilities, we create a production-ready system that provides enhanced search quality, centralized configuration, hybrid capabilities, and future-proof architecture. 