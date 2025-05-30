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

#### Add Operation Flow (Pseudo Code)

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

#### Search Operation Flow (Pseudo Code)

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

#### Update Operation Flow (Pseudo Code)

```
FUNCTION update_memory(memory_id, new_content, user_id, agent_id, run_id):
    // 1. Session Context & Validation
    session_context = build_session_context(user_id, agent_id, run_id)
    
    // 2. Existence & Permission Validation
    existing_memory = vector_store.get_by_id(memory_id)
    IF NOT existing_memory:
        THROW MemoryNotFoundError(memory_id)
    
    validate_memory_ownership(existing_memory, session_context)
    
    // 3. Change Tracking (Before State)
    change_record = create_change_record(
        operation="update",
        memory_id=memory_id,
        old_content=existing_memory.content,
        new_content=new_content,
        session=session_context,
        timestamp=get_current_timestamp()
    )
    
    // 4. Content Processing & Embedding Generation
    processed_content = process_content(new_content)
    new_embedding = embedding_provider.generate_embedding(processed_content)
    
    // 5. Vector Store Update
    updated_memory = vector_store.update(
        id=memory_id,
        content=processed_content,
        embedding=new_embedding,
        metadata=merge_metadata(existing_memory.metadata, get_update_metadata())
    )
    
    // 6. History Logging
    log_change_history(change_record)
    log_operation_history(
        operation="update",
        session=session_context,
        memory_id=memory_id,
        changes=change_record
    )
    
    RETURN updated_memory
END FUNCTION
```

#### Delete Operation Flow (Pseudo Code)

```
FUNCTION delete_memory(memory_id, user_id, agent_id, run_id):
    // 1. Session Context & Validation
    session_context = build_session_context(user_id, agent_id, run_id)
    
    // 2. Existence & Permission Validation
    existing_memory = vector_store.get_by_id(memory_id)
    IF NOT existing_memory:
        THROW MemoryNotFoundError(memory_id)
    
    validate_memory_ownership(existing_memory, session_context)
    
    // 3. Pre-deletion Logging
    deletion_record = create_deletion_record(
        memory_id=memory_id,
        content=existing_memory.content,
        session=session_context,
        timestamp=get_current_timestamp()
    )
    
    // 4. Vector Store Deletion
    success = vector_store.delete(memory_id)
    IF NOT success:
        THROW DeletionFailedError(memory_id)
    
    // 5. History Logging
    log_deletion_history(deletion_record)
    log_operation_history(
        operation="delete",
        session=session_context,
        memory_id=memory_id,
        deletion_record=deletion_record
    )
    
    RETURN {"id": memory_id, "status": "deleted"}
END FUNCTION
```

### 3. AsyncMemory (Asynchronous Implementation)

**Concurrency Design**:
- Full async/await support throughout the stack
- Concurrent processing of batch operations
- Non-blocking I/O for all external service calls
- Proper resource management with context managers

**Performance Optimizations**:
- Parallel embedding generation for multiple texts
- Concurrent vector store operations
- Async LLM calls with proper timeout handling
- Connection pooling for database operations

#### Async Add Operation Flow (Pseudo Code)

```
ASYNC FUNCTION add_memory_async(messages, user_id, agent_id, run_id, metadata, mode):
    // 1. Async Session Context Building
    session_context = await build_session_context_async(user_id, agent_id, run_id)
    await validate_session_permissions_async(session_context)
    
    // 2. Concurrent Message Processing
    normalized_messages = await normalize_message_format_async(messages)
    
    // 3. Parallel Processing Based on Mode
    IF mode == "inference":
        // Concurrent fact extraction and memory retrieval
        fact_extraction_task = fact_extractor.extract_facts_async(
            messages=normalized_messages,
            context=session_context
        )
        
        // Start both tasks concurrently
        extracted_facts, existing_memories = await asyncio.gather(
            fact_extraction_task,
            get_existing_memories_async(session_context, normalized_messages)
        )
        
        // Decision making with extracted facts and memories
        memory_operations = await decision_engine.decide_operations_async(
            existing_memories=existing_memories,
            new_facts=extracted_facts,
            context=session_context
        )
        
        // Execute operations concurrently where possible
        results = await execute_memory_operations_async(memory_operations, session_context)
        
    ELSE:
        // Direct mode with concurrent embedding generation
        embedding_task = embedding_provider.generate_embedding_async(messages)
        memory_record_task = create_memory_record_async(messages, metadata, session_context)
        
        embedding, memory_record = await asyncio.gather(embedding_task, memory_record_task)
        memory_record.embedding = embedding
        
        memory_id = await vector_store.insert_async(memory_record)
        results = [{"id": memory_id, "event": "ADD"}]
    
    // 4. Async History Logging (non-blocking)
    asyncio.create_task(log_operation_history_async(
        operation="add",
        session=session_context,
        results=results
    ))
    
    RETURN results
END FUNCTION
```

