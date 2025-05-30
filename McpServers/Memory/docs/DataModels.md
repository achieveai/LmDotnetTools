# Data Models and Schemas - Detailed Design

## Overview

This document defines all data structures, schemas, and models used throughout the memory system. These specifications ensure consistency across components and provide clear contracts for implementation in any programming language.

## Core Data Models

### 1. Memory Record

**Purpose**: Represents a stored memory with all associated metadata and content.

**Schema**:
```json
{
  "id": "string (UUID)",
  "content": "string",
  "embedding": "array<float>",
  "metadata": {
    "user_id": "string",
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
  "id": "550e8400-e29b-41d4-a716-446655440000",
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

### 2. Session Context

**Purpose**: Defines the session scope for memory operations and access control.

**Schema**:
```json
{
  "user_id": "string (required)",
  "agent_id": "string (optional)",
  "run_id": "string (optional)",
  "session_type": "string (enum: USER, AGENT, RUN)",
  "permissions": {
    "read": "boolean",
    "write": "boolean",
    "delete": "boolean"
  },
  "metadata": "object (optional)"
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

This comprehensive data model specification ensures that implementers have clear, unambiguous definitions for all data structures used throughout the memory system, enabling consistent implementation across different programming languages and platforms. 