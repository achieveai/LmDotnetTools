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
```
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