#### Batch Processing Flow (Pseudo Code)

```
ASYNC FUNCTION add_batch_memories(memory_requests, max_concurrency=5):
    // Create semaphore for controlling concurrency
    semaphore = asyncio.Semaphore(max_concurrency)
    
    ASYNC FUNCTION process_single_request(request):
        async with semaphore:
            try:
                result = await add_memory_async(
                    messages=request.messages,
                    user_id=request.user_id,
                    agent_id=request.agent_id,
                    run_id=request.run_id,
                    metadata=request.metadata,
                    mode=request.mode
                )
                RETURN {"success": True, "result": result, "request_id": request.id}
            except Exception as e:
                RETURN {"success": False, "error": str(e), "request_id": request.id}
    
    // Process all requests concurrently with controlled concurrency
    tasks = [process_single_request(req) for req in memory_requests]
    results = await asyncio.gather(*tasks, return_exceptions=True)
    
    // Aggregate results and handle exceptions
    successful_results = [r for r in results if r.get("success")]
    failed_results = [r for r in results if not r.get("success")]
    
    // Log batch operation results
    await log_batch_operation_async(
        total_requests=len(memory_requests),
        successful=len(successful_results),
        failed=len(failed_results)
    )
    
    RETURN {
        "successful": successful_results,
        "failed": failed_results,
        "total": len(memory_requests)
    }
END FUNCTION
```

### 4. Memory Answer System

**Purpose**: Provides question-answering capabilities based on stored memories, enabling retrieval and contextual responses.

**Core Functionality**:
- Search relevant memories based on user questions
- Generate contextual answers using stored information
- Support for temporal reasoning and fact verification
- Integration with conversation history for context

**Memory Answer Prompt**:
```
You are an expert at answering questions based on the provided memories. Your task is to provide accurate and concise answers to the questions by leveraging the information given in the memories.

Guidelines:
- Extract relevant information from the memories based on the question.
- If no relevant information is found, make sure you don't say no information is found. Instead, accept the question and provide a general response.
- Ensure that the answers are clear, concise, and directly address the question.

Here are the details of the task:
{memories}

Question: {question}
Answer:
```

**Answer Generation Flow**:
```
FUNCTION answer_question(question, user_id, agent_id, run_id):
    // 1. Search relevant memories
    session_context = build_session_context(user_id, agent_id, run_id)
    relevant_memories = search_memories(
        query=question,
        session_context=session_context,
        limit=10
    )
    
    // 2. Format memories for prompt
    formatted_memories = format_memories_for_answer(relevant_memories)
    
    // 3. Generate answer using LLM
    answer_prompt = build_answer_prompt(formatted_memories, question)
    answer = llm_provider.generate_response(answer_prompt)
    
    // 4. Post-process and validate answer
    validated_answer = validate_answer_quality(answer, question)
    
    RETURN validated_answer
END FUNCTION
```

### 5. Procedural Memory System

**Purpose**: Creates comprehensive summaries of multi-step agent interactions and execution histories.

**Use Cases**:
- Agent workflow documentation
- Task execution summaries
- Multi-step process recording
- Debugging and audit trails

**Procedural Memory Prompt**:
```
You are a memory summarization system that records and preserves the complete interaction history between a human and an AI agent. You are provided with the agent's execution history over the past N steps. Your task is to produce a comprehensive summary of the agent's output history that contains every detail necessary for the agent to continue the task without ambiguity. **Every output produced by the agent must be recorded verbatim as part of the summary.**

### Overall Structure:
- **Overview (Global Metadata):**
  - **Task Objective**: The overall goal the agent is working to accomplish.
  - **Progress Status**: The current completion percentage and summary of specific milestones or steps completed.

- **Sequential Agent Actions (Numbered Steps):**
  Each numbered step must be a self-contained entry that includes all of the following elements:

  1. **Agent Action**:
     - Precisely describe what the agent did (e.g., "Clicked on the 'Blog' link", "Called API to fetch content", "Scraped page data").
     - Include all parameters, target elements, or methods involved.

  2. **Action Result (Mandatory, Unmodified)**:
     - Immediately follow the agent action with its exact, unaltered output.
     - Record all returned data, responses, HTML snippets, JSON content, or error messages exactly as received. This is critical for constructing the final output later.

  3. **Embedded Metadata**:
     For the same numbered step, include additional context such as:
     - **Key Findings**: Any important information discovered (e.g., URLs, data points, search results).
     - **Navigation History**: For browser agents, detail which pages were visited, including their URLs and relevance.
     - **Errors & Challenges**: Document any error messages, exceptions, or challenges encountered along with any attempted recovery or troubleshooting.
     - **Current Context**: Describe the state after the action (e.g., "Agent is on the blog detail page" or "JSON data stored for further processing") and what the agent plans to do next.

### Guidelines:
1. **Preserve Every Output**: The exact output of each agent action is essential. Do not paraphrase or summarize the output. It must be stored as is for later use.
2. **Chronological Order**: Number the agent actions sequentially in the order they occurred. Each numbered step is a complete record of that action.
3. **Detail and Precision**: Use exact data: Include URLs, element indexes, error messages, JSON responses, and any other concrete values.
4. **Output Only the Summary**: The final output must consist solely of the structured summary with no additional commentary or preamble.
```

