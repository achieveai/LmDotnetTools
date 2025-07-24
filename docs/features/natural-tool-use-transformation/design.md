# Design Document: Natural Tool Use Transformation

## Design Philosophy

This feature implements **surgical changes** to the existing architecture rather than a complete rewrite. The design leverages the existing middleware patterns and message infrastructure to provide reverse transformation capabilities while maintaining full backward compatibility.

**Core Principle**: Enable transparent SDK interface consistency across different model capabilities through composable, opt-in transformation layers.

## High-Level Architecture

### Component Overview

```
Request Flow (User → Agent):
User Request 
    ↓
┌─────────────────────────────────────────────────────────────┐
│ NaturalToolUseTransformationMiddleware (NEW)               │
│   ┌─────────────────────────────────────────────────────────┤
│   │ FunctionCallMiddleware (existing)                       │
│   │   ┌─────────────────────────────────────────────────────┤
│   │   │ NaturalToolUseParserMiddleware (existing)           │
│   │   │   ┌─────────────────────────────────────────────────┤
│   │   │   │ Core Agent                                      │
│   │   │   └─────────────────────────────────────────────────┤
│   │   └─────────────────────────────────────────────────────┤
│   └─────────────────────────────────────────────────────────┤
└─────────────────────────────────────────────────────────────┘
    ↓
Response Flow (Agent → User):
Core Agent Response (natural text with tool calls)
    ↑
NaturalToolUseParserMiddleware (parses tool calls → ToolsCallMessage)
    ↑  
FunctionCallMiddleware (executes tools → ToolsCallAggregateMessage)
    ↑
NaturalToolUseTransformationMiddleware (transforms back → natural format)
    ↑
Final Response to User
```

### Design Approach: Post-Processing Middleware

**Rationale**: The transformation middleware operates as a post-processor after `FunctionCallMiddleware`, intercepting `ToolsCallAggregateMessage` instances and converting them back to natural language format.

**Benefits**:
- Clean separation of concerns
- Follows existing architectural patterns
- Composable and configurable
- Works consistently across all providers
- Opt-in behavior with no impact on existing functionality

## Detailed Design

### Core Components

#### 1. NaturalToolUseTransformationMiddleware

**Location**: `src/LmCore/Middleware/NaturalToolUseTransformationMiddleware.cs`

```csharp
public class NaturalToolUseTransformationMiddleware : IStreamingMiddleware
{
    private readonly TransformationOptions _options;
    private readonly ILogger<NaturalToolUseTransformationMiddleware> _logger;
    
    public string? Name { get; }
    
    public NaturalToolUseTransformationMiddleware(
        TransformationOptions? options = null,
        ILogger<NaturalToolUseTransformationMiddleware>? logger = null,
        string? name = null)
    
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default)
        
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
}
```

**Responsibilities**:
- Intercept response messages from downstream agents
- Identify message sequences containing ToolsCallAggregateMessage
- Delegate transformation to ToolsCallAggregateTransformer
- Handle both streaming and non-streaming scenarios
- Manage message buffering for sequence analysis

#### 2. ToolsCallAggregateTransformer

**Location**: `src/LmCore/Middleware/ToolsCallAggregateTransformer.cs`

```csharp
public static class ToolsCallAggregateTransformer
{
    public static TextMessage TransformToNaturalFormat(
        ToolsCallAggregateMessage aggregateMessage,
        TransformationOptions options)
        
    public static TextMessage CombineMessageSequence(
        IEnumerable<IMessage> messageSequence,
        TransformationOptions options)
        
    public static string FormatToolCallAndResponse(
        ToolCall toolCall,
        ToolCallResult result,
        TransformationOptions options)
        
    private static string FormatToolResponse(
        string result,
        TransformationOptions options)
}
```

**Responsibilities**:
- Core transformation logic (pure functions)
- XML formatting for tool calls and responses
- Message sequence combination
- Metadata merging strategies
- Content formatting and pretty-printing

#### 3. MessageSequenceProcessor

