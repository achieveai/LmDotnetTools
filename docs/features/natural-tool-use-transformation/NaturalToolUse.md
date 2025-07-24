# Natural Tool Use Transformation

## Overview

The Natural Tool Use Transformation feature provides bidirectional conversion between structured tool calls and natural language format. While the existing `NaturalToolUseParserMiddleware` converts natural text with XML-style tool calls into structured `ToolsCallMessage` objects, this feature provides the reverse transformation.

## Quick Start

### Basic Usage

```csharp
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

// Transform a single aggregate message
var naturalFormat = aggregateMessage.ToNaturalToolUse();

// Transform a collection of messages
var transformedMessages = messageCollection.ToNaturalToolUse();

// Combine a message sequence with natural formatting
var combined = messageSequence.CombineAsNaturalToolUse();
```

### Using the Core Transformer Directly

```csharp
// Direct transformation
var naturalMessage = ToolsCallAggregateTransformer.TransformToNaturalFormat(aggregateMessage);

// Combine message sequences
var combinedMessage = ToolsCallAggregateTransformer.CombineMessageSequence(messages);
```

## Transformation Direction

### Forward Direction (Existing)
```
Natural Text with <tool_call> blocks → ToolsCallMessage + ToolsCallResultMessage
```

### Reverse Direction (This Feature)
```
ToolsCallAggregateMessage → Natural Text with <tool_call> and <tool_response> blocks
```

## XML Format Specification

The transformation produces XML-style blocks embedded within natural text:

### Single Tool Call
```xml
<tool_call name="GetWeather">
{
  "location": "San Francisco, CA",
  "unit": "celsius"
}
</tool_call>
<tool_response name="GetWeather">
Temperature is 22°C with partly cloudy skies and light winds from the west.
</tool_response>
```

### Multiple Tool Calls
Multiple tool call/response pairs are separated by `---`:

```xml
<tool_call name="GetWeather">
{
  "location": "San Francisco, CA",
  "unit": "celsius"  
}
</tool_call>
<tool_response name="GetWeather">
Temperature is 22°C with partly cloudy skies.
</tool_response>
---
<tool_call name="GetTime">
{
  "timezone": "America/Los_Angeles"
}
</tool_call>
<tool_response name="GetTime">
{
  "current_time": "2024-07-24T15:30:00-07:00",
  "timezone": "PDT",
  "formatted": "3:30 PM PDT"
}
</tool_response>
```

### Complex JSON Responses
Tool responses are automatically pretty-printed if they contain valid JSON:

```xml
<tool_call name="SearchDatabase">
{
  "query": "customers in California",
  "limit": 10
}
</tool_call>
<tool_response name="SearchDatabase">
{
  "results": [
    {
      "name": "John Doe",
      "city": "San Francisco"
    },
    {
      "name": "Jane Smith", 
      "city": "Los Angeles"
    }
  ],
  "total": 25
}
</tool_response>
```

## Usage Examples

### Example 1: Single Message Transformation

```csharp
// Create a sample aggregate message
var toolCall = new ToolCall("GetWeather", "{\"location\":\"Paris\",\"unit\":\"celsius\"}");
var toolResult = new ToolCallResult(null, "Sunny, 25°C with clear skies");

var toolCallMessage = new ToolsCallMessage
{
    ToolCalls = ImmutableList.Create(toolCall),
    Role = Role.Assistant,
    GenerationId = "gen-123"
};

var toolResultMessage = new ToolsCallResultMessage
{
    ToolCallResults = ImmutableList.Create(toolResult)
};

var aggregateMessage = new ToolsCallAggregateMessage(toolCallMessage, toolResultMessage, "weather-agent");

// Transform to natural format
var naturalFormat = aggregateMessage.ToNaturalToolUse();

Console.WriteLine(naturalFormat.Text);
/*
Output:
<tool_call name="GetWeather">
{
  "location": "Paris",
  "unit": "celsius"
}
</tool_call>
<tool_response name="GetWeather">
Sunny, 25°C with clear skies
</tool_response>
*/
```

### Example 2: Message Sequence Combination

```csharp
// Create a conversation sequence
var messages = new IMessage[]
{
    new TextMessage 
    { 
        Text = "I'll check the weather for you.", 
        Role = Role.Assistant 
    },
    aggregateMessage, // Tool call/response
    new TextMessage 
    { 
        Text = "Based on this forecast, it's a great day for outdoor activities!", 
        Role = Role.Assistant 
    }
};

// Combine into single natural format message
var combined = messages.CombineAsNaturalToolUse();

Console.WriteLine(combined.Text);
/*
Output:
I'll check the weather for you.

<tool_call name="GetWeather">
{
  "location": "Paris",
  "unit": "celsius"
}
</tool_call>
<tool_response name="GetWeather">
Sunny, 25°C with clear skies
</tool_response>

Based on this forecast, it's a great day for outdoor activities!
*/
```

### Example 3: Collection Transformation

```csharp
var messageCollection = new IMessage[]
{
    new TextMessage { Text = "Hello", Role = Role.User },
    aggregateMessage,
    new TextMessage { Text = "Goodbye", Role = Role.Assistant }
};

// Transform only the aggregate messages, leave others unchanged
var transformed = messageCollection.ToNaturalToolUse().ToList();

// Result: 3 messages where only the middle one is transformed
```

### Example 4: Conditional Transformation