**Procedural Memory Creation Flow**:
```
FUNCTION create_procedural_memory(messages, metadata, custom_prompt):
    // 1. Build procedural memory prompt
    system_prompt = custom_prompt OR default_procedural_prompt
    
    // 2. Construct conversation for LLM
    procedural_messages = [
        {"role": "system", "content": system_prompt},
        ...messages,
        {"role": "user", "content": "Create procedural memory of the above conversation."}
    ]
    
    // 3. Generate procedural summary
    procedural_summary = llm_provider.generate_response(procedural_messages)
    
    // 4. Store as special memory type
    metadata["memory_type"] = "PROCEDURAL"
    embeddings = embedding_provider.generate_embedding(procedural_summary)
    memory_id = create_memory(procedural_summary, embeddings, metadata)
    
    RETURN {"id": memory_id, "memory": procedural_summary, "event": "ADD"}
END FUNCTION
```

### 6. Vision Message Support

**Purpose**: Processes images in conversations and converts them to text descriptions for memory storage.

**Supported Formats**:
- Single image messages
- Multiple images in one message
- Mixed text and image content
- Various image formats (URLs, base64, etc.)

**Vision Processing Flow**:
```
FUNCTION parse_vision_messages(messages, llm_provider, vision_config):
    processed_messages = []
    
    FOR message IN messages:
        IF message.role == "system":
            processed_messages.append(message)
            CONTINUE
        
        IF message.content IS image_content:
            // Process image(s) and convert to text description
            description = get_image_description(
                image_content=message.content,
                llm_provider=llm_provider,
                vision_config=vision_config
            )
            
            processed_message = {
                "role": message.role,
                "content": description
            }
            processed_messages.append(processed_message)
        ELSE:
            // Regular text message
            processed_messages.append(message)
    
    RETURN processed_messages
END FUNCTION

FUNCTION get_image_description(image_content, llm_provider, vision_config):
    // 1. Validate image format and accessibility
    validated_image = validate_image_content(image_content)
    
    // 2. Generate description using vision-enabled LLM
    description_prompt = build_vision_description_prompt(vision_config)
    description = llm_provider.generate_vision_response(
        image=validated_image,
        prompt=description_prompt
    )
    
    // 3. Post-process description
    cleaned_description = clean_and_validate_description(description)
    
    RETURN cleaned_description
END FUNCTION
```

### 7. Custom Prompt Configuration

**Purpose**: Allows domain-specific customization of fact extraction and memory update prompts.

**Configuration Options**:
- `custom_fact_extraction_prompt`: Override default fact extraction behavior
- `custom_update_memory_prompt`: Override default memory decision logic
- Domain-specific prompt templates
- Multi-language prompt support

**Configuration Structure**:
```
memory_config = {
    "custom_fact_extraction_prompt": "domain_specific_extraction_prompt",
    "custom_update_memory_prompt": "domain_specific_update_prompt",
    "llm": {...},
    "vector_store": {...},
    "embedder": {...}
}
```

**Custom Prompt Usage Flow**:
```
FUNCTION add_with_custom_prompts(messages, config):
    // 1. Check for custom fact extraction prompt
    IF config.custom_fact_extraction_prompt:
        system_prompt = config.custom_fact_extraction_prompt
        user_prompt = f"Input:\n{parsed_messages}"
    ELSE:
        system_prompt, user_prompt = get_default_fact_extraction_prompts(parsed_messages)
    
    // 2. Extract facts using custom or default prompt
    facts = extract_facts_with_prompt(system_prompt, user_prompt)
    
    // 3. Check for custom update memory prompt
    update_prompt = get_update_memory_prompt(
        existing_memories=existing_memories,
        new_facts=facts,
        custom_prompt=config.custom_update_memory_prompt
    )
    
    // 4. Process memory operations
    operations = process_memory_operations(update_prompt)
    
    RETURN operations
END FUNCTION
```

**Domain-Specific Examples**:

**Customer Support Configuration**:
```
custom_fact_extraction_prompt = """
Please only extract entities containing customer support information, order details, and user information.
Focus on: order numbers, customer names, product issues, return requests, support tickets.
"""
```