**Location**: `src/LmCore/Middleware/MessageSequenceProcessor.cs`

```csharp
public class MessageSequenceProcessor
{
    public static IEnumerable<MessageGroup> AnalyzeSequences(
        IEnumerable<IMessage> messages)
        
    public static bool CanCombineSequence(
        IEnumerable<IMessage> sequence)
        
    private static MessageGroup CreateMessageGroup(
        IEnumerable<IMessage> messages,
        MessageGroupType groupType)
}

public class MessageGroup
{
    public IEnumerable<IMessage> Messages { get; }
    public MessageGroupType GroupType { get; }
    public bool RequiresTransformation { get; }
}

public enum MessageGroupType
{
    PassThrough,
    TransformableSequence,
    StandaloneAggregate
}
```

**Responsibilities**:
- Analyze message sequences for transformation opportunities
- Group messages into transformable units
- Determine combination strategies
- Handle complex message chain scenarios

#### 4. TransformationOptions

**Location**: `src/LmCore/Middleware/TransformationOptions.cs`

```csharp
public class TransformationOptions
{
    public bool EnableTransformation { get; init; } = true;
    public bool PrettyPrintJson { get; init; } = true;
    public int MaxResponseLength { get; init; } = 4000;
    public string ToolSeparator { get; init; } = "---";
    public RoleAssignmentStrategy RoleStrategy { get; init; } = RoleAssignmentStrategy.PreserveOriginal;
    public MetadataMergeStrategy MetadataStrategy { get; init; } = MetadataMergeStrategy.LastWins;
}

public enum RoleAssignmentStrategy
{
    PreserveOriginal,
    AlwaysUser,
    AlwaysAssistant,
    BasedOnContent
}

public enum MetadataMergeStrategy
{
    LastWins,
    FirstWins,
    Combine
}
```

### Message Flow Design

#### Non-Streaming Flow

```
1. Agent produces: [TextMessage, ToolsCallAggregateMessage, TextMessage]
2. MessageSequenceProcessor identifies transformable sequence
3. ToolsCallAggregateTransformer combines into single TextMessage
4. Output: [Combined TextMessage with embedded XML tool calls]
```

#### Streaming Flow

```
1. Buffer incoming messages until complete sequence identified
2. For each complete sequence:
   a. Check if contains ToolsCallAggregateMessage
   b. If yes, transform and yield combined message
   c. If no, yield messages as-is
3. Handle partial sequences at stream end
```

### Transformation Algorithm

#### Core Algorithm

```
FOR each message sequence:
  IF sequence contains ToolsCallAggregateMessage:
    1. Extract text content from TextMessages
    2. For each ToolsCallAggregateMessage:
       a. Extract tool calls and results
       b. Format as XML: <tool_call name="X">args</tool_call><tool_response name="X">result</tool_response>
       c. Handle multiple tool calls with separators
    3. Combine: prefix_text + tool_xml + suffix_text
    4. Create new TextMessage with combined content
    5. Merge metadata from all source messages
  ELSE:
    Pass through unchanged
```

#### XML Format Specification

```xml
<tool_call name="FunctionName">
{
  "arg1": "value1",
  "arg2": "value2"
}
</tool_call>
<tool_response name="FunctionName">
Simple text response or formatted JSON
</tool_response>
---
<tool_call name="AnotherFunction">
{"param": "value"}
</tool_call>
<tool_response name="AnotherFunction">
{
  "complex": "response",
  "data": ["array", "values"]
}
</tool_response>
```

### Integration Design

#### Provider Integration

**No Changes Required**: Existing providers work unchanged. Transformation happens at the middleware layer before messages reach provider adapters.

**Optional Enhancement**: Providers can detect natural format and optimize accordingly.

#### Configuration Integration