```csharp
// Check if transformation is applicable
if (message.IsTransformableToolCall())
{
    var natural = message.ToNaturalToolUse();
    // Process transformed message
}

if (messageCollection.ContainsTransformableToolCalls())
{
    var transformed = messageCollection.ToNaturalToolUse();
    // Process collection with transformed messages
}
```

## Advanced Features

### Metadata Preservation

All message metadata is preserved during transformation:

```csharp
var originalMetadata = ImmutableDictionary.Create<string, object>()
    .Add("source", "weather_service")
    .Add("confidence", 0.95);

// Metadata is preserved in the transformed message
var transformed = aggregateMessage.ToNaturalToolUse();
Assert.Equal(originalMetadata["source"], transformed.Metadata["source"]);
```

### Error Handling

The transformation includes graceful error handling:

```csharp
// Invalid or malformed messages are returned unchanged
var invalidMessage = new TextMessage { Text = "Regular text", Role = Role.User };
var result = invalidMessage.ToNaturalToolUse(); // Returns the same instance

// Transformation failures fall back to original message
var problematicAggregate = CreateProblematicAggregateMessage();
var fallback = problematicAggregate.ToNaturalToolUse(); // Graceful fallback
```

### JSON Pretty-Printing

Tool arguments and responses are automatically formatted for readability:

```csharp
// Compact JSON input
var compactJson = "{\"name\":\"John\",\"age\":30,\"skills\":[\"programming\",\"design\"]}";
var toolCall = new ToolCall("ProcessUser", compactJson);

// Automatically formatted output
/*
<tool_call name="ProcessUser">
{
  "name": "John",
  "age": 30,
  "skills": [
    "programming",
    "design"
  ]
}
</tool_call>
*/
```

## Integration Points

### Extension Methods

The feature provides convenient extension methods in `MessageExtensions`:

- `ToNaturalToolUse()` - Transform individual messages or collections
- `CombineAsNaturalToolUse()` - Combine message sequences with natural formatting
- `IsTransformableToolCall()` - Check if a message can be transformed
- `ContainsTransformableToolCalls()` - Check if a collection has transformable messages

### Core Transformer

The `ToolsCallAggregateTransformer` class provides the core transformation logic:

- `TransformToNaturalFormat()` - Main transformation method
- `CombineMessageSequence()` - Message sequence combination
- `FormatToolCallAndResponse()` - Individual tool call formatting

## Message Flow

### Before Transformation
```
ToolsCallAggregateMessage {
  ToolsCallMessage: { ToolCalls: [...] }
  ToolsCallResultMessage: { ToolCallResults: [...] }
  Metadata: { ... }
}
```

### After Transformation  
```
TextMessage {
  Text: "I'll help you.\n\n<tool_call name=\"GetWeather\">...\n</tool_call>\n<tool_response name=\"GetWeather\">...\n</tool_response>\n\nHope that helps!"
  Role: Assistant
  Metadata: { ... } // Preserved from original
}
```

## Best Practices

### When to Use

- **API Responses**: Converting structured tool calls back to readable format for user display
- **Logging**: Creating human-readable logs of tool interactions
- **Debugging**: Understanding tool call flows in natural language
- **Documentation**: Generating examples and documentation from actual tool usage

### Performance Considerations

- Transformation is lightweight and fast
- JSON pretty-printing adds minimal overhead
- Metadata merging is efficient with immutable data structures
- No network calls or external dependencies

### Error Recovery

- Always check `IsTransformableToolCall()` before transformation if error handling is critical
- Use `CombineAsNaturalToolUse()` for robust message sequence processing
- Original messages are preserved when transformation fails

## Compatibility

### Framework Support
- .NET 8.0+
- .NET 9.0+

### Message Types
- ✅ `ToolsCallAggregateMessage` - Primary transformation target
- ✅ `TextMessage` - Pass-through unchanged
- ✅ `IMessage` collections - Selective transformation
- ✅ Mixed message sequences - Combines with transformation

### Provider Compatibility
- Works with all LLM providers
- Provider-agnostic transformation
- Consistent output format regardless of source

## Troubleshooting

### Common Issues

**Q: Extension methods not available**
A: Ensure you have `using AchieveAi.LmDotnetTools.LmCore.Messages;` in your file.

**Q: Transformation returns original message**
A: Check if the message is actually a `ToolsCallAggregateMessage` using `IsTransformableToolCall()`.

**Q: JSON not pretty-printed**
A: The transformer automatically detects and formats valid JSON. Invalid JSON is left as-is.

**Q: Metadata not preserved**
A: Metadata is automatically preserved. Check the source message had metadata to begin with.

### Debugging

Enable detailed logging to see transformation behavior:

```csharp
// Check transformation applicability
Console.WriteLine($"Is transformable: {message.IsTransformableToolCall()}");

// Examine message type
Console.WriteLine($"Message type: {message.GetType().Name}");

// Check for tool calls in collections
Console.WriteLine($"Contains tool calls: {messages.ContainsTransformableToolCalls()}");
```

## See Also

- [Design Document](design.md) - Detailed architectural design
- [Requirements](requirements.md) - Feature requirements and specifications  
- [Task List](tasks.md) - Implementation tasks and progress
- [NaturalToolUseParserMiddleware](../../../src/LmCore/Middleware/NaturalToolUseParserMiddleware.cs) - Forward transformation
