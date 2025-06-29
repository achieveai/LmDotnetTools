# Error Handling & Resilience - Test Acceptance Criteria

## Overview
This document defines the comprehensive test acceptance criteria for implementing robust error handling and resilience in the Document Segmentation service. All criteria must be met and validated through automated tests.

## 📋 Test Categories & Success Criteria

### 1. LLM Failure Simulation Tests (`subtask-152`)

#### **AC-1.1: Network Timeout Handling**
- **Given:** LLM API call experiences network timeout (>30s)
- **When:** Service attempts document segmentation
- **Then:** 
  - ✅ Service logs timeout with appropriate level (Warning)
  - ✅ Retry mechanism triggers with exponential backoff
  - ✅ After max retries, gracefully falls back to rule-based segmentation
  - ✅ Response includes quality indicator showing degraded mode
  - ✅ Total processing time < 60 seconds including retries

#### **AC-1.2: Rate Limiting (HTTP 429) Handling**
- **Given:** LLM API returns HTTP 429 (Too Many Requests)
- **When:** Service receives rate limit response
- **Then:**
  - ✅ Service respects Retry-After header if present
  - ✅ Circuit breaker does NOT trigger (429 is expected, not a failure)
  - ✅ Exponential backoff applies with jitter
  - ✅ Maximum 5 retry attempts before fallback
  - ✅ Logging indicates rate limiting encountered

