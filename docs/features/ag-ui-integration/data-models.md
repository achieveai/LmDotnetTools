# AG-UI Data Models and Event Types

This document provides detailed specifications for all data structures, event types, and transformations used in the AG-UI protocol integration.

## Table of Contents
1. [AG-UI Event Types](#ag-ui-event-types)
2. [Data Transfer Objects](#data-transfer-objects)
3. [Message Conversion Logic](#message-conversion-logic)
4. [JSON Schemas](#json-schemas)
5. [Tool Call Management](#tool-call-management)

## AG-UI Event Types

The AG-UI protocol defines 16 standard event types organized into four categories:

### Lifecycle Events

#### RunStartedEvent
Signals the beginning of agent processing.

```csharp
public record RunStartedEvent : AgUiEventBase
{
    public override string Type => "run-started";
    public string RunId { get; init; } = Guid.NewGuid().ToString();
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object>? Metadata { get; init; }
}
```

#### RunFinishedEvent
Signals completion of agent processing.

```csharp
public record RunFinishedEvent : AgUiEventBase
{
    public override string Type => "run-finished";
    public string RunId { get; init; }
    public DateTime FinishedAt { get; init; } = DateTime.UtcNow;
    public RunStatus Status { get; init; } = RunStatus.Success;
    public string? Error { get; init; }
}

public enum RunStatus
{
    Success,
    Failed,
    Cancelled
}
```

### Text Message Events

#### TextMessageStartEvent
Begins a new text message from the agent.

```csharp
public record TextMessageStartEvent : AgUiEventBase
{
    public override string Type => "text-message-start";
    public string MessageId { get; init; } = Guid.NewGuid().ToString();
    public MessageRole Role { get; init; } = MessageRole.Assistant;
}

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool
}
```

#### TextMessageContentEvent
Streams chunks of text content.

```csharp
public record TextMessageContentEvent : AgUiEventBase
{
    public override string Type => "text-message-content";
    public string MessageId { get; init; }
    public string Content { get; init; }
    public int ChunkIndex { get; init; }
}
```

#### TextMessageEndEvent
Signals completion of a text message.

```csharp
public record TextMessageEndEvent : AgUiEventBase
{
    public override string Type => "text-message-end";
    public string MessageId { get; init; }
    public int TotalChunks { get; init; }
    public int TotalLength { get; init; }
}
```

### Tool Call Events

#### ToolCallStartEvent
Initiates a tool/function call.

```csharp
public record ToolCallStartEvent : AgUiEventBase
{
    public override string Type => "tool-call-start";
    public string ToolCallId { get; init; } = Guid.NewGuid().ToString();
    public string ToolName { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}
```

#### ToolCallArgumentsEvent
Streams tool call arguments (supports incremental JSON).

```csharp
public record ToolCallArgumentsEvent : AgUiEventBase
{
    public override string Type => "tool-call-arguments";
    public string ToolCallId { get; init; }
    public string ArgumentsChunk { get; init; }
    public bool IsComplete { get; init; }
}
```

#### ToolCallResultEvent
Provides tool execution results.

```csharp
public record ToolCallResultEvent : AgUiEventBase
{
    public override string Type => "tool-call-result";
    public string ToolCallId { get; init; }
    public object? Result { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```

#### ToolCallEndEvent
Signals tool call completion.

```csharp
public record ToolCallEndEvent : AgUiEventBase
{
    public override string Type => "tool-call-end";
    public string ToolCallId { get; init; }
    public DateTime EndedAt { get; init; } = DateTime.UtcNow;
    public TimeSpan Duration { get; init; }
}
```

### State Management Events

#### StateSnapshotEvent
Provides complete state representation.

```csharp
public record StateSnapshotEvent : AgUiEventBase
{
    public override string Type => "state-snapshot";
    public Dictionary<string, object> State { get; init; }
    public int Version { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
```

#### StateDeltaEvent
Provides incremental state updates.

```csharp
public record StateDeltaEvent : AgUiEventBase
{
    public override string Type => "state-delta";
    public Dictionary<string, object?> Changes { get; init; }
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
}
```

### Additional Events

#### ReasoningStartEvent
Indicates the agent is thinking/reasoning.

```csharp
public record ReasoningStartEvent : AgUiEventBase
{
    public override string Type => "reasoning-start";
    public string ReasoningId { get; init; } = Guid.NewGuid().ToString();
}
```

#### ReasoningContentEvent
Streams reasoning content.

```csharp
public record ReasoningContentEvent : AgUiEventBase
{
    public override string Type => "reasoning-content";
    public string ReasoningId { get; init; }
    public string Content { get; init; }
}
```

#### ReasoningEndEvent
Completes reasoning sequence.

```csharp
public record ReasoningEndEvent : AgUiEventBase
{
    public override string Type => "reasoning-end";
    public string ReasoningId { get; init; }
}
```

#### ErrorEvent
Reports errors during processing.

```csharp
public record ErrorEvent : AgUiEventBase
{
    public override string Type => "error";
    public string ErrorCode { get; init; }
    public string Message { get; init; }
    public Dictionary<string, object>? Details { get; init; }
    public bool Recoverable { get; init; }
}
```

#### MetadataEvent
Provides additional context or metadata.

```csharp
public record MetadataEvent : AgUiEventBase
{
    public override string Type => "metadata";
    public string MetadataType { get; init; }
    public Dictionary<string, object> Data { get; init; }
}
```

## Data Transfer Objects

### Base Event Structure

All AG-UI events inherit from a common base:

```csharp
public abstract record AgUiEventBase
{
    public abstract string Type { get; }
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? SessionId { get; init; }
    public string? CorrelationId { get; init; }
}
```

### Request/Response Objects

#### RunAgentInput
Primary request object for agent execution.

```csharp
public record RunAgentInput
{
    public string Message { get; init; }
    public List<Message>? History { get; init; }
    public Dictionary<string, object>? Context { get; init; }
    public RunConfiguration? Configuration { get; init; }
    public string? SessionId { get; init; }
}

public record RunConfiguration
{
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? Model { get; init; }
    public List<string>? EnabledTools { get; init; }
    public Dictionary<string, object>? ModelParameters { get; init; }
}
```

#### Message History
Represents conversation history.

```csharp
public record Message
{
    public MessageRole Role { get; init; }
    public string Content { get; init; }
    public DateTime Timestamp { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public record ToolCall
{
    public string Id { get; init; }
    public string Name { get; init; }
    public Dictionary<string, object> Arguments { get; init; }
    public object? Result { get; init; }
    public ToolCallStatus Status { get; init; }
}

public enum ToolCallStatus
{
    Pending,
    Executing,
    Completed,
    Failed
}
```

## Message Conversion Logic

### LmCore to AG-UI Mapping

```csharp
public static class MessageConverter
{
    public static IEnumerable<AgUiEventBase> ConvertToAgUiEvents(IMessage message)
    {
        return message switch
        {
            TextMessageUpdate textUpdate => ConvertTextUpdate(textUpdate),
            ToolCallUpdate toolUpdate => ConvertToolUpdate(toolUpdate),
            ToolCallResultUpdate resultUpdate => ConvertToolResult(resultUpdate),
            ReasoningUpdate reasoningUpdate => ConvertReasoning(reasoningUpdate),
            ErrorMessage errorMsg => ConvertError(errorMsg),
            _ => Enumerable.Empty<AgUiEventBase>()
        };
    }

    private static IEnumerable<AgUiEventBase> ConvertTextUpdate(TextMessageUpdate update)
    {
        if (update.IsStart)
            yield return new TextMessageStartEvent { MessageId = update.Id };

        if (!string.IsNullOrEmpty(update.Content))
            yield return new TextMessageContentEvent
            {
                MessageId = update.Id,
                Content = update.Content,
                ChunkIndex = update.ChunkIndex
            };

        if (update.IsComplete)
            yield return new TextMessageEndEvent
            {
                MessageId = update.Id,
                TotalChunks = update.TotalChunks,
                TotalLength = update.TotalLength
            };
    }

    private static IEnumerable<AgUiEventBase> ConvertToolUpdate(ToolCallUpdate update)
    {
        if (update.IsStart)
            yield return new ToolCallStartEvent
            {
                ToolCallId = update.Id,
                ToolName = update.FunctionName
            };

        if (update.HasArguments)
            yield return new ToolCallArgumentsEvent
            {
                ToolCallId = update.Id,
                ArgumentsChunk = update.ArgumentsJson,
                IsComplete = update.ArgumentsComplete
            };
    }

    // Additional conversion methods...
}
```

## JSON Schemas

### WebSocket Message Format

All WebSocket messages follow this structure:

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["type", "id", "createdAt"],
  "properties": {
    "type": {
      "type": "string",
      "enum": [
        "run-started", "run-finished",
        "text-message-start", "text-message-content", "text-message-end",
        "tool-call-start", "tool-call-arguments", "tool-call-result", "tool-call-end",
        "state-snapshot", "state-delta",
        "reasoning-start", "reasoning-content", "reasoning-end",
        "error", "metadata"
      ]
    },
    "id": {
      "type": "string",
      "format": "uuid"
    },
    "createdAt": {
      "type": "string",
      "format": "date-time"
    },
    "sessionId": {
      "type": "string"
    },
    "correlationId": {
      "type": "string"
    },
    "payload": {
      "type": "object",
      "description": "Event-specific payload"
    }
  }
}
```

### RunAgentInput Schema

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["message"],
  "properties": {
    "message": {
      "type": "string",
      "minLength": 1
    },
    "history": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["role", "content"],
        "properties": {
          "role": {
            "type": "string",
            "enum": ["system", "user", "assistant", "tool"]
          },
          "content": {
            "type": "string"
          },
          "timestamp": {
            "type": "string",
            "format": "date-time"
          },
          "toolCalls": {
            "type": "array",
            "items": {
              "$ref": "#/definitions/toolCall"
            }
          }
        }
      }
    },
    "context": {
      "type": "object",
      "additionalProperties": true
    },
    "configuration": {
      "type": "object",
      "properties": {
        "temperature": {
          "type": "number",
          "minimum": 0,
          "maximum": 2
        },
        "maxTokens": {
          "type": "integer",
          "minimum": 1
        },
        "model": {
          "type": "string"
        },
        "enabledTools": {
          "type": "array",
          "items": {
            "type": "string"
          }
        }
      }
    },
    "sessionId": {
      "type": "string"
    }
  },
  "definitions": {
    "toolCall": {
      "type": "object",
      "required": ["id", "name", "arguments"],
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "arguments": {
          "type": "object"
        },
        "result": {},
        "status": {
          "type": "string",
          "enum": ["pending", "executing", "completed", "failed"]
        }
      }
    }
  }
}
```

## Tool Call Management

### JsonFragmentUpdate Integration

The system handles incremental JSON streaming for tool arguments:

```csharp
public class ToolCallTracker
{
    private readonly Dictionary<string, ToolCallState> _activeCalls = new();
    private readonly ILogger<ToolCallTracker> _logger;

    public void ProcessJsonFragment(string toolCallId, JsonFragmentUpdate fragment)
    {
        if (!_activeCalls.TryGetValue(toolCallId, out var state))
        {
            state = new ToolCallState(toolCallId);
            _activeCalls[toolCallId] = state;
        }

        state.AccumulateFragment(fragment);

        if (fragment.IsComplete)
        {
            var completeArgs = state.GetCompleteArguments();
            // Emit complete arguments event
            EmitArgumentsComplete(toolCallId, completeArgs);
        }
        else
        {
            // Emit partial arguments event
            EmitArgumentsChunk(toolCallId, fragment.Content);
        }
    }

    private class ToolCallState
    {
        public string Id { get; }
        public StringBuilder ArgumentsBuilder { get; } = new();
        public bool IsComplete { get; private set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        public ToolCallState(string id)
        {
            Id = id;
        }

        public void AccumulateFragment(JsonFragmentUpdate fragment)
        {
            ArgumentsBuilder.Append(fragment.Content);
            IsComplete = fragment.IsComplete;
        }

        public string GetCompleteArguments() => ArgumentsBuilder.ToString();
    }
}
```

### Tool Result Correlation

```csharp
public class ToolResultCorrelator
{
    private readonly Dictionary<string, TaskCompletionSource<object?>> _pendingResults = new();

    public async Task<object?> WaitForResult(string toolCallId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<object?>();
        _pendingResults[toolCallId] = tcs;

        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }

    public void SetResult(string toolCallId, object? result)
    {
        if (_pendingResults.TryGetValue(toolCallId, out var tcs))
        {
            tcs.TrySetResult(result);
            _pendingResults.Remove(toolCallId);
        }
    }

    public void SetError(string toolCallId, Exception error)
    {
        if (_pendingResults.TryGetValue(toolCallId, out var tcs))
        {
            tcs.TrySetException(error);
            _pendingResults.Remove(toolCallId);
        }
    }
}
```

## Serialization Configuration

### System.Text.Json Configuration

```csharp
public static class AgUiJsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new AgUiEventConverter(),
            new ToolCallArgumentsConverter()
        },
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
}

public class AgUiEventConverter : JsonConverter<AgUiEventBase>
{
    public override AgUiEventBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeElement))
            throw new JsonException("Missing 'type' property");

        var type = typeElement.GetString();

        return type switch
        {
            "run-started" => JsonSerializer.Deserialize<RunStartedEvent>(root.GetRawText(), options),
            "text-message-start" => JsonSerializer.Deserialize<TextMessageStartEvent>(root.GetRawText(), options),
            "tool-call-start" => JsonSerializer.Deserialize<ToolCallStartEvent>(root.GetRawText(), options),
            // ... other event types
            _ => throw new JsonException($"Unknown event type: {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, AgUiEventBase value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
```

## Performance Considerations

### Memory Optimization

- Use `ArrayPool<byte>` for buffer management
- Implement object pooling for frequently created events
- Stream large content in chunks (default 4KB)
- Lazy-load message history when needed

### Throughput Optimization

```csharp
public class EventBuffer
{
    private readonly Channel<AgUiEventBase> _channel;
    private readonly int _maxBufferSize;

    public EventBuffer(int maxBufferSize = 1000)
    {
        _maxBufferSize = maxBufferSize;

        var options = new BoundedChannelOptions(maxBufferSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<AgUiEventBase>(options);
    }

    public async ValueTask WriteAsync(AgUiEventBase evt, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync(evt, ct);
    }

    public async IAsyncEnumerable<AgUiEventBase> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
        {
            yield return evt;
        }
    }
}
```

## Validation

### Event Validation

```csharp
public interface IEventValidator
{
    ValidationResult Validate(AgUiEventBase evt);
}

public class EventValidator : IEventValidator
{
    private readonly Dictionary<Type, IValidator> _validators = new();

    public EventValidator()
    {
        RegisterValidator<TextMessageContentEvent>(evt =>
        {
            if (string.IsNullOrEmpty(evt.MessageId))
                return ValidationResult.Error("MessageId is required");
            if (evt.ChunkIndex < 0)
                return ValidationResult.Error("ChunkIndex must be non-negative");
            return ValidationResult.Success;
        });

        // Register other validators...
    }

    public ValidationResult Validate(AgUiEventBase evt)
    {
        var type = evt.GetType();
        if (_validators.TryGetValue(type, out var validator))
        {
            return validator.Validate(evt);
        }
        return ValidationResult.Success;
    }

    private void RegisterValidator<T>(Func<T, ValidationResult> validator) where T : AgUiEventBase
    {
        _validators[typeof(T)] = new TypedValidator<T>(validator);
    }
}

public record ValidationResult
{
    public bool IsValid { get; init; }
    public string? Error { get; init; }

    public static ValidationResult Success => new() { IsValid = true };
    public static ValidationResult Error(string message) => new() { IsValid = false, Error = message };
}
```

## References

- [AG-UI Protocol Specification](https://docs.ag-ui.com)
- [CopilotKit Documentation](https://docs.copilotkit.ai)
- [LmCore Message Types](../../LmCore/README.md)