# SSE (Server-Sent Events) Streaming Support in FakeHttpMessageHandler

The `FakeHttpMessageHandler` class now includes comprehensive support for creating SSE streams for testing purposes. This is useful when testing applications that consume Server-Sent Events.

## Available Methods

### 1. CreateSseStreamHandler
Creates a handler that returns a properly formatted SSE stream with full control over event properties.

```csharp
var events = new[]
{
    new SseEvent { Id = "1", Event = "message", Data = "Hello" },
    new SseEvent { Id = "2", Event = "message", Data = "World" },
    new SseEvent { Data = "Simple data without ID or event type" }
};

var handler = FakeHttpMessageHandler.CreateSseStreamHandler(events);
```

### 2. CreateSimpleSseStreamHandler
Creates a handler for simple text message streaming with optional event type.

```csharp
var messages = new[] { "First message", "Second message", "Third message" };
var handler = FakeHttpMessageHandler.CreateSimpleSseStreamHandler(messages, "chat");
```

### 3. CreateJsonSseStreamHandler
Creates a handler that serializes objects to JSON for each SSE event.

```csharp
var objects = new[]
{
    new { type = "start", message = "Beginning" },
    new { type = "data", message = "Processing" },
    new { type = "end", message = "Complete" }
};

var handler = FakeHttpMessageHandler.CreateJsonSseStreamHandler(objects, "update");
```

## SSE Format

The handlers automatically format responses according to the SSE specification:

- **Content-Type**: `text/event-stream`
- **Cache-Control**: `no-cache`
- **Connection**: `keep-alive`

Each event follows the format:
```
id: 1
event: message
data: Hello World

```

## Example Usage in Tests

```csharp
[Fact]
public async Task TestSseConsumer()
{
    // Arrange
    var events = new[]
    {
        new SseEvent { Id = "1", Event = "start", Data = "Starting process" },
        new SseEvent { Id = "2", Event = "progress", Data = "50% complete" },
        new SseEvent { Id = "3", Event = "complete", Data = "Process finished" }
    };

    var handler = FakeHttpMessageHandler.CreateSseStreamHandler(events);
    var httpClient = new HttpClient(handler);

    // Act
    var response = await httpClient.GetAsync("https://api.example.com/stream");

    // Assert
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    
    var content = await response.Content.ReadAsStringAsync();
    Assert.Contains("event: start", content);
    Assert.Contains("data: Starting process", content);
}
```

## SseEvent Record

The `SseEvent` record provides a simple way to define SSE events:

```csharp
public record SseEvent
{
    public string? Id { get; init; }      // Optional event ID
    public string? Event { get; init; }   // Optional event type
    public string? Data { get; init; }    // Event data
}
```

All properties are optional, allowing for flexible event creation based on your testing needs. 