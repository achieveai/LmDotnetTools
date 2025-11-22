# Simplified Message Flow Architecture

## Overview

The Simplified Message Flow is a new architecture that improves message handling in LM .NET Tools by introducing deterministic message ordering and enabling better KV cache optimization.

### Key Improvements

1. **Deterministic Message Ordering**: All messages within a generation are assigned a `messageOrderIdx` (0, 1, 2...)
2. **Tool Call Indexing**: Tool calls within a message are assigned a `toolCallIdx` for deterministic ordering
3. **Explicit Agentic Loop Control**: Application code manually controls tool execution via `ToolCallExecutor`
4. **KV Cache Optimization**: Deterministic ordering allows LLM providers to reuse cached computations
5. **Backward Compatibility**: Existing code continues to work through automatic transformation

## Architecture

### Message Flow Diagram

```
Application Code
     ↓ (Ordered Messages: MessageOrderIdx assigned)
MessageTransformationMiddleware
     ↓ UPSTREAM: Reconstruct aggregates
     ↓ (CompositeMessage, ToolsCallAggregateMessage)
Provider Agent (OpenAI, Anthropic, etc.)
     ↓ (Raw messages from LLM)
MessageTransformationMiddleware
     ↓ DOWNSTREAM: Assign messageOrderIdx
     ↑ (Ordered Messages)
Application Code
```

### Core Components

#### 1. MessageTransformationMiddleware

Bidirectional middleware that transforms messages based on flow direction:

**Downstream (Provider → Application)**:
- Assigns `messageOrderIdx` to messages with the same `GenerationId`
- Ordering restarts at 0 for each new `GenerationId`
- Messages without `GenerationId` pass through unchanged

**Upstream (Application → Provider)**:
- Reconstructs `CompositeMessage` from multiple messages with same `GenerationId`
- Reconstructs `ToolsCallAggregateMessage` from `ToolsCallMessage` + `ToolsCallResultMessage`
- Single messages pass through unchanged

```csharp
var middleware = new MessageTransformationMiddleware();
var agent = new MiddlewareWrappingAgent(providerAgent, middleware);
```

#### 2. ToolCallExecutor

Stateless utility for executing tool calls from `ToolsCallMessage`:

```csharp
// Define your tools
var functionMap = new Dictionary<string, Func<string, Task<string>>>
{
    ["get_weather"] = async args =>
    {
        var data = JsonSerializer.Deserialize<WeatherArgs>(args);
        return await GetWeatherAsync(data.Location);
    }
};

// Execute tool calls
var toolResult = await ToolCallExecutor.ExecuteAsync(
    toolCallMessage,
    functionMap
);
```

#### 3. ToolCallInjectionMiddleware

Lightweight middleware that injects tool definitions into options (without executing):

```csharp
var functions = new[]
{
    new FunctionContract
    {
        Name = "get_weather",
        Description = "Get weather for a location",
        Parameters = [...]
    }
};

var middleware = new ToolCallInjectionMiddleware(functions);
var agent = new MiddlewareWrappingAgent(providerAgent, middleware);
```

## Message Properties

### messageOrderIdx

Assigned to all messages with a `GenerationId`:
- Type: `int?`
- Scope: Per-generation (restarts at 0 for each `GenerationId`)
- Purpose: Deterministic ordering for KV cache optimization

```csharp
// Example: First generation
var msg1 = new TextMessage
{
    Text = "Hello",
    GenerationId = "gen1",
    MessageOrderIdx = 0  // First message
};

var msg2 = new TextMessage
{
    Text = "World",
    GenerationId = "gen1",
    MessageOrderIdx = 1  // Second message
};

// Example: Second generation (restarts at 0)
var msg3 = new TextMessage
{
    Text = "Response",
    GenerationId = "gen2",
    MessageOrderIdx = 0  // Restarts for new generation
};
```

### toolCallIdx

Assigned to each `ToolCall` within a `ToolsCallMessage`:
- Type: `int`
- Scope: Per-message
- Purpose: Deterministic tool call ordering

```csharp
var toolCallMsg = new ToolsCallMessage
{
    ToolCalls =
    [
        new ToolCall("get_weather", "{\"location\":\"SF\"}")
        {
            ToolCallId = "call_1",
            ToolCallIdx = 0  // First tool call
        },
        new ToolCall("get_weather", "{\"location\":\"NYC\"}")
        {
            ToolCallId = "call_2",
            ToolCallIdx = 1  // Second tool call
        }
    ],
    GenerationId = "gen1",
    MessageOrderIdx = 0
};
```

## Usage Examples

### Basic Agentic Loop

