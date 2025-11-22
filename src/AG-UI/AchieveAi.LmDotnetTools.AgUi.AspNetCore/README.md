# AG-UI AspNetCore Integration

This library provides ASP.NET Core integration for the AG-UI protocol, enabling real-time agent-UI communication through both **Server-Sent Events (SSE)** and **WebSocket** transports.

## Features

- **Dual Transport Support**: Both SSE and WebSocket endpoints
- **Standard AG-UI Protocol**: Compatible with `@ag-ui/client` and CopilotKit
- **Channel-Based Architecture**: Clean separation between event publishing and transport
- **Session Management**: Thread and run tracking with persistence support

## Quick Start

### 1. Install the Package

```bash
dotnet add package AchieveAi.LmDotnetTools.AgUi.AspNetCore
```

### 2. Configure Services

```csharp
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add AG-UI services with optional persistence
builder.Services.AddAgUi(options =>
{
    options.WebSocketPath = "/ag-ui/ws";
    options.EnablePersistence = true;
    options.DatabasePath = "agui.db";
    options.EnableDebugLogging = true;
});

// Register your agents as IStreamingAgent
builder.Services.AddSingleton<IStreamingAgent, MyAgent>();

// Add controllers (required for SSE endpoint)
builder.Services.AddControllers()
    .AddApplicationPart(typeof(AgUiController).Assembly);
```

### 3. Configure Middleware

```csharp
var app = builder.Build();

app.UseCors(); // CORS must be before AG-UI
app.UseAgUi(); // Enables WebSocket endpoint
app.MapControllers(); // Enables SSE endpoint

app.Run();
```

## Endpoints

### SSE Endpoint (Recommended)

**POST** `/api/ag-ui`

The SSE endpoint is the recommended way to consume AG-UI from web clients, as it works seamlessly with `@ag-ui/client` and CopilotKit.

**Request:**
```json
{
  "threadId": "optional-thread-id",
  "runId": "optional-run-id",
  "messages": [
    {
      "role": "user",
      "content": "Hello, what can you help me with?",
      "name": "optional-sender-name"
    }
  ],
  "agent": "optional-agent-name"
}
```

**Response:** Server-Sent Events stream

```
data: {"type":"RUN_STARTED","runId":"...","startedAt":"...","sessionId":"..."}

data: {"type":"TEXT_MESSAGE_START","messageId":"...","role":"Assistant","sessionId":"..."}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"...","content":"Hello! I can help...","chunkIndex":0,"sessionId":"..."}

data: {"type":"TEXT_MESSAGE_END","messageId":"...","totalChunks":1,"sessionId":"..."}

data: {"type":"RUN_FINISHED","runId":"...","finishedAt":"...","status":"Success","sessionId":"..."}
```

**Testing with curl:**
```bash
curl -X POST http://localhost:5264/api/ag-ui \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello"}]}' \
  -N --no-buffer
```

### WebSocket Endpoint

**WS** `/ag-ui/ws?sessionId={sessionId}`

The WebSocket endpoint provides bidirectional communication for scenarios requiring real-time interaction.

## Integration with CopilotKit

You can now use the SSE endpoint directly with CopilotKit, eliminating the need for a Node.js bridge:

```tsx
import { CopilotKit } from "@copilotkit/react-core";
import { HttpAgent } from "@ag-ui/client";

const agent = new HttpAgent({
  url: "http://localhost:5264/api/ag-ui"
});

function App() {
  return (
    <CopilotKit agent={agent}>
      {/* Your app */}
    </CopilotKit>
  );
}
```

## Architecture

### Channel-Based Event Flow

```
Agent → AgUiStreamingMiddleware → IEventPublisher (Channel)
                                         ↓
                    ┌────────────────────┴────────────────────┐
                    ↓                                         ↓
            AgUiController (SSE)                    WebSocket Handler
                    ↓                                         ↓
              SSE Clients                              WebSocket Clients
```

### Key Components

- **AgUiStreamingMiddleware**: Wraps agents and publishes events to the channel
- **IEventPublisher/ChannelEventPublisher**: Central event distribution using `System.Threading.Channels`
- **AgUiController**: HTTP controller that subscribes to channel and streams SSE
- **WebSocket Handler**: Alternative transport for bidirectional communication

### Design Principles

1. **Separation of Concerns**: Middleware publishes events; transports consume them
2. **Multiple Transports**: Same event stream served via SSE and WebSocket
3. **Stateless Controllers**: No session state in HTTP controllers
4. **Agent Agnostic**: Works with any `IStreamingAgent` implementation

## Agent Selection

The SSE endpoint supports agent selection via the `agent` field in the request:

```json
{
  "agent": "ToolCallingAgent",
  "messages": [...]
}
```

Agents must be registered as `IStreamingAgent`:

```csharp
builder.Services.AddSingleton<ToolCallingAgent>();
builder.Services.AddSingleton<IStreamingAgent>(sp =>
    sp.GetRequiredService<ToolCallingAgent>());
```

If no agent is specified, the first registered `IStreamingAgent` is used.

## Event Types

The AG-UI protocol supports the following event types:

- `SESSION_STARTED` - New session created
- `RUN_STARTED` - Agent execution begins
- `TEXT_MESSAGE_START` - Text message starts
- `TEXT_MESSAGE_CONTENT` - Text content chunk
- `TEXT_MESSAGE_END` - Text message completes
- `REASONING_START` - Reasoning block starts
- `REASONING_MESSAGE_START` - Reasoning message starts
- `REASONING_MESSAGE_CONTENT` - Reasoning content chunk
- `REASONING_MESSAGE_END` - Reasoning message completes
- `REASONING_END` - Reasoning block completes
- `TOOL_CALL_START` - Tool invocation starts
- `TOOL_CALL_ARGS` - Tool arguments streaming
- `TOOL_CALL_RESULT` - Tool execution result
- `RUN_FINISHED` - Agent execution completes
- `ERROR_EVENT` - Error occurred

## Configuration Options

```csharp
builder.Services.AddAgUi(options =>
{
    // WebSocket endpoint path
    options.WebSocketPath = "/ag-ui/ws";

    // Enable session persistence
    options.EnablePersistence = true;
    options.DatabasePath = "agui.db";
    options.MaxSessionAgeHours = 24;

    // Debug logging
    options.EnableDebugLogging = true;

    // CORS (optional)
    options.EnableCors = true;
    options.AllowedOrigins = new[] { "http://localhost:3000" };

    // Performance tuning
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.MaxMessageSize = 1024 * 1024; // 1MB
    options.EventBufferSize = 1000;
});
```

## Troubleshooting

### No agents registered

**Error:** `No agent available - please register at least one IStreamingAgent in DI`

**Solution:** Register your agents as `IStreamingAgent`:
```csharp
builder.Services.AddSingleton<IStreamingAgent, MyAgent>();
```

### SSE endpoint returns 404

**Solution:** Ensure controllers are mapped and the AspNetCore assembly is added:
```csharp
builder.Services.AddControllers()
    .AddApplicationPart(typeof(AgUiController).Assembly);

app.MapControllers();
```

### Events not streaming

**Solution:** Ensure the agent is wrapped with `AgUiStreamingMiddleware`:
```csharp
var middleware = serviceProvider.GetRequiredService<AgUiStreamingMiddleware>();
var wrappedAgent = agent.WithMiddleware(middleware);
```

## License

See LICENSE file in the repository root.
