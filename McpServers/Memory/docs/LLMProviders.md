# LLM Provider Architecture - Detailed Design

## Overview

The LLM Provider Architecture provides a unified interface for interacting with different Large Language Model providers. This system supports OpenAI and Anthropic providers with structured output capabilities for fact extraction and memory decision making.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    LLM Provider Layer                       │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   LLMBase   │  │ LLMFactory  │  │   LLMConfig         │  │
│  │ (Abstract)  │  │             │  │                     │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Provider Implementations                 │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   OpenAI    │  │  Anthropic  │  │   Structured        │  │
│  │  Provider   │  │  Provider   │  │    Output           │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│                    Utility Components                       │
├─────────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │   Retry     │  │   Rate      │  │     Response        │  │
│  │  Handler    │  │  Limiter    │  │    Validator        │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Core Components

### 1. LLMBase (Abstract Interface)

**Purpose**: Defines the contract for all LLM provider implementations, ensuring consistent behavior across different providers.

**Key Methods**:
- `generate_response()`: Generate text responses from conversation messages
- `generate_structured_response()`: Generate JSON responses with schema validation
- `validate_connection()`: Test provider connectivity and authentication

**Design Principles**:
- Provider-agnostic message format normalization
- Consistent error handling across providers
- Standardized response formats
- Built-in retry and rate limiting support

**Message Format Standardization**:
- Unified message structure with role, content, and optional name fields
- Automatic conversion between provider-specific formats
- Support for system, user, and assistant roles
- Preservation of conversation context and metadata

### 2. OpenAI Provider Implementation

**Authentication Strategy**:
- API key-based authentication with optional organization support
- Support for custom base URLs (Azure OpenAI, local deployments)
- Secure credential management through environment variables
- Connection validation during initialization

#### OpenAI Provider Flow (Pseudo Code)

```
FUNCTION openai_generate_response(messages, config, retry_config):
    // 1. Authentication & Configuration
    client = initialize_openai_client(
        api_key=config.api_key,
        organization=config.organization,
        base_url=config.base_url
    )
    
    // 2. Request Preparation
    request_params = build_request_params(
        model=config.model,
        messages=normalize_messages_for_openai(messages),
        temperature=config.temperature,
        max_tokens=config.max_tokens,
        response_format=config.response_format
    )
    
    // 3. Request Execution with Retry Logic
    response = execute_with_retry(
        request_function=lambda: client.chat.completions.create(**request_params),
        retry_config=retry_config,
        error_handlers={
            "rate_limit": handle_rate_limit_error,
            "token_limit": handle_token_limit_error,
            "api_error": handle_api_error
        }
    )
    
    // 4. Response Processing
    processed_response = process_openai_response(response)
    validate_response_format(processed_response, config.expected_format)
    
    // 5. Usage Tracking
    track_token_usage(
        prompt_tokens=response.usage.prompt_tokens,
        completion_tokens=response.usage.completion_tokens,
        model=config.model
    )
    
    RETURN processed_response
END FUNCTION
```

**Request Handling**:
- Native support for OpenAI's chat completions API
- Structured output using response_format parameter
- Token usage tracking and optimization
- Proper handling of OpenAI-specific parameters (temperature, top_p, max_tokens)

**Error Management**:
- OpenAI-specific error code handling
- Rate limit detection and backoff
- Token limit management
- Network timeout and retry logic

**Structured Output Strategy**:
- Leverage OpenAI's native JSON mode for reliable structured responses
- Schema validation for response format compliance
- Fallback parsing for edge cases
- Error recovery for malformed JSON

### 3. Anthropic Provider Implementation

**Authentication Strategy**:
- API key-based authentication
- Support for different model families (Claude 3, Claude 2)
- Proper handling of Anthropic's message format requirements
- Connection validation and health checks

#### Anthropic Provider Flow (Pseudo Code)