#### **AC-1.3: Authentication Errors (HTTP 401)**
- **Given:** LLM API returns HTTP 401 (Unauthorized)
- **When:** Service attempts API call
- **Then:**
  - ✅ Service logs authentication error (Error level)
  - ✅ NO retries attempted (authentication won't fix itself)
  - ✅ Immediate fallback to rule-based segmentation
  - ✅ Circuit breaker opens after threshold failures
  - ✅ Error response includes clear error message

#### **AC-1.4: Service Unavailable (HTTP 503)**
- **Given:** LLM API returns HTTP 503 (Service Unavailable)
- **When:** Service attempts document processing
- **Then:**
  - ✅ Service treats as temporary failure
  - ✅ Retry mechanism activates with full backoff
  - ✅ Circuit breaker failure count increments
  - ✅ After threshold, circuit opens
  - ✅ Graceful fallback to rule-based processing

#### **AC-1.5: Malformed Response Handling**
- **Given:** LLM API returns HTTP 200 but invalid JSON/data
- **When:** Service attempts to parse response
- **Then:**
  - ✅ Service logs parsing error with response excerpt
  - ✅ Treats as failure and increments circuit breaker count
  - ✅ Retry mechanism triggers (might be transient)
  - ✅ After max retries, falls back to rule-based
  - ✅ No exceptions bubble up to caller

#### **AC-1.6: Connection Failure Handling**
- **Given:** Network connection to LLM API completely fails
- **When:** Service attempts to establish connection
- **Then:**
  - ✅ Service logs connection failure (Warning level)
  - ✅ Retry mechanism applies with exponential backoff
  - ✅ Circuit breaker failure count increments
  - ✅ After threshold, circuit opens and stops attempts
  - ✅ Immediate fallback to rule-based segmentation

### 2. Circuit Breaker Implementation (`subtask-153`)

#### **AC-2.1: Circuit Breaker States**
- **Closed State:**
  - ✅ All API calls pass through normally
  - ✅ Failure count resets on successful call
  - ✅ Transitions to Open on reaching failure threshold (5 failures)
  
- **Open State:**
  - ✅ No API calls attempted
  - ✅ Immediate fallback to rule-based segmentation
  - ✅ Transitions to Half-Open after timeout (30 seconds)
  - ✅ Logs circuit open event
  
- **Half-Open State:**
  - ✅ Single test API call allowed
  - ✅ Success transitions to Closed
  - ✅ Failure transitions back to Open
  - ✅ Logs state transitions

#### **AC-2.2: Failure Threshold Configuration**
- ✅ Default threshold: 5 consecutive failures
- ✅ Configurable via appsettings.json
- ✅ Different thresholds for different error types (401 vs 503)
- ✅ Threshold resets on any successful call

#### **AC-2.3: Recovery Timing**
- ✅ Default Open→Half-Open timeout: 30 seconds
- ✅ Exponentially increasing timeout on repeated failures
- ✅ Maximum timeout cap: 5 minutes
- ✅ Configurable timing parameters

### 3. Retry Mechanism with Exponential Backoff (`subtask-154`)

#### **AC-3.1: Retry Count Validation**
- ✅ Maximum retry attempts: 3 (configurable)
- ✅ Retry counter resets on success
- ✅ Different retry counts for different error types
- ✅ No retries for non-retryable errors (401, 400)

#### **AC-3.2: Exponential Backoff Timing**
- ✅ Base delay: 1 second
- ✅ Exponential factor: 2.0
- ✅ Sequence: 1s, 2s, 4s (for 3 retries)
- ✅ Maximum delay cap: 30 seconds

#### **AC-3.3: Jitter Implementation**
- ✅ Random jitter: ±10% of calculated delay
- ✅ Prevents thundering herd problem
- ✅ Jitter is truly random (not deterministic)
- ✅ Applied to all retry delays

#### **AC-3.4: Retry Context Preservation**
- ✅ Original request parameters preserved
- ✅ Correlation ID maintained across retries
- ✅ Request timeout accounts for total retry time
- ✅ Logging includes retry attempt number

### 4. Graceful Degradation to Rule-Based (`subtask-155`)

#### **AC-4.1: Seamless Fallback**
- ✅ Fallback occurs within 5 seconds of LLM failure
- ✅ No exceptions exposed to calling code
- ✅ Response structure identical to LLM-enhanced version
- ✅ Processing continues without interruption

#### **AC-4.2: Quality Indicators**
- ✅ Response includes `degradedMode: true` flag
- ✅ Quality score reflects rule-based confidence (typically 0.7)
- ✅ Metadata indicates segmentation strategy used
- ✅ Reasoning explanation provided for degradation

#### **AC-4.3: Performance Requirements**
- ✅ Rule-based fallback completes within 10 seconds
- ✅ Performance degradation < 20% compared to LLM mode
- ✅ Memory usage remains stable during fallback
- ✅ CPU usage spikes handled gracefully

#### **AC-4.4: User Experience**
- ✅ Clear indication of degraded mode in response
- ✅ Suggested actions for users (retry later, etc.)
- ✅ No confusing error messages
- ✅ Consistent API response format

### 5. Error Recovery and State Management (`subtask-156`)

#### **AC-5.1: Service Recovery**
- ✅ Automatic detection of LLM service restoration
- ✅ Circuit breaker transitions Closed on successful call
- ✅ Gradual ramp-up of traffic (not immediate full load)
- ✅ Health check endpoint reflects current state

#### **AC-5.2: State Cleanup**
- ✅ Error metrics reset appropriately
- ✅ No memory leaks from error tracking
- ✅ Log files don't grow unbounded
- ✅ Background tasks clean up expired state

#### **AC-5.3: Error Metrics Collection**
- ✅ Metrics for each error type (timeout, 429, 503, etc.)
- ✅ Circuit breaker state duration tracking
- ✅ Fallback usage frequency
- ✅ Recovery time measurements
- ✅ API response time percentiles

#### **AC-5.4: Logging Standards**
- ✅ Error level for actual problems (auth failures)
- ✅ Warning level for retryable issues (timeouts)
- ✅ Info level for circuit breaker state changes
- ✅ Debug level for retry attempt details
- ✅ Structured logging with correlation IDs

## 🎯 Success Metrics

### Performance Targets
- **P95 Response Time:** < 15 seconds (including retries/fallback)
- **P99 Response Time:** < 30 seconds
- **Availability:** > 99.5% (measured as successful responses)
- **Fallback Rate:** < 5% under normal conditions

### Reliability Targets
- **Error Recovery:** < 60 seconds to detect and recover from LLM outages
- **Memory Stability:** No memory leaks during 24-hour error scenario testing
- **Circuit Breaker Accuracy:** > 99% correct state transitions
- **Retry Efficiency:** > 80% success rate on first retry for transient failures

## 🧪 Test Implementation Requirements

### Test Infrastructure
- ✅ Mock HTTP handlers for simulating various failure scenarios
- ✅ Time manipulation for testing backoff delays
- ✅ Metrics collection verification
- ✅ Concurrent load testing capabilities
- ✅ Integration test environment with controllable LLM mock

### Test Categories
- **Unit Tests:** Individual component behavior (circuit breaker, retry logic)
- **Integration Tests:** End-to-end error scenarios
- **Load Tests:** Performance under error conditions
- **Chaos Tests:** Random failure injection
- **Recovery Tests:** Service restoration scenarios

### Test Data Requirements
- Sample documents of various sizes (small, medium, large)
- Different document types (technical, narrative, mixed)
- Various error response payloads
- Performance baseline measurements

## 📊 Acceptance Validation

Each subtask will be considered complete when:

1. **All test scenarios pass** with the defined success criteria
2. **Code coverage** > 90% for error handling components
3. **Performance benchmarks** meet or exceed targets
4. **Integration tests** pass in CI/CD pipeline
5. **Manual testing** confirms user experience requirements
6. **Documentation** is updated with error handling guide

---

*This document serves as the definitive test acceptance criteria for the Error Handling & Resilience implementation. All criteria must be met before the subtask can be marked as complete.*
