# LmMultiTurn

Multi-turn agent infrastructure for building conversational AI agents with support for streaming, persistence, and graceful lifecycle management.

## Overview

This project provides:

- **MultiTurnAgentBase**: Abstract base class with channel management, subscription handling, and lifecycle management
- **ClaudeAgentLoop**: Concrete implementation using Claude Agent SDK CLI with MCP tools
- **Persistence**: SQLite-based conversation state persistence

## Key Features

### Lifecycle Management

The multi-turn agents support a complete lifecycle with graceful shutdown and restart capabilities:

```
                 ┌──────────────────┐
                 │   Not Started    │
                 │ _runTask = null  │
                 └────────┬─────────┘
                          │ RunAsync()
                          ▼
                 ┌──────────────────┐
                 │    Running       │◄──────────────┐
                 │ _runTask active  │               │
                 └────────┬─────────┘               │
                          │                         │
          ┌───────────────┼───────────────┐         │
          │               │               │         │
     StopAsync()    StopProcessAsync()  DisposeAsync()
     (stop loop)    (stop loop +        (final cleanup)
                     process)                       │
          │               │               │         │
          ▼               ▼               ▼         │
 ┌────────────────┐ ┌──────────┐  ┌────────────┐    │
 │  Stopped       │ │ Stopped  │  │ Disposed   │    │
 │ (can restart)  │ │ (restart │  │ (terminal) │    │
 └────────┬───────┘ │ possible)│  └────────────┘    │
          │         └────┬─────┘                    │
          │              │                          │
          └──────────────┴──────────────────────────┘
                   SendAsync() triggers restart
```

### Graceful Shutdown

The `ClaudeAgentLoop` implements a layered shutdown sequence:

1. **Send `/exit` command** - Graceful signal to the CLI
2. **Close stdin** - Signal EOF to the process
3. **Wait for exit** - Allow process to terminate cleanly (10s default)
4. **Force kill** - Terminate process tree if still running
5. **Cleanup** - Wait for background tasks and clean up resources

### Auto-Restart

After calling `StopProcessAsync()`, the agent can automatically restart when `SendAsync()` is called:

```csharp
var agent = new ClaudeAgentLoop(options, mcpServers, "thread-1");

// Start and use the agent
await agent.RunAsync();
await agent.SendAsync(new List<IMessage> { new TextMessage { Text = "Hello" } });

// Stop the process (but don't dispose)
await agent.StopProcessAsync();

// Later, send another message - agent auto-restarts
await agent.SendAsync(new List<IMessage> { new TextMessage { Text = "Hello again" } });
// ^ This automatically restarts the process and run loop
```

## Usage

### Basic Usage

```csharp
var options = new ClaudeAgentSdkOptions
{
    Mode = ClaudeAgentSdkMode.Interactive,
    ProjectRoot = "/path/to/project"
};

var mcpServers = new Dictionary<string, McpServerConfig>
{
    ["filesystem"] = new McpServerConfig
    {
        Type = "stdio",
        Command = "npx",
        Args = ["-y", "@anthropic-ai/mcp-server-filesystem", "/path/to/allowed"]
    }
};

await using var agent = new ClaudeAgentLoop(
    claudeOptions: options,
    mcpServers: mcpServers,
    threadId: "my-thread",
    systemPrompt: "You are a helpful assistant."
);

// Start the agent
_ = agent.RunAsync();

// Send messages and receive responses
await foreach (var msg in agent.ExecuteRunAsync(new UserInput(
    new List<IMessage> { new TextMessage { Text = "Hello!", Role = Role.User } }
)))
{
    Console.WriteLine(msg);
}
```

### With Persistence

```csharp
var store = new SqliteConversationStore("conversations.db");

await using var agent = new ClaudeAgentLoop(
    claudeOptions: options,
    mcpServers: mcpServers,
    threadId: "persistent-thread",
    store: store
);

// Recover previous conversation state
await agent.RecoverAsync();

// Continue conversation
_ = agent.RunAsync();
```

### Stopping and Restarting

```csharp
// Stop just the run loop (process keeps running)
await agent.StopAsync();

// Or stop both run loop AND process
await agent.StopProcessAsync();

// Agent auto-restarts on next SendAsync()
await agent.SendAsync(messages);
```

## Thread Safety

- `SendAsync()` is thread-safe for concurrent calls
- Multiple subscribers can call `SubscribeAsync()` concurrently
- Internal operations use `SemaphoreSlim` for serialization
- State transitions use `Interlocked.CompareExchange` for atomicity

## Race Condition Mitigations

| Race Condition | Mitigation |
|----------------|------------|
| Concurrent SendMessages + Shutdown | `SemaphoreSlim` serializes operations |
| State transitions during async ops | Interlocked state machine |
| Channel write after close | Recreatable channel pattern |
| Multiple shutdown calls | State check prevents re-entry |
| Background tasks not stopping | Polling-based approach with cancellation |

## Architecture

### MultiTurnAgentBase

Base class providing:
- Input channel management (recreatable for restart support)
- Output subscriber management
- Conversation history with thread-safe access
- Run lifecycle (start, stop, restart)
- Persistence hooks

### ClaudeAgentLoop

Specialized implementation that:
- Wraps Claude Agent SDK CLI process
- Manages MCP server configuration
- Provides `StopProcessAsync()` for full shutdown
- Auto-restarts on `SendAsync()` when stopped

### IClaudeAgentSdkClient

Interface for the underlying CLI client:
- `StartAsync()` - Start the Node.js process
- `SendMessagesAsync()` - Send messages and stream responses
- `SendExitCommandAsync()` - Send `/exit` for graceful shutdown
- `ShutdownAsync()` - Layered shutdown with timeout
- `LastRequest` - Stored for restart capability

## Configuration

### ClaudeAgentSdkOptions

```csharp
var options = new ClaudeAgentSdkOptions
{
    Mode = ClaudeAgentSdkMode.Interactive, // or OneShot
    ProjectRoot = "/path/to/project",
    NodeJsPath = null,  // Auto-detected
    CliPath = null,     // Auto-detected
};
```

### MCP Server Configuration

```csharp
var mcpServers = new Dictionary<string, McpServerConfig>
{
    ["server-name"] = new McpServerConfig
    {
        Type = "stdio",      // or "sse"
        Command = "npx",
        Args = ["-y", "@package/name"],
        Env = new Dictionary<string, string> { ["KEY"] = "value" }
    }
};
```

## Testing

The project includes comprehensive tests in `tests/LmMultiTurn.Tests/`:

```bash
dotnet test tests/LmMultiTurn.Tests/
```

For testing, use the `clientFactory` parameter to inject a mock:

```csharp
var mockClient = new MockClaudeAgentSdkClient(messagesToReplay);

var agent = new ClaudeAgentLoop(
    options,
    mcpServers,
    "test-thread",
    clientFactory: (opts, logger) => mockClient
);
```