```
FUNCTION anthropic_generate_response(messages, config, retry_config):
    // 1. Authentication & Configuration
    client = initialize_anthropic_client(
        api_key=config.api_key,
        base_url=config.base_url
    )
    
    // 2. Message Format Adaptation
    system_message, conversation_messages = separate_system_message(messages)
    formatted_messages = format_messages_for_anthropic(conversation_messages)
    
    // 3. Request Preparation
    request_params = build_anthropic_request(
        model=config.model,
        system=system_message,
        messages=formatted_messages,
        max_tokens=config.max_tokens,
        temperature=config.temperature
    )
    
    // 4. Structured Output Enhancement
    IF config.structured_output_required:
        request_params = enhance_for_structured_output(
            params=request_params,
            output_schema=config.output_schema
        )
    
    // 5. Request Execution with Retry Logic
    response = execute_with_retry(
        request_function=lambda: client.messages.create(**request_params),
        retry_config=retry_config,
        error_handlers={
            "rate_limit": handle_anthropic_rate_limit,
            "content_filter": handle_content_filter_error,
            "api_error": handle_anthropic_api_error
        }
    )
    
    // 6. Response Processing & JSON Extraction
    raw_content = extract_content_from_response(response)
    
    IF config.structured_output_required:
        structured_data = extract_json_from_content(
            content=raw_content,
            schema=config.output_schema,
            fallback_strategy="retry_with_simpler_prompt"
        )
        processed_response = structured_data
    ELSE:
        processed_response = raw_content
    
    // 7. Usage Tracking
    track_anthropic_usage(
        input_tokens=response.usage.input_tokens,
        output_tokens=response.usage.output_tokens,
        model=config.model
    )
    
    RETURN processed_response
END FUNCTION
```

**Message Format Adaptation**:
- Separation of system messages from conversation flow
- Proper role mapping between formats
- Context length management for long conversations
- Anthropic-specific parameter handling

**Structured Output Approach**:
- Prompt-based JSON generation (no native structured output)
- Enhanced prompting for reliable JSON responses
- JSON extraction from mixed text/JSON responses
- Validation and error recovery mechanisms

**Rate Limiting Considerations**:
- Anthropic-specific rate limit handling
- Request queuing and throttling
- Usage tracking and optimization
- Graceful degradation under limits

### 4. LLM Factory Pattern

**Provider Registration**:
- Dynamic provider registration system
- Configuration-driven provider selection
- Support for custom provider implementations
- Runtime provider switching capabilities

#### LLM Factory Flow (Pseudo Code)

```
FUNCTION create_llm_provider(config):
    // 1. Provider Selection
    provider_type = config.provider.lower()
    
    // 2. Provider-Specific Initialization
    SWITCH provider_type:
        CASE "openai":
            provider = initialize_openai_provider(
                api_key=config.openai.api_key,
                model=config.openai.model,
                organization=config.openai.organization,
                base_url=config.openai.base_url
            )
            
        CASE "anthropic":
            provider = initialize_anthropic_provider(
                api_key=config.anthropic.api_key,
                model=config.anthropic.model,
                base_url=config.anthropic.base_url
            )
            
        DEFAULT:
            THROW UnsupportedProviderError(provider_type)
    
    // 3. Provider Wrapping with Common Interface
    wrapped_provider = wrap_with_common_interface(
        provider=provider,
        config=config,
        retry_config=config.retry,
        rate_limit_config=config.rate_limiting
    )
    
    // 4. Health Check
    validate_provider_health(wrapped_provider)
    
    // 5. Monitoring Setup
    setup_provider_monitoring(wrapped_provider, config.monitoring)
    
    RETURN wrapped_provider
END FUNCTION

FUNCTION wrap_with_common_interface(provider, config, retry_config, rate_limit_config):
    // Create unified interface wrapper
    wrapper = LLMProviderWrapper(
        provider=provider,
        provider_type=config.provider
    )
    
    // Add retry functionality
    wrapper = add_retry_wrapper(wrapper, retry_config)
    
    // Add rate limiting
    wrapper = add_rate_limiting_wrapper(wrapper, rate_limit_config)
    
    // Add monitoring
    wrapper = add_monitoring_wrapper(wrapper, config.monitoring)
    
    // Add caching if enabled
    IF config.caching.enabled:
        wrapper = add_caching_wrapper(wrapper, config.caching)
    
    RETURN wrapper
END FUNCTION
```

