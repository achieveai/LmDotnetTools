# LmMultiTurn

Multi-turn agent infrastructure for building conversational AI agents with support for **fully duplex non-blocking communication**, streaming, persistence, and graceful lifecycle management.

## Overview

This project provides:

- **MultiTurnAgentBase**: Abstract base class with duplex channel management, subscription handling, and lifecycle management
- **MultiTurnAgentLoop**: Concrete implementation using raw LLM APIs with middleware pipeline (poll-based input consumption)
- **ClaudeAgentLoop**: Concrete implementation using Claude Agent SDK CLI with MCP tools (push-based for Interactive mode)
- **Persistence**: SQLite-based conversation state persistence

## Key Features

### Fully Duplex Non-Blocking Communication

The multi-turn agent system implements a **fire-and-forget input pattern** with decoupled output streaming:

```
                    ┌─────────────────┐
   SendAsync() ──▶  │   Input Queue   │  (fire-and-forget, returns SendReceipt immediately)
                    │   (Channel)     │
                    └────────┬────────┘
                             │
                             ▼
                    ┌─────────────────┐
                    │  Implementation │  (owns RunLoopAsync, decides when to start runs)
                    │  Run Loop       │
                    └────────┬────────┘
                             │
                             ▼
                    ┌─────────────────┐
   SubscribeAsync()◀│ Output Channel  │  (streams RunAssignmentMessage, responses, RunCompletedMessage)
                    │   (per-sub)     │
                    └─────────────────┘
```

#### Key Types

- **`SendReceipt`**: Returned immediately by `SendAsync`. Contains `ReceiptId`, `InputId`, and `QueuedAt`.
- **`RunAssignment`**: Published via `RunAssignmentMessage` when the implementation starts processing. Contains `RunId`, `GenerationId`, `InputIds` (list of receipts included in this run), `ParentRunId`, and `WasInjected`.
- **`QueuedInput`**: Internal representation linking `UserInput` with `ReceiptId`.

#### Correlation Pattern

```csharp
// Caller sends and gets immediate receipt
var receipt = await agent.SendAsync(messages, inputId: "my-input");
Console.WriteLine($"Queued: {receipt.ReceiptId}");

// Later, subscriber receives RunAssignmentMessage when run starts
await foreach (var msg in agent.SubscribeAsync(ct))
{
    if (msg is RunAssignmentMessage assignment)
    {
        // Match receipt to run via InputIds
        if (assignment.Assignment.InputIds?.Contains(receipt.ReceiptId) == true)
        {
            Console.WriteLine($"Run {assignment.RunId} started for our input!");
        }
    }
}
```

### Implementation Patterns

The base class provides two consumption patterns for implementations:

#### Poll-Based (MultiTurnAgentLoop)
Implementations call `TryDrainInputs()` between LLM turns to check for new input:

```csharp
protected override async Task RunLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // Wait for input
        await InputReader.WaitToReadAsync(ct);
        TryDrainInputs(out var batch);

        var assignment = StartRun(batch);
        await PublishToAllAsync(new RunAssignmentMessage { Assignment = assignment, ThreadId = ThreadId }, ct);

        try
        {
            // Execute turns, poll between each turn
            while (hasPendingToolCalls)
            {
                // POLL: Check for new inputs that can be injected
                if (TryDrainInputs(out var newInputs) && newInputs.Count > 0)
                {
                    // Inject into current run
                }
                await ExecuteTurnAsync(...);
            }
        }
        finally
        {
            await CompleteRunAsync(assignment.RunId, assignment.GenerationId, false, null, ct);
        }
    }
}
```

#### Push-Based (ClaudeAgentLoop Interactive Mode)
Implementations watch `InputReader` concurrently while the agent runs:

```csharp
protected override async Task RunLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await InputReader.WaitToReadAsync(ct);
        TryDrainInputs(out var batch);

        var assignment = StartRun(batch);
        await PublishToAllAsync(new RunAssignmentMessage { ... }, ct);

        // Watch for new inputs concurrently while agent processes
        var watchTask = WatchAndInjectInputsAsync(ct);
        var agentTask = ExecuteAgentAsync(ct);

        await Task.WhenAll(watchTask, agentTask);
        await CompleteRunAsync(...);
    }
}
```

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

### Basic Usage with Fire-and-Forget Pattern

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

// Start the agent background loop
_ = agent.RunAsync();

// Subscribe to output (should happen before sending for real-time updates)
_ = Task.Run(async () =>
{
    await foreach (var msg in agent.SubscribeAsync())
    {
        if (msg is RunAssignmentMessage ram)
            Console.WriteLine($"[Run Started] {ram.RunId}");
        else if (msg is TextMessage tm)
            Console.WriteLine($"[Response] {tm.Text}");
        else if (msg is RunCompletedMessage rcm)
            Console.WriteLine($"[Run Completed] {rcm.CompletedRunId}");
    }
});

// Send message (fire-and-forget, returns immediately)
var receipt = await agent.SendAsync(
    [new TextMessage { Text = "Hello!", Role = Role.User }],
    inputId: "greeting"
);
Console.WriteLine($"Queued with receipt: {receipt.ReceiptId}");
```

### Simpler Usage with ExecuteRunAsync

For simpler cases where you don't need the full duplex pattern:

```csharp
// ExecuteRunAsync provides a simpler API that sends and waits for the run to complete
await foreach (var msg in agent.ExecuteRunAsync(new UserInput(
    [new TextMessage { Text = "Hello!", Role = Role.User }]
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