**Healthcare Configuration**:
```
custom_fact_extraction_prompt = """
Extract health-related facts while maintaining patient privacy.
Focus on: symptoms, treatments, medications, appointments, dietary restrictions.
Exclude: specific medical record numbers, detailed personal information.
"""
```

## Key Features

### 1. Session Management Integration

**Session Isolation Strategy**:
- Strict separation using session identifiers
- Metadata-based filtering at the vector store level
- Validation of session ownership for all operations
- Support for multi-tenant scenarios

**Session Types**:
- **User Sessions**: Personal memory spaces
- **Agent Sessions**: AI assistant memory contexts
- **Run Sessions**: Temporary conversation scopes

### 2. Dual Processing Modes

**Inference Mode (Default)**:
- LLM-powered fact extraction from conversations
- Intelligent memory decision making (ADD/UPDATE/DELETE)
- Conflict resolution and duplicate detection
- Context-aware memory organization

**Direct Mode**:
- Raw message storage without AI processing
- Faster operation for simple use cases
- Preserves original message structure and metadata
- Useful for debugging and data collection

### 3. Component Orchestration

**Provider Management**:
- Factory pattern for component instantiation
- Configuration-driven provider selection
- Graceful fallback handling for service failures
- Health checking and connection validation

**Data Flow Coordination**:
- Embedding generation and caching
- Vector store operations with retry logic
- History tracking with transactional consistency
- Optional graph store integration

### 4. Error Handling Strategy

**Resilience Patterns**:
- Circuit breaker for external service failures
- Exponential backoff for transient errors
- Graceful degradation when components unavailable
- Comprehensive logging for debugging

**Data Consistency**:
- Atomic operations where possible
- Rollback mechanisms for failed multi-step operations
- Eventual consistency for distributed components
- Conflict resolution for concurrent updates

### 5. Performance Optimization

**Caching Strategy**:
- Embedding cache with LRU eviction
- Session metadata caching
- Query result caching for repeated searches
- Configuration caching to reduce overhead

**Batch Processing**:
- Bulk embedding generation
- Batch vector store operations
- Parallel processing of independent operations
- Optimized memory allocation for large datasets

## Configuration Management

**Configuration Structure**:
- Hierarchical configuration with environment overrides
- Provider-specific configuration sections
- Performance tuning parameters
- Feature flags for optional functionality

**Key Configuration Areas**:
- LLM provider settings (API keys, models, parameters)
- Vector store configuration (connection, indexing)
- Embedding provider setup
- Performance parameters (timeouts, batch sizes)
- Optional features (graph memory, telemetry)

**Environment Integration**:
- 12-factor app compliance
- Secure credential management
- Development/staging/production profiles
- Runtime configuration updates where safe

## Testing Strategy

### 1. Unit Testing Approach

**Component Isolation**:
- Mock external dependencies (LLM, vector store)
- Test each method independently
- Validate error handling paths
- Verify session isolation logic

**Test Categories**:
- Happy path scenarios for all operations
- Error conditions and edge cases
- Session boundary validation
- Configuration validation

### 2. Integration Testing

**End-to-End Workflows**:
- Complete memory lifecycle testing
- Multi-component interaction validation
- Real provider integration testing
- Performance baseline establishment

**Test Scenarios**:
- Conversation processing with fact extraction
- Memory search and retrieval accuracy
- Update and delete operation consistency
- Concurrent operation handling

### 3. Performance Testing

**Load Testing**:
- High-volume memory operations
- Concurrent user simulation
- Memory usage profiling
- Latency measurement under load

**Scalability Testing**:
- Large memory collection handling
- Search performance with growing datasets
- Resource utilization monitoring
- Bottleneck identification

## Implementation Notes

### 1. Thread Safety Considerations

**Synchronous Implementation**:
- Thread-safe component initialization
- Proper locking for shared resources
- Immutable configuration objects
- Safe concurrent access patterns

**Asynchronous Implementation**:
- Async-safe resource management
- Proper coroutine lifecycle handling
- Concurrent operation coordination
- Deadlock prevention strategies

### 2. Resource Management

**Connection Handling**:
- Connection pooling for database operations
- Proper cleanup on shutdown
- Resource leak prevention
- Health monitoring and recovery

**Memory Management**:
- Efficient embedding storage
- Cache size limits and eviction
- Large dataset streaming
- Garbage collection optimization

### 3. Monitoring and Observability

**Metrics Collection**:
- Operation latency and throughput
- Error rates and types
- Resource utilization
- Cache hit rates

**Logging Strategy**:
- Structured logging with correlation IDs
- Appropriate log levels for different scenarios
- Performance-sensitive logging optimization
- Security-conscious log sanitization

**Health Checks**:
- Component availability monitoring
- Dependency health validation
- Performance threshold alerting
- Automated recovery procedures 