**Configuration Management**:
- Provider-specific configuration classes
- Environment variable integration
- Validation of provider requirements
- Default configuration handling

**Instance Creation**:
- Lazy initialization of provider clients
- Connection pooling where applicable
- Resource cleanup and lifecycle management
- Health monitoring and failover

### 5. Utility Components

#### Retry Handler Flow (Pseudo Code)

```
FUNCTION execute_with_retry(request_function, retry_config, error_handlers):
    max_retries = retry_config.max_retries
    base_delay = retry_config.base_delay
    max_delay = retry_config.max_delay
    backoff_multiplier = retry_config.backoff_multiplier
    
    FOR attempt IN range(max_retries + 1):
        TRY:
            result = request_function()
            RETURN result
            
        EXCEPT Exception as error:
            error_type = classify_error(error)
            
            // Check if error is retryable
            IF NOT is_retryable_error(error_type):
                THROW error
            
            // Check if we've exhausted retries
            IF attempt >= max_retries:
                THROW error
            
            // Handle specific error types
            IF error_type IN error_handlers:
                error_handlers[error_type](error, attempt)
            
            // Calculate delay with exponential backoff
            delay = min(
                base_delay * (backoff_multiplier ** attempt),
                max_delay
            )
            
            // Add jitter to prevent thundering herd
            jittered_delay = delay * (0.5 + random() * 0.5)
            
            // Log retry attempt
            log_retry_attempt(attempt, error_type, jittered_delay)
            
            // Wait before retry
            sleep(jittered_delay)
    
    THROW MaxRetriesExceededError(max_retries)
END FUNCTION
```

#### Rate Limiter Flow (Pseudo Code)

```
FUNCTION rate_limited_request(request_function, rate_limiter):
    // 1. Check Rate Limit Availability
    wait_time = rate_limiter.get_wait_time()
    
    IF wait_time > 0:
        log_rate_limit_wait(wait_time)
        sleep(wait_time)
    
    // 2. Acquire Rate Limit Token
    token = rate_limiter.acquire_token()
    
    TRY:
        // 3. Execute Request
        start_time = get_current_time()
        result = request_function()
        end_time = get_current_time()
        
        // 4. Update Rate Limiter Based on Response
        response_headers = extract_rate_limit_headers(result)
        rate_limiter.update_from_response(response_headers)
        
        // 5. Log Request Metrics
        log_request_metrics(
            duration=end_time - start_time,
            tokens_used=token,
            remaining_tokens=rate_limiter.get_remaining_tokens()
        )
        
        RETURN result
        
    EXCEPT RateLimitError as error:
        // 6. Handle Rate Limit Error
        reset_time = extract_reset_time(error)
        rate_limiter.handle_rate_limit_error(reset_time)
        
        THROW error
        
    FINALLY:
        // 7. Release Token
        rate_limiter.release_token(token)
END FUNCTION
```

#### Response Validator Flow (Pseudo Code)

