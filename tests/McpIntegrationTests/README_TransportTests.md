# MCP Transport Test Architecture

## Overview

This directory contains a **reusable test architecture** for testing MCP (Model Context Protocol) functionality across different transport layers. The architecture follows the **Template Method pattern** to eliminate code duplication and ensure consistent testing across all transport implementations.

## Architecture

### Core Components

1. **`McpTransportTestBase.cs`** - Abstract base class containing all core MCP functionality tests
2. **`StdioMcpTransportTests.cs`** - STDIO transport implementation
3. **Future: `SseMcpTransportTests.cs`** - SSE transport implementation (pending SDK support)

### Design Pattern: Template Method + Factory

```csharp
// Abstract base class defines the test structure
public abstract class McpTransportTestBase : IDisposable
{
    // Template methods - implemented by concrete classes
    protected abstract Task<IMcpClient> CreateClientAsync();
    protected abstract string GetTransportName();
    
    // Concrete test methods - shared across all transports
    [Fact] public async Task ToolDiscovery_ShouldDiscoverAll13MemoryTools() { ... }
    [Fact] public async Task MemoryAdd_WithoutConnectionId_ShouldAutoGenerateConnectionId() { ... }
    [Fact] public async Task SessionWorkflow_InitAddSearchUpdateDelete_ShouldWorkEndToEnd() { ... }
    [Fact] public async Task SessionIsolation_DifferentConnections_ShouldNotInterfere() { ... }
    [Fact] public async Task MemoryStats_WithMultipleMemories_ShouldReturnAccurateStats() { ... }
    [Fact] public async Task ErrorHandling_InvalidToolName_ShouldReturnError() { ... }
    [Fact] public async Task ErrorHandling_MissingRequiredParameters_ShouldReturnError() { ... }
}

// Concrete implementation for STDIO transport
public class StdioMcpTransportTests : McpTransportTestBase
{
    protected override async Task<IMcpClient> CreateClientAsync()
    {
        var transport = new StdioClientTransport(...);
        return await McpClientFactory.CreateAsync(transport);
    }
    protected override string GetTransportName() => "STDIO";
}
```

## Benefits

### ✅ **DRY Principle**
- **Single source of truth** for MCP functionality tests
- **No code duplication** between transport implementations
- **Consistent test coverage** across all transports

### ✅ **Transport Agnostic Testing**
- Same test logic runs on **STDIO**, **SSE**, **WebSocket**, etc.
- Tests verify that **MCP client interface works identically** regardless of transport
- **Transport-specific setup** is isolated in concrete classes

### ✅ **Easy Extensibility**
- Adding a new transport requires only:
  1. Create new class inheriting from `McpTransportTestBase`
  2. Implement `CreateClientAsync()` and `GetTransportName()`
  3. All 7 core tests run automatically

### ✅ **Maintainability**
- **One place to update** test logic for all transports
- **Clear separation** between transport setup and test logic
- **Consistent logging** and error handling across transports

## Current Test Coverage

All transport implementations test these **7 core MCP scenarios**:

1. **Tool Discovery** - Verify all 13 memory tools are discoverable
2. **Memory Add (Auto Connection)** - Test memory creation with auto-generated connection ID
3. **Session Workflow** - End-to-end test: init → add → search → update → delete
4. **Session Isolation** - Verify different connections don't interfere with each other
5. **Memory Statistics** - Test stats collection and reporting
6. **Error Handling (Invalid Tool)** - Test error handling for non-existent tools
7. **Error Handling (Missing Params)** - Test error handling for missing required parameters

## Transport Status

| Transport | Status | Implementation | Tests |
|-----------|--------|----------------|-------|
| **STDIO** | ✅ **Ready** | `StdioMcpTransportTests.cs` | 7/7 passing |
| **SSE** | ⏳ **Pending** | Waiting for SDK support | N/A |
| **WebSocket** | 📋 **Future** | Not yet planned | N/A |

### SSE Transport Notes

SSE (Server-Sent Events) transport is **not yet implemented** because:
- The MCP SDK doesn't have full SSE client support yet
- Current SSE tests only verify server-side infrastructure
- When SSE client support is available, create `SseMcpTransportTests.cs` inheriting from `McpTransportTestBase`

## Usage

### Running Transport Tests

```bash
# Run all STDIO transport tests
dotnet test --filter "StdioMcpTransportTests"

# Run specific test across all transports
dotnet test --filter "ToolDiscovery_ShouldDiscoverAll13MemoryTools"

# Run all transport tests (when multiple transports exist)
dotnet test --filter "TransportTests"
```

### Adding a New Transport

1. **Create the transport test class:**
```csharp
public class WebSocketMcpTransportTests : McpTransportTestBase
{
    public WebSocketMcpTransportTests(ITestOutputHelper output) : base(output) { }
    
    protected override string GetTransportName() => "WebSocket";
    
    protected override async Task<IMcpClient> CreateClientAsync()
    {
        var transport = new WebSocketClientTransport(...);
        return await McpClientFactory.CreateAsync(transport);
    }
}
```

2. **All 7 tests run automatically** - no additional code needed!

## Comparison with Legacy Tests

| Aspect | Legacy Approach | New Transport Architecture |
|--------|----------------|---------------------------|
| **Code Duplication** | ❌ Duplicated test logic | ✅ Single shared test suite |
| **Consistency** | ❌ Tests might differ | ✅ Identical tests across transports |
| **Maintenance** | ❌ Update multiple files | ✅ Update one base class |
| **Transport Coverage** | ❌ Manual per transport | ✅ Automatic for all transports |
| **Extensibility** | ❌ Copy/paste approach | ✅ Inherit and implement 2 methods |

## Future Enhancements

1. **SSE Transport** - When SDK supports SSE clients
2. **WebSocket Transport** - For real-time bidirectional communication
3. **gRPC Transport** - For high-performance scenarios
4. **Performance Tests** - Transport-specific performance characteristics
5. **Load Tests** - Concurrent connection testing per transport

---

This architecture ensures that **MCP functionality works identically across all transports** while making it **trivial to add new transport implementations**. 