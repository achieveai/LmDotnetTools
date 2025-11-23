# AchieveAi.LmDotnetTools.McpServer.AspNetCore

An AspNetCore library for hosting Model Context Protocol (MCP) servers that expose `IFunctionProvider` implementations as MCP tools via HTTP endpoints.

## Features

- ✅ **Disposable Server Wrapper**: `McpFunctionProviderServer` class with proper async disposal
- ✅ **Dynamic Port Allocation**: Automatically assigns free ports (no hardcoded ports)
- ✅ **IFunctionProvider Integration**: Seamlessly exposes existing LmCore function providers as MCP tools
- ✅ **Automatic Schema Generation**: Converts `FunctionContract` schemas to MCP tool schemas
- ✅ **HTTP Transport**: Uses official ModelContextProtocol.AspNetCore library
- ✅ **Clean Lifecycle Management**: Start, stop, and dispose with proper cleanup

## Quick Start

### 1. Create Function Providers

```csharp
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;

public class WeatherTool : IFunctionProvider
{
    public string ProviderName => "WeatherProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "get_weather",
            Description = "Get the current weather for a city",
            Parameters = new[]
            {
                new FunctionParameterContract
                {
                    Name = "city",
                    Description = "The city name",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true
                }
            }
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = GetWeatherAsync,
            ProviderName = ProviderName
        };
    }

    private async Task<string> GetWeatherAsync(string argumentsJson)
    {
        // Implementation
    }
}
```

### 2. Create and Start the Server

```csharp
using AchieveAi.LmDotnetTools.McpServer.AspNetCore;

// Create server with function providers
var server = McpFunctionProviderServer.Create(
    new[] {
        new WeatherTool(),
        new CalculatorTool(),
        new FileInfoTool()
    });

// Start the server (dynamic port allocation)
await server.StartAsync();

Console.WriteLine($"MCP Server running on: {server.McpEndpointUrl}");
// Output: MCP Server running on: http://localhost:54321/mcp

// Use the server...

// Clean disposal
await server.DisposeAsync();
```

### 3. Configure for Claude Code SDK

Add to your MCP configuration:

```json
{
  "mcpServers": {
    "my-function-server": {
      "command": "npx",
      "args": ["mcp-remote@latest", "http://localhost:YOUR_PORT/mcp"]
    }
  }
}
```

## Architecture

```
┌─────────────────────────────────────────┐
│    IFunctionProvider Implementations    │
│    (WeatherTool, CalculatorTool, etc.)  │
└──────────────┬──────────────────────────┘
               │ Registered via DI
               ▼
┌─────────────────────────────────────────┐
│  FunctionProviderMcpAdapter             │
│  - Collects all IFunctionProviders      │
│  - Generates MCP tool definitions       │
│  - Routes tool calls to handlers        │
└──────────────┬──────────────────────────┘
               │
               ▼
┌─────────────────────────────────────────┐
│  ModelContextProtocol.AspNetCore        │
│  - HTTP transport                       │
│  - /mcp endpoint                        │
│  - JSON-RPC handling                    │
└─────────────────────────────────────────┘
```

## Dynamic Port Allocation

The server automatically finds and assigns a free port:

```csharp
var server = McpFunctionProviderServer.Create(functionProviders);
await server.StartAsync();

// Port is automatically assigned
Console.WriteLine($"Server started on port: {server.Port}");
Console.WriteLine($"MCP endpoint: {server.McpEndpointUrl}");
```

## Disposal

The server implements `IAsyncDisposable` for proper cleanup:

```csharp
await using var server = McpFunctionProviderServer.Create(functionProviders);
await server.StartAsync();

// Server automatically disposed when out of scope
```

Or manually:

```csharp
var server = McpFunctionProviderServer.Create(functionProviders);
try
{
    await server.StartAsync();
    // Use server...
}
finally
{
    await server.DisposeAsync();
}
```

## Dependencies

- **LmCore**: Core function provider interfaces
- **ModelContextProtocol v0.3.0-preview.3**: Official MCP SDK
- **ModelContextProtocol.AspNetCore v0.3.0-preview.3**: HTTP transport
- **ASP.NET Core 8.0 or 9.0**

## Testing

See `tests/McpServer.AspNetCore.Tests` for comprehensive integration tests demonstrating:
- Dynamic port allocation
- Tool discovery
- Tool execution
- Proper disposal
- Multiple server instances

## Implementation Status

✅ **Completed**:
- Core library implementation
- FunctionProviderMcpAdapter (converts IFunctionProvider → MCP tools)
- McpFunctionProviderServer (disposable wrapper with dynamic ports)
- Extension methods for easy setup
- Sample application
- Integration test structure

⏳ **In Progress**:
- Test compilation fixes (MCP client API compatibility)

## License

Same as the parent LmDotnetTools project.