```
FUNCTION validate_llm_response(response, expected_format, validation_config):
    // 1. Basic Format Validation
    IF NOT response OR response.is_empty():
        THROW EmptyResponseError()
    
    // 2. Code Block Removal (Critical for JSON parsing)
    cleaned_response = remove_code_blocks(response)
    
    // 3. Format-Specific Validation
    SWITCH expected_format:
        CASE "json":
            validated_response = validate_json_response(
                response=cleaned_response,
                schema=validation_config.json_schema,
                repair_strategy=validation_config.json_repair_strategy
            )
            
        CASE "text":
            validated_response = validate_text_response(
                response=cleaned_response,
                min_length=validation_config.min_text_length,
                max_length=validation_config.max_text_length
            )
            
        DEFAULT:
            validated_response = cleaned_response
    
    // 4. Content Quality Validation
    quality_score = assess_response_quality(
        response=validated_response,
        quality_metrics=validation_config.quality_metrics
    )
    
    IF quality_score < validation_config.min_quality_score:
        THROW LowQualityResponseError(quality_score)
    
    // 5. Safety and Content Filtering
    IF validation_config.content_filtering_enabled:
        content_safety = check_content_safety(validated_response)
        IF NOT content_safety.is_safe:
            THROW UnsafeContentError(content_safety.violations)
    
    RETURN validated_response
END FUNCTION

FUNCTION remove_code_blocks(content):
    // Critical utility for cleaning LLM responses
    // Pattern matches: ```[optional_language]\n...content...\n```
    pattern = r"^```[a-zA-Z0-9]*\n([\s\S]*?)\n```$"
    match = regex_match(pattern, content.strip())
    
    IF match:
        RETURN match.group(1).strip()
    ELSE:
        RETURN content.strip()
END FUNCTION

FUNCTION validate_json_response(response, schema, repair_strategy):
    TRY:
        parsed_json = json.parse(response)
        validate_against_schema(parsed_json, schema)
        RETURN parsed_json
        
    EXCEPT JsonParseError as error:
        IF repair_strategy == "attempt_repair":
            repaired_response = attempt_json_repair(response)
            RETURN validate_json_response(repaired_response, schema, "none")
        ELIF repair_strategy == "partial_recovery":
            partial_data = attempt_partial_json_recovery(response)
            RETURN partial_data
        ELSE:
            THROW error
            
    EXCEPT SchemaValidationError as error:
        IF repair_strategy == "partial_accept":
            partial_data = extract_valid_parts(response, schema)
            RETURN partial_data
        ELSE:
            THROW error
END FUNCTION

FUNCTION attempt_json_repair(malformed_json):
    // Common repair strategies for malformed JSON
    repaired = malformed_json
    
    // Fix common issues
    repaired = fix_trailing_commas(repaired)
    repaired = fix_unescaped_quotes(repaired)
    repaired = fix_missing_brackets(repaired)
    repaired = fix_incomplete_objects(repaired)
    
    RETURN repaired
END FUNCTION

FUNCTION attempt_partial_json_recovery(response):
    // Try to extract valid JSON from partial or corrupted response
    // Look for JSON-like patterns and extract what's salvageable
    
    // Strategy 1: Find complete JSON objects
    json_objects = extract_complete_json_objects(response)
    IF json_objects:
        RETURN json_objects[0]  // Return first valid object
    
    // Strategy 2: Extract key-value pairs
    key_value_pairs = extract_key_value_pairs(response)
    IF key_value_pairs:
        RETURN construct_json_from_pairs(key_value_pairs)
    
    // Strategy 3: Return empty structure based on expected format
    RETURN get_empty_fallback_structure()
END FUNCTION
```

## Integration with Memory System

### 1. Fact Extraction Integration

**Prompt Management**:
- Sophisticated prompting strategies for fact extraction
- Few-shot examples for consistent output
- Context-aware prompt customization
- Multi-language prompt support

**Response Processing**:
- Structured fact list extraction
- Fact validation and filtering
- Duplicate detection and merging
- Quality scoring and ranking

### 2. Memory Decision Integration

**Decision Prompting**:
- Complex decision trees for memory operations
- Context-rich prompts with existing memory information
- Conflict resolution guidance
- Temporal reasoning support

**Operation Validation**:
- Decision consistency checking
- Operation feasibility validation
- Conflict detection and resolution
- Quality assurance for memory changes

## Error Handling and Resilience

### 1. Connection Failures

**Resilience Patterns**:
- Automatic retry with exponential backoff
- Circuit breaker for persistent failures
- Health check monitoring
- Graceful degradation to fallback providers

