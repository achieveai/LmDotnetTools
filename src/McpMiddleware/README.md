# McpMiddleware - MCP Client Integration

This package provides integration between Model Context Protocol (MCP) clients and the LmDotnetTools function system.

## Components

### McpMiddleware

The main middleware that wraps `FunctionCallMiddleware` and provides MCP client functionality.

### McpClientFunctionProvider

A new `IFunctionProvider` implementation that allows adding MCP client functions to `FunctionRegistry`.

### FunctionRegistry Extensions

Extension methods that make it easy to add MCP clients to `FunctionRegistry`.

## Usage

### Basic Usage with FunctionRegistry

```csharp
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;

// Create your MCP clients
var mcpClients = new Dictionary<string, IMcpClient>
{
    ["client1"] = await McpClientFactory.CreateAsync(transport1),
    ["client2"] = await McpClientFactory.CreateAsync(transport2)
};

// Create function registry and add MCP clients
var registry = new FunctionRegistry();
await registry.AddMcpClientsAsync(mcpClients);

// Build middleware with conflict resolution
var middleware = registry
    .WithConflictResolution(ConflictResolution.PreferMcp)
    .BuildMiddleware();
```

### Adding Individual MCP Clients

```csharp
var registry = new FunctionRegistry();

// Add individual clients with custom provider names
await registry.AddMcpClientAsync(client1, "FileSystem", "FileSystemProvider");
await registry.AddMcpClientAsync(client2, "Database", "DatabaseProvider");

var middleware = registry.BuildMiddleware();
```

### Mixing MCP Clients with Other Function Providers

```csharp
var registry = new FunctionRegistry();

// Add MCP clients
await registry.AddMcpClientsAsync(mcpClients);

// Add other function providers
registry.AddProvider(new TypeFunctionProvider(typeof(MyStaticClass)));

// Custom conflict resolution
var middleware = registry
    .WithConflictHandler((functionName, candidates) => 
    {
        // Prefer MCP functions over others
        return candidates.FirstOrDefault(c => c.ProviderName.StartsWith("Mcp")) 
               ?? candidates.First();
    })
    .BuildMiddleware();
```

### Using McpClientFunctionProvider Directly

```csharp
// Create provider directly
var provider = await McpClientFunctionProvider.CreateAsync(mcpClients, "MyProvider");

// Add to registry
var registry = new FunctionRegistry();
registry.AddProvider(provider);

var middleware = registry.BuildMiddleware();
```

## Function Naming

Functions from MCP clients follow the naming pattern: `{clientId}-{toolName}`

For example:
- Client "filesystem" with tool "read_file" becomes function "filesystem-read_file"
- Client "database" with tool "query" becomes function "database-query"

## Conflict Resolution

When using multiple function providers, you can resolve conflicts using:

- `ConflictResolution.PreferMcp` - Prefer MCP functions over others
- `ConflictResolution.TakeFirst` - Use the first function found
- `ConflictResolution.TakeLast` - Use the last function found
- Custom handler for complex logic

## Error Handling

- If MCP client discovery fails, errors are logged but other clients continue
- Function execution errors are returned as JSON error responses
- Invalid JSON arguments are handled gracefully

## Logging

The providers support structured logging through `ILogger<McpClientFunctionProvider>`:

```csharp
await registry.AddMcpClientsAsync(
    mcpClients, 
    "MyProvider", 
    loggerFactory.CreateLogger<McpClientFunctionProvider>()
);
```

## Comparison with McpMiddleware

| Feature | McpMiddleware | FunctionRegistry + McpClientFunctionProvider |
|---------|---------------|----------------------------------------------|
| **Use Case** | Standalone MCP integration | Mix MCP with other function sources |
| **Conflict Resolution** | No conflicts (single source) | Full conflict resolution support |
| **Function Sources** | MCP clients only | MCP + TypeFunctionProvider + others |
| **Complexity** | Simple, direct | More flexible, composable |
| **Performance** | Slightly faster (direct) | Minimal overhead |

Use `McpMiddleware` when you only need MCP client functions. Use `FunctionRegistry` with the extensions when you need to combine MCP clients with other function sources or need advanced conflict resolution.