```csharp
// 1. Setup agent with MessageTransformationMiddleware
var provider = new OpenAgent("gpt-4", apiKey);
var middleware = new MessageTransformationMiddleware();
var agent = new MiddlewareWrappingAgent(provider, middleware);

// 2. Define tools
var functions = new[]
{
    new FunctionContract
    {
        Name = "get_weather",
        Description = "Get weather for a location"
    }
};

var functionMap = new Dictionary<string, Func<string, Task<string>>>
{
    ["get_weather"] = async args => await GetWeatherAsync(args)
};

// 3. Initial user message
var conversationHistory = new List<IMessage>
{
    new TextMessage
    {
        Text = "What's the weather in San Francisco?",
        Role = Role.User
    }
};

// 4. Agentic loop
while (true)
{
    // Call LLM
    var response = await agent.GenerateReplyAsync(
        conversationHistory,
        new GenerateReplyOptions { Functions = functions }
    );

    var messages = response.ToList();
    conversationHistory.AddRange(messages);

    // Check for tool calls
    var toolCallMsg = messages.OfType<ToolsCallMessage>().FirstOrDefault();
    if (toolCallMsg == null)
    {
        // No more tool calls, we're done
        break;
    }

    // Execute tools
    var toolResult = await ToolCallExecutor.ExecuteAsync(
        toolCallMsg,
        functionMap
    );

    conversationHistory.Add(toolResult);
}

// 5. Get final response
var finalText = conversationHistory
    .OfType<TextMessage>()
    .Last()
    .Text;
```

### Streaming with Simplified Flow

```csharp
var provider = new OpenAgent("gpt-4", apiKey);
var middleware = new MessageTransformationMiddleware();
var streamingAgent = new MiddlewareWrappingStreamingAgent(provider, middleware);

var stream = await streamingAgent.GenerateReplyStreamingAsync(messages);

await foreach (var message in stream)
{
    // Each message has messageOrderIdx assigned
    Console.WriteLine($"[{message.MessageOrderIdx}] {message.GetType().Name}");

    if (message is TextUpdateMessage textUpdate)
    {
        Console.Write(textUpdate.Text);
    }
}
```

### Multi-Tool Execution

```csharp
var functionMap = new Dictionary<string, Func<string, Task<string>>>
{
    ["get_weather"] = async args =>
    {
        var data = JsonSerializer.Deserialize<WeatherRequest>(args);
        return JsonSerializer.Serialize(await GetWeatherAsync(data.Location));
    },
    ["get_time"] = async args =>
    {
        var data = JsonSerializer.Deserialize<TimeRequest>(args);
        return JsonSerializer.Serialize(await GetTimeAsync(data.Timezone));
    }
};

var toolCallMsg = new ToolsCallMessage
{
    ToolCalls =
    [
        new ToolCall("get_weather", "{\"location\":\"SF\"}") { ToolCallIdx = 0 },
        new ToolCall("get_time", "{\"timezone\":\"PST\"}") { ToolCallIdx = 1 }
    ]
};

// Execute all tool calls
var results = await ToolCallExecutor.ExecuteAsync(toolCallMsg, functionMap);

// results.ToolCallResults contains 2 results in order
Assert.Equal(2, results.ToolCallResults.Count);
```

## Migration Guide

### From FunctionCallMiddleware

**Old Approach (Automatic)**:
```csharp
var functions = [...];
var functionMap = new Dictionary<string, Func<string, Task<string>>> { ... };
var middleware = new FunctionCallMiddleware(functions, functionMap);
var agent = new MiddlewareWrappingAgent(provider, middleware);

// Tools execute automatically
var response = await agent.GenerateReplyAsync(messages);
```

**New Approach (Explicit)**:
```csharp
var functions = [...];
var functionMap = new Dictionary<string, Func<string, Task<string>>> { ... };

// Use ToolCallInjectionMiddleware + manual execution
var injectionMiddleware = new ToolCallInjectionMiddleware(functions);
var transformationMiddleware = new MessageTransformationMiddleware();

var agent = new MiddlewareWrappingAgent(
    new MiddlewareWrappingAgent(provider, injectionMiddleware),
    transformationMiddleware
);

// Manual tool execution in agentic loop
var response = await agent.GenerateReplyAsync(messages);
var toolCallMsg = response.OfType<ToolsCallMessage>().FirstOrDefault();
if (toolCallMsg != null)
{
    var toolResult = await ToolCallExecutor.ExecuteAsync(toolCallMsg, functionMap);
    // Add toolResult to conversation and continue
}
```

**Note**: `FunctionCallMiddleware` still works for backward compatibility but uses the new components internally.

### Middleware Ordering

The recommended middleware order is:

```
Application
    ↓
MessageTransformationMiddleware (outermost)
    ↓
ToolCallInjectionMiddleware
    ↓
Other Middlewares (logging, fallback, etc.)
    ↓
Provider Agent
```

Example:
```csharp
var agent = provider
    .WithMessageTransformation()    // Assigns ordering, reconstructs aggregates
    .WithToolCallInjection(functions)  // Injects tool definitions
    .WithLogging(logger);              // Additional middleware
```

## Benefits

### 1. KV Cache Optimization

Deterministic ordering allows LLM providers to reconstruct identical input:

```
Turn 1:
[0] ToolsCallMessage (gen1)
[1] ToolsCallResultMessage (gen1)

Turn 2: Provider can cache and reuse computation for [0] and [1]
[0] ToolsCallMessage (gen1)      ← Cached
[1] ToolsCallResultMessage (gen1) ← Cached
[0] TextMessage (gen2)           ← Only this needs processing
```

### 2. Explicit Control

Application code controls when tools execute:

```csharp
// Can add validation, logging, error handling
if (toolCallMsg != null)
{
    logger.LogInformation("Executing {Count} tools", toolCallMsg.ToolCalls.Count);

    try
    {
        var result = await ToolCallExecutor.ExecuteAsync(toolCallMsg, functionMap);
        conversationHistory.Add(result);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Tool execution failed");
        // Add error message to conversation
    }
}
```

### 3. Testing

Easier to test with explicit execution:

```csharp
// Test tool execution independently
var toolCallMsg = new ToolsCallMessage { ... };
var result = await ToolCallExecutor.ExecuteAsync(toolCallMsg, functionMap);
Assert.Equal("expected", result.ToolCallResults[0].Result);

// Test agent without tool execution
var mockAgent = new MockAgent();
var response = await agent.GenerateReplyAsync(messages);
Assert.IsType<ToolsCallMessage>(response.First());
```

## Advanced Scenarios

### Custom Tool Execution Logic

```csharp
// Custom execution with retries
var toolCallMsg = messages.OfType<ToolsCallMessage>().FirstOrDefault();
if (toolCallMsg != null)
{
    var result = await RetryHelper.ExecuteWithRetryAsync(
        async () => await ToolCallExecutor.ExecuteAsync(toolCallMsg, functionMap),
        maxRetries: 3
    );

    conversationHistory.Add(result);
}
```

### Selective Tool Execution

```csharp
// Only execute specific tools
var toolCallMsg = messages.OfType<ToolsCallMessage>().FirstOrDefault();
if (toolCallMsg != null)
{
    var allowedTools = new[] { "get_weather", "get_time" };

    var filteredToolCalls = toolCallMsg.ToolCalls
        .Where(tc => allowedTools.Contains(tc.FunctionName))
        .ToList();

    var filteredMessage = toolCallMsg with
    {
        ToolCalls = filteredToolCalls.ToImmutableList()
    };

    var result = await ToolCallExecutor.ExecuteAsync(filteredMessage, functionMap);
    conversationHistory.Add(result);
}
```

### Tool Execution Callbacks

```csharp
var resultCallback = new ToolResultCallback(
    onExecuting: (toolCall) =>
    {
        Console.WriteLine($"Executing: {toolCall.FunctionName}");
    },
    onExecuted: (toolCall, result) =>
    {
        Console.WriteLine($"Completed: {toolCall.FunctionName} → {result}");
    }
);

var result = await ToolCallExecutor.ExecuteAsync(
    toolCallMsg,
    functionMap,
    resultCallback: resultCallback
);
```

## Best Practices

1. **Always use MessageTransformationMiddleware** when working with provider agents
2. **Prefer explicit tool execution** over automatic execution for better control
3. **Use ToolCallInjectionMiddleware** to inject tool definitions without execution
4. **Leverage messageOrderIdx** for caching and debugging
5. **Test bidirectional transformation** in integration tests
6. **Handle tool execution errors** gracefully in application code

## Troubleshooting

### Messages missing messageOrderIdx

Ensure `MessageTransformationMiddleware` is in the middleware pipeline:

```csharp
var agent = provider.WithMessageTransformation();
```

### Tool calls not being aggregated for provider

Verify middleware order - `MessageTransformationMiddleware` should be outermost:

```csharp
// Correct
var agent = provider
    .WithToolCallInjection(functions)
    .WithMessageTransformation();

// Incorrect
var agent = provider
    .WithMessageTransformation()
    .WithToolCallInjection(functions);
```

### Tool execution not working

Ensure `ToolCallExecutor` is called in your agentic loop:

```csharp
var toolCallMsg = response.OfType<ToolsCallMessage>().FirstOrDefault();
if (toolCallMsg != null)
{
    var result = await ToolCallExecutor.ExecuteAsync(toolCallMsg, functionMap);
    conversationHistory.Add(result);
}
```

## API Reference

See the following files for detailed API documentation:
- [MessageTransformationMiddleware.cs](../src/LmCore/Middleware/MessageTransformationMiddleware.cs)
- [ToolCallExecutor.cs](../src/LmCore/Middleware/ToolCallExecutor.cs)
- [ToolCallInjectionMiddleware.cs](../src/LmCore/Middleware/ToolCallInjectionMiddleware.cs)
- [IMessage.cs](../src/LmCore/Messages/IMessage.cs)
- [ToolsCallMessage.cs](../src/LmCore/Messages/ToolsCallMessage.cs)