**Recovery Strategies**:
- Connection pool management
- DNS resolution caching
- Network timeout optimization
- Regional failover support

### 2. Rate Limiting Management

**Rate Limit Handling**:
- Proactive rate limit monitoring
- Adaptive request scheduling
- Queue management for burst traffic
- Cost optimization strategies

**Backoff Strategies**:
- Intelligent backoff based on rate limit headers
- Jittered retry to avoid thundering herd
- Priority-based request queuing
- Load shedding under extreme limits

### 3. Response Quality Assurance

**Quality Validation**:
- Response completeness checking
- Format validation and correction
- Content filtering and sanitization
- Consistency verification across requests

**Fallback Mechanisms**:
- Alternative prompt strategies
- Simplified request formats
- Provider switching for quality issues
- Manual intervention triggers

## Performance Optimization

### 1. Request Optimization

**Efficiency Strategies**:
- Connection pooling and reuse
- Request batching where supported
- Streaming responses for long content
- Token usage optimization

**Caching Strategy**:
- Response caching for identical requests
- Prompt template caching
- Configuration caching
- TTL-based cache invalidation

### 2. Monitoring and Metrics

**Performance Metrics**:
- Request latency tracking
- Token usage monitoring
- Error rate analysis
- Cost tracking and optimization

**Operational Metrics**:
- Provider availability monitoring
- Rate limit utilization
- Cache hit rates
- Quality score tracking

## Security Considerations

### 1. API Key Management

**Security Practices**:
- Environment variable configuration
- Secure key rotation support
- Key validation and masking in logs
- Access control and auditing

**Credential Protection**:
- In-memory key storage only
- Encrypted configuration files
- Runtime key injection
- Audit trail for key usage

### 2. Request/Response Security

**Data Protection**:
- Request/response sanitization
- PII detection and handling
- Content filtering for sensitive data
- Audit logging with privacy controls

**Communication Security**:
- TLS encryption for all requests
- Certificate validation
- Request signing where supported
- Network security best practices

## Testing Strategy

### 1. Unit Testing Approach

**Component Testing**:
- Mock-based provider testing
- Configuration validation testing
- Error handling scenario testing
- Response format validation testing

**Test Coverage**:
- All provider implementations
- Error conditions and edge cases
- Configuration variations
- Security boundary testing

### 2. Integration Testing

**Provider Integration**:
- Real API endpoint testing (with test credentials)
- Cross-provider consistency validation
- Performance baseline establishment
- Error scenario simulation

**System Integration**:
- End-to-end workflow testing
- Memory system integration validation
- Concurrent operation testing
- Failover and recovery testing

### 3. Performance Testing

**Load Testing**:
- High-volume request simulation
- Concurrent user testing
- Rate limit boundary testing
- Resource utilization monitoring

**Quality Testing**:
- Response quality consistency
- Structured output reliability
- Error recovery effectiveness
- Provider comparison analysis

## Configuration Examples

**OpenAI Configuration**:
```yaml
llm:
  provider: openai
  api_key: ${OPENAI_API_KEY}
  model: gpt-4
  temperature: 0.0
  max_tokens: 1000
  timeout: 30
  max_retries: 3
```

**Anthropic Configuration**:
```yaml
llm:
  provider: anthropic
  api_key: ${ANTHROPIC_API_KEY}
  model: claude-3-sonnet-20240229
  temperature: 0.0
  max_tokens: 1000
  timeout: 30
  max_retries: 3
```

## Implementation Priorities

### Phase 1: Core Infrastructure
1. Abstract base class and interfaces
2. OpenAI provider implementation
3. Basic error handling and retry logic

### Phase 2: Enhanced Functionality
4. Anthropic provider implementation
5. Structured output support
6. Rate limiting and optimization

### Phase 3: Advanced Features
7. Response caching and optimization
8. Advanced error recovery
9. Monitoring and observability 