```csharp
// Extension method for easy configuration
public static class MiddlewareExtensions
{
    public static IAgent WithNaturalToolUseTransformation(
        this IAgent agent,
        TransformationOptions? options = null)
    {
        return new WrappedAgent(agent, 
            new NaturalToolUseTransformationMiddleware(options));
    }
}

// Usage example
var agent = new SomeAgent()
    .WithNaturalToolUseTransformation(new TransformationOptions 
    { 
        EnableTransformation = true,
        PrettyPrintJson = true 
    });
```

### Error Handling Strategy

#### Graceful Degradation

1. **Transformation Failures**: Log warning and pass through original messages
2. **Malformed Messages**: Validate structure and skip invalid messages
3. **Resource Limits**: Respect size limits and truncate if necessary
4. **Streaming Errors**: Buffer appropriately and handle partial sequences

#### Logging and Observability

```csharp
// Key logging points
_logger.LogDebug("Analyzing message sequence of {Count} messages", messages.Count());
_logger.LogInformation("Transformed {Count} ToolsCallAggregateMessage instances", transformedCount);
_logger.LogWarning("Failed to transform message {MessageId}: {Error}", messageId, error);
_logger.LogError("Transformation middleware encountered error: {Error}", error);
```

### Performance Considerations

#### Optimization Strategies

1. **Lazy Evaluation**: Process messages only when transformation is needed
2. **Streaming Efficiency**: Minimize buffering, yield early when possible
3. **Memory Management**: Dispose of large intermediate objects promptly
4. **Conditional Processing**: Skip analysis when no ToolsCallAggregateMessage present

#### Performance Monitoring

- Message processing latency
- Memory usage during buffering
- Transformation success/failure rates
- Stream buffer sizes

## File Structure

```
src/LmCore/Middleware/
├── NaturalToolUseTransformationMiddleware.cs
├── ToolsCallAggregateTransformer.cs
├── MessageSequenceProcessor.cs
└── TransformationOptions.cs

src/LmCore/Messages/
└── MessageExtensions.cs (add new methods)

tests/LmCore.Tests/Middleware/
├── NaturalToolUseTransformationMiddlewareTests.cs
├── ToolsCallAggregateTransformerTests.cs
└── MessageSequenceProcessorTests.cs

docs/features/natural-tool-use-transformation/
├── requirements.md (existing)
├── design.md (this document)
└── api-examples.md (usage examples)
```

## Testing Strategy

### Unit Tests

1. **ToolsCallAggregateTransformer**: Test pure transformation functions
2. **MessageSequenceProcessor**: Test sequence analysis and grouping
3. **TransformationOptions**: Test configuration scenarios
4. **XML Formatting**: Test various tool call/response formats

### Integration Tests

1. **Full Pipeline**: Test complete middleware integration
2. **Streaming Scenarios**: Test buffering and yielding behavior
3. **Error Handling**: Test graceful degradation scenarios
4. **Provider Compatibility**: Test with different provider implementations

### Example Test Cases

```csharp
[Fact]
public void TransformToNaturalFormat_SingleToolCall_ProducesCorrectXmlFormat()

[Fact]
public void CombineMessageSequence_TextPlusAggregatePlusText_CombinesCorrectly()

[Fact]
public void StreamingTransformation_PartialSequences_BuffersAppropriately()

[Fact]
public void TransformationFailure_InvalidMessage_PassesThroughGracefully()
```

## Migration and Deployment

### Backward Compatibility

- **Zero Breaking Changes**: All existing code continues to work unchanged
- **Opt-in Behavior**: Transformation only occurs when explicitly configured
- **Default Disabled**: New middleware is not applied by default

### Deployment Strategy

1. **Phase 1**: Core transformation logic (non-breaking)
2. **Phase 2**: Middleware integration and configuration
3. **Phase 3**: Provider-specific optimizations (optional)
4. **Phase 4**: Documentation and usage examples

### Configuration Migration

```csharp
// Before: Standard pipeline
var agent = new SomeAgent();

// After: With natural tool use transformation (opt-in)
var agent = new SomeAgent()
    .WithNaturalToolUseTransformation();
```

This design provides a robust, performant, and maintainable solution that meets all requirements while preserving the existing architecture's strengths.
