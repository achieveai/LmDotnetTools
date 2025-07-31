# Logging Guidance for LmDotnetTools

This document provides comprehensive guidance on logging levels, structured logging patterns, best practices, and performance considerations for the LmDotnetTools library.

## Table of Contents

1. [Logging Levels and When to Use Each](#logging-levels-and-when-to-use-each)
2. [Structured Logging Patterns](#structured-logging-patterns)
3. [Best Practices](#best-practices)
4. [Performance Considerations](#performance-considerations)
5. [Event IDs and Categories](#event-ids-and-categories)
6. [Troubleshooting](#troubleshooting)

## Logging Levels and When to Use Each

### Trace Level
**Use for**: Extremely detailed diagnostic information, typically only of interest when diagnosing problems.

**Examples**:
- Message content summaries during conversion
- Request/response serialization details
- Internal state transitions
- Memory operations (caching, disposal)

```csharp
_logger.LogTrace("Message conversion: From={FromType} To={ToType}, ContentLength={ContentLength}",
    sourceMessage.GetType().Name, targetType.Name, content?.Length ?? 0);

_logger.LogTrace("Request serialization: PayloadSize={PayloadSize}, SerializationTime={SerializationTime}ms",
    jsonContent.Length, serializationTime);
```

**When to enable**: Only during deep debugging sessions. Not recommended for production due to volume.

### Debug Level
**Use for**: Detailed information for diagnosing problems, understanding system behavior, and development debugging.

**Examples**:
- Model resolution and provider selection details
- Agent caching decisions (hit/miss)
- Streaming metrics and chunk processing
- Message transformation details
- Configuration resolution

```csharp
_logger.LogDebug("Provider selection: ModelId={ModelId}, AvailableProviders={ProviderCount}, SelectedProvider={SelectedProvider}",
    modelId, availableProviders.Count, selectedProvider.Name);

_logger.LogDebug("Agent cache hit: CacheKey={CacheKey}, AgentType={AgentType}",
    cacheKey, agent.GetType().Name);

_logger.LogDebug("Streaming metrics: ChunkCount={ChunkCount}, TimeToFirstToken={TimeToFirstToken}ms, TokensPerSecond={TokensPerSecond:F2}",
    chunkCount, timeToFirstToken, tokensPerSecond);
```

**When to enable**: Development, testing, and when investigating specific issues in staging environments.

### Information Level
**Use for**: General information about application flow, successful operations, and key business events.

**Examples**:
- LLM API call initiation and completion
- Function execution results
- Middleware processing summaries
- Usage data enrichment
- Agent delegation decisions

```csharp
_logger.LogInformation("LLM request initiated: Model={ModelId}, MessageCount={MessageCount}, Type={RequestType}",
    modelId, messageCount, requestType);

_logger.LogInformation("Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}",
    functionName, duration, success);

_logger.LogInformation("Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}}}",
    completionId, model, promptTokens, completionTokens, totalCost);
```

**When to enable**: Production environments for monitoring and operational visibility.

### Warning Level
**Use for**: Potentially harmful situations that don't prevent the application from continuing.

**Examples**:
- Missing usage data from providers
- Token count discrepancies
- Retry attempts
- Fallback mechanism usage
- Partial operation failures

```csharp
_logger.LogWarning("Usage data unavailable: CompletionId={CompletionId}, Provider={Provider}, Attempt={Attempt}",
    completionId, providerName, attemptNumber);

_logger.LogWarning("Token count mismatch: Expected={ExpectedTokens}, Actual={ActualTokens}, CompletionId={CompletionId}",
    expectedTokens, actualTokens, completionId);

_logger.LogWarning("API retry triggered: Attempt={Attempt}/{MaxAttempts}, Error={Error}, Delay={Delay}ms",
    attempt, maxAttempts, error.Message, delayMs);
```

**When to enable**: Always enabled in production to catch potential issues.

### Error Level
**Use for**: Error events that might still allow the application to continue running.

**Examples**:
- LLM provider API failures
- Function execution failures
- Authentication/authorization failures
- Middleware processing errors
- Agent creation failures

```csharp
_logger.LogError(exception, "LLM request failed: Model={ModelId}, Provider={Provider}, Duration={Duration}ms",
    modelId, providerName, duration);

_logger.LogError(exception, "Function execution failed: Name={FunctionName}, Args={Args}",
    functionName, JsonSerializer.Serialize(args));

_logger.LogError(exception, "Agent creation failed: Provider={Provider}, Model={Model}",
    providerName, modelName);
```

**When to enable**: Always enabled in all environments.

### Critical Level
**Use for**: Very serious error events that might cause the application to terminate.

**Examples**:
- Configuration corruption
- Critical resource unavailability
- Security breaches
- System-level failures

```csharp
_logger.LogCritical(exception, "Configuration system failure: Unable to load provider configurations");

_logger.LogCritical("Security violation detected: Unauthorized access attempt from {Source}",
    requestSource);
```

**When to enable**: Always enabled and should trigger immediate alerts.

## Structured Logging Patterns

### Standard Parameter Names

Use consistent parameter names across the library for better log analysis:

```csharp
// Model and Provider Information
{ModelId}           // The model identifier (e.g., "gpt-4")
{ProviderName}      // Provider name (e.g., "openai", "anthropic")
{EffectiveModel}    // The actual model name used by the provider
{AgentType}         // Type of agent (e.g., "UnifiedAgent", "OpenClientAgent")

// Request Information
{RequestId}         // Unique request identifier
{CompletionId}      // LLM completion identifier
{MessageCount}      // Number of messages in the request
{RequestType}       // "streaming" or "non-streaming"

// Performance Metrics
{Duration}          // Operation duration in milliseconds
{TokensPerSecond}   // Throughput metric
{PromptTokens}      // Number of prompt tokens
{CompletionTokens}  // Number of completion tokens
{TotalCost}         // Cost in dollars (format with :F6)

// Function and Tool Information
{FunctionName}      // Name of the executed function
{ToolCallId}        // Unique tool call identifier
{ToolCallCount}     // Number of tool calls

// Middleware Information
{MiddlewareName}    // Name of the middleware
{ProcessedMessages} // Number of messages processed

// Error Information
{ErrorMessage}      // Exception message
{ErrorType}         // Exception type name
{StatusCode}        // HTTP status code
```

### Structured Logging Templates

#### LLM Operations
```csharp
// Request initiation
_logger.LogInformation("LLM request initiated: Model={ModelId}, Agent={AgentName}, MessageCount={MessageCount}, Type={RequestType}",
    modelId, agentName, messageCount, requestType);

// Request completion
_logger.LogInformation("LLM request completed: CompletionId={CompletionId}, Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost:F6}, Duration={Duration}ms",
    completionId, model, promptTokens, completionTokens, totalCost, duration);

// Request failure
_logger.LogError(exception, "LLM request failed: Model={ModelId}, Agent={AgentType}, Duration={Duration}ms",
    modelId, agentType, duration);
```

#### Function Execution
```csharp
// Function success
_logger.LogInformation("Function executed: Name={FunctionName}, Duration={Duration}ms, Success={Success}",
    functionName, duration, true);

// Function failure
_logger.LogError(exception, "Function execution failed: Name={FunctionName}, Args={Args}, Duration={Duration}ms",
    functionName, argsJson, duration);
```

#### Usage Data
```csharp
// Usage enrichment (maintain existing format for compatibility)
_logger.LogInformation("Usage data enriched: {{completionId: {CompletionId}, model: {Model}, promptTokens: {PromptTokens}, completionTokens: {CompletionTokens}, totalCost: {TotalCost:F6}, cached: {Cached}}}",
    completionId, model, promptTokens, completionTokens, totalCost, cached);
```

#### Performance Metrics
```csharp
// Streaming performance
_logger.LogDebug("Streaming metrics: CompletionId={CompletionId}, TotalChunks={TotalChunks}, TimeToFirstToken={TimeToFirstToken}ms, TokensPerSecond={TokensPerSecond:F2}",
    completionId, totalChunks, timeToFirstToken, tokensPerSecond);

// Cache performance
_logger.LogDebug("Cache metrics: Operation={Operation}, Key={CacheKey}, Hit={Hit}, Duration={Duration}ms",
    operation, cacheKey, isHit, duration);
```

### Log Message Formatting Guidelines

1. **Start with action**: Begin log messages with what happened
2. **Use present tense**: "Request completed" not "Request has completed"
3. **Be specific**: Include relevant context and identifiers
4. **Use consistent terminology**: Stick to established terms throughout
5. **Include units**: Specify units for measurements (ms, tokens, bytes)

```csharp
// ✅ Good
_logger.LogInformation("Provider resolved: ModelId={ModelId}, Provider={ProviderName}, Duration={Duration}ms",
    modelId, providerName, duration);

// ❌ Avoid
_logger.LogInformation($"The provider {providerName} has been resolved for model {modelId}");
```

## Best Practices

### 1. Use Event IDs for Categorization

```csharp
public static class LogEventIds
{
    // Agent Events (1000-1999)
    public static readonly EventId AgentRequestInitiated = new(1001, "AgentRequestInitiated");
    public static readonly EventId AgentRequestCompleted = new(1002, "AgentRequestCompleted");
    public static readonly EventId AgentRequestFailed = new(1003, "AgentRequestFailed");
    
    // Middleware Events (2000-2999)
    public static readonly EventId FunctionCallExecuted = new(2001, "FunctionCallExecuted");
    public static readonly EventId FunctionCallFailed = new(2002, "FunctionCallFailed");
}

// Usage
_logger.LogInformation(LogEventIds.AgentRequestInitiated, 
    "LLM request initiated: Model={ModelId}", modelId);
```

### 2. Use Scopes for Request Correlation

```csharp
public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
    IEnumerable<IMessage> messages,
    GenerateReplyOptions? options = null,
    CancellationToken cancellationToken = default)
{
    var requestId = Guid.NewGuid().ToString("N")[..8];
    
    using var scope = _logger.BeginScope(new Dictionary<string, object>
    {
        ["RequestId"] = requestId,
        ["ModelId"] = options?.ModelId ?? "default",
        ["Operation"] = "GenerateReply"
    });

    _logger.LogInformation("Request started");
    
    try
    {
        // Implementation
        _logger.LogInformation("Request completed successfully");
        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Request failed");
        throw;
    }
}
```

### 3. Avoid Logging Sensitive Information

```csharp
// ✅ Good - Log metadata only
_logger.LogDebug("API request prepared: Endpoint={Endpoint}, PayloadSize={PayloadSize}",
    endpoint, payload.Length);

// ❌ Avoid - Don't log sensitive content
// _logger.LogDebug("API request: {Payload}", payload);

// ✅ Good - Sanitize or summarize
_logger.LogTrace("Message content: Type={MessageType}, Length={Length}, Preview={Preview}",
    message.GetType().Name, content.Length, content.Substring(0, Math.Min(50, content.Length)));
```

### 4. Use Conditional Logging for Expensive Operations

```csharp
// ✅ Good - Check before expensive operations
if (_logger.IsEnabled(LogLevel.Debug))
{
    var debugInfo = GenerateExpensiveDebugInformation();
    _logger.LogDebug("Debug information: {DebugInfo}", debugInfo);
}

// ✅ Good - Use lazy evaluation
_logger.LogDebug("Complex data: {Data}", () => GenerateComplexData());
```

### 5. Handle Exceptions in Logging Code

```csharp
public void SafeLog(string message, params object[] args)
{
    try
    {
        _logger.LogInformation(message, args);
    }
    catch (Exception ex)
    {
        // Fallback logging or ignore
        Console.WriteLine($"Logging failed: {ex.Message}");
    }
}
```

## Performance Considerations

### 1. Log Level Impact

| Level | Performance Impact | Production Use |
|-------|-------------------|----------------|
| Trace | Very High | Never |
| Debug | High | Rarely (troubleshooting only) |
| Information | Medium | Yes (selective categories) |
| Warning | Low | Yes |
| Error | Very Low | Yes |
| Critical | Minimal | Yes |

### 2. Efficient Logging Patterns

```csharp
// ✅ Efficient - Structured parameters
_logger.LogInformation("Request processed: Duration={Duration}ms, Tokens={Tokens}",
    duration, tokenCount);

// ❌ Inefficient - String interpolation
_logger.LogInformation($"Request processed: Duration={duration}ms, Tokens={tokenCount}");

// ✅ Efficient - Conditional expensive operations
if (_logger.IsEnabled(LogLevel.Debug))
{
    _logger.LogDebug("Detailed info: {Details}", GenerateExpensiveDetails());
}

// ❌ Inefficient - Always generates expensive data
_logger.LogDebug("Detailed info: {Details}", GenerateExpensiveDetails());
```

### 3. Async Logging for High Throughput

```csharp
// Use async sinks for high-volume logging
Log.Logger = new LoggerConfiguration()
    .WriteTo.Async(a => a.Console())
    .WriteTo.Async(a => a.File("logs/app.txt"))
    .CreateLogger();
```

### 4. Sampling for High-Frequency Events

```csharp
private static int _logCounter = 0;

public void LogWithSampling(string message)
{
    // Log every 100th occurrence
    if (Interlocked.Increment(ref _logCounter) % 100 == 0)
    {
        _logger.LogDebug(message);
    }
}
```

## Event IDs and Categories

### Event ID Ranges

| Range | Category | Examples |
|-------|----------|----------|
| 1000-1999 | Agent Events | Request initiation, completion, failures |
| 2000-2999 | Middleware Events | Function calls, message processing |
| 3000-3999 | Provider Events | Provider resolution, API calls |
| 4000-4999 | Performance Events | Metrics, timing, throughput |
| 5000-5999 | Configuration Events | Settings, validation, changes |

### Category Naming Convention

```
AchieveAi.LmDotnetTools.{Component}.{SubComponent}

Examples:
- AchieveAi.LmDotnetTools.LmConfig.Agents.UnifiedAgent
- AchieveAi.LmDotnetTools.LmCore.Middleware.FunctionCallMiddleware
- AchieveAi.LmDotnetTools.OpenAIProvider.Agents.OpenClient
```

## Troubleshooting

### Common Issues and Solutions

#### 1. No Logs Appearing

**Symptoms**: Expected logs are not showing up

**Solutions**:
```csharp
// Check minimum log level
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Check category-specific filters
builder.Logging.AddFilter("AchieveAi.LmDotnetTools", LogLevel.Debug);

// Verify logger is being injected
public MyClass(ILogger<MyClass> logger)
{
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
```

#### 2. Too Many Logs

**Symptoms**: Log volume is overwhelming

**Solutions**:
```csharp
// Increase minimum level for production
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Filter out chatty components
builder.Logging.AddFilter("AchieveAi.LmDotnetTools.LmCore.Middleware", LogLevel.Warning);

// Use sampling for high-frequency events
```

#### 3. Performance Issues

**Symptoms**: Logging is impacting application performance

**Solutions**:
```csharp
// Use async logging
.WriteTo.Async(a => a.Console())

// Check for expensive operations in log messages
if (_logger.IsEnabled(LogLevel.Debug))
{
    // Only do expensive work if debug logging is enabled
}

// Avoid string concatenation in log messages
_logger.LogInformation("Value: {Value}", value); // Good
_logger.LogInformation($"Value: {value}"); // Avoid
```

#### 4. Missing Structured Data

**Symptoms**: Log analysis tools can't parse structured data

**Solutions**:
```csharp
// Use named parameters
_logger.LogInformation("Request completed: Duration={Duration}ms", duration);

// Configure structured output format
.WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")

// Use JSON formatter for structured output
.WriteTo.Console(new JsonFormatter())
```

### Debugging Logging Configuration

```csharp
// Enable internal logging to see what's happening
builder.Logging.AddDebug();

// Add console provider to see immediate output
builder.Logging.AddConsole();

// Check if specific loggers are enabled
var logger = serviceProvider.GetRequiredService<ILogger<MyClass>>();
Console.WriteLine($"Debug enabled: {logger.IsEnabled(LogLevel.Debug)}");
Console.WriteLine($"Info enabled: {logger.IsEnabled(LogLevel.Information)}");
```

### Log Analysis Queries

For structured logs, use these patterns for analysis:

```sql
-- Find all failed requests
SELECT * FROM logs 
WHERE Level = 'Error' 
AND Properties.EventId = 1003

-- Analyze performance by model
SELECT Properties.ModelId, AVG(Properties.Duration) as AvgDuration
FROM logs 
WHERE Properties.EventId = 1002
GROUP BY Properties.ModelId

-- Find high-cost operations
SELECT * FROM logs 
WHERE Properties.TotalCost > 0.01
ORDER BY Properties.TotalCost DESC
```

## Migration Guide

### Upgrading from Previous Versions

If you're upgrading from a version without comprehensive logging:

1. **Add logger parameters** to your component constructors
2. **Update factory methods** to pass loggers
3. **Configure logging providers** in your application startup
4. **Adjust log levels** based on your environment needs
5. **Test logging output** to ensure it meets your requirements

### Backward Compatibility

The logging system is designed to be backward compatible:
- All logger parameters are optional
- Components work without loggers (using NullLogger)
- Existing code continues to function unchanged
- New logging can be enabled incrementally

This comprehensive logging system provides the visibility and debugging capabilities needed for production LLM applications while maintaining performance and flexibility.