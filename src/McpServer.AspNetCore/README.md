# AchieveAi.LmDotnetTools.McpServer.AspNetCore

An AspNetCore library for hosting Model Context Protocol (MCP) servers that expose `IFunctionProvider` implementations as MCP tools via HTTP endpoints.

## Features

- **IHostedService Integration**: Proper lifecycle management with ASP.NET Core host
- **Singleton Registration**: Server instance can be injected across all scopes
- **Dynamic Port Allocation**: Automatically assigns free ports (use `Port = 0`)
- **Stateful/Stateless Function Filtering**: Control which functions are exposed via MCP
- **IFunctionProvider Integration**: Seamlessly exposes existing LmCore function providers as MCP tools
- **Automatic Schema Generation**: Converts `FunctionContract` schemas to MCP tool schemas
- **HTTP Transport**: Uses official ModelContextProtocol.AspNetCore library
- **Clean Lifecycle Management**: Start, stop, and dispose with proper cleanup

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
            ProviderName = ProviderName,
            IsStateful = false  // Mark as stateless for MCP server reuse
        };
    }

    private async Task<string> GetWeatherAsync(string argumentsJson)
    {
        // Implementation
    }
}
```

### 2. Register and Start the Server with DI

```csharp
using AchieveAi.LmDotnetTools.McpServer.AspNetCore;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using Microsoft.Extensions.DependencyInjection;

// Create services
var services = new ServiceCollection();

// Register function providers
services.AddFunctionProvider(new WeatherTool());
services.AddFunctionProvider(new CalculatorTool());

// Add MCP server with options
services.AddMcpFunctionProviderServer(options =>
{
    options.Port = 0;  // Dynamic port allocation
    options.IncludeStatefulFunctions = false;  // Only expose stateless functions
});

// Build service provider and get server
var serviceProvider = services.BuildServiceProvider();
var server = serviceProvider.GetRequiredService<McpFunctionProviderServer>();

// Start the server
await server.StartAsync();

Console.WriteLine($"MCP Server running on: {server.McpEndpointUrl}");
// Output: MCP Server running on: http://localhost:54321/mcp

// Server is automatically managed via IHostedService lifecycle
```

### 3. Integration with ASP.NET Core Host

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Register your function providers
builder.Services.AddFunctionProvider<WeatherTool>();
builder.Services.AddFunctionProvider<CalculatorTool>();

// Add MCP server (automatically registered as IHostedService)
builder.Services.AddMcpFunctionProviderServer(options =>
{
    options.Port = 5123;  // Fixed port
    options.IncludeStatefulFunctions = true;
});

var host = builder.Build();
await host.RunAsync();
```

### 4. Inject Server Instance in Other Services

```csharp
public class MyService
{
    private readonly McpFunctionProviderServer _mcpServer;

    public MyService(McpFunctionProviderServer mcpServer)
    {
        _mcpServer = mcpServer;
    }

    public string GetMcpEndpoint() => _mcpServer.McpEndpointUrl ?? "Server not started";
}
```

### 5. Configure for Claude Code SDK

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

## Configuration Options

```csharp
services.AddMcpFunctionProviderServer(options =>
{
    // Port to listen on. Use 0 for dynamic allocation (default)
    options.Port = 0;

    // Whether to include stateful functions. Default is true.
    // Set to false to only expose stateless functions (safer for shared MCP servers)
    options.IncludeStatefulFunctions = true;

    // MCP endpoint path (default: "/mcp")
    options.EndpointPath = "/mcp";
});
```

## Stateful vs Stateless Functions

Mark functions as stateful or stateless using the `IsStateful` property:

```csharp
yield return new FunctionDescriptor
{
    Contract = contract,
    Handler = MyHandler,
    ProviderName = ProviderName,
    IsStateful = true  // This function maintains state per LLM call
};
```

- **Stateless functions** (`IsStateful = false`): Safe to reuse across multiple LLM invocations. Examples: weather lookup, calculations, static data retrieval.
- **Stateful functions** (`IsStateful = true`): Require per-call instance management. Examples: conversation context, file editing sessions.

Use `IncludeStatefulFunctions = false` to only expose stateless functions via MCP, allowing safe server reuse across agent runs.

## Architecture

```
┌─────────────────────────────────────────┐
│    IFunctionProvider Implementations    │
│    (WeatherTool, CalculatorTool, etc.)  │
└──────────────┬──────────────────────────┘
               │ Registered via DI
               ▼
┌─────────────────────────────────────────┐
│  StatelessFunctionProviderWrapper       │
│  - Filters out stateful functions       │
│  - (when IncludeStatefulFunctions=false)│
└──────────────┬──────────────────────────┘
               │
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
│  McpFunctionProviderServer              │
│  - Implements IHostedService            │
│  - Singleton across all scopes          │
│  - Dynamic port allocation              │
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

The server automatically finds and assigns a free port when `Port = 0`:

```csharp
services.AddMcpFunctionProviderServer(options => options.Port = 0);
var server = serviceProvider.GetRequiredService<McpFunctionProviderServer>();
await server.StartAsync();

// Port is automatically assigned
Console.WriteLine($"Server started on port: {server.Port}");
Console.WriteLine($"MCP endpoint: {server.McpEndpointUrl}");
```

## Disposal

The server implements `IAsyncDisposable` for proper cleanup. When used with `IHostedService`, disposal is managed automatically by the host.

For standalone usage:

```csharp
var services = new ServiceCollection();
services.AddFunctionProvider(new WeatherTool());
services.AddMcpFunctionProviderServer();

await using var serviceProvider = services.BuildServiceProvider();
var server = serviceProvider.GetRequiredService<McpFunctionProviderServer>();
await server.StartAsync();

// Server automatically disposed when serviceProvider is disposed
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
- Stateful/stateless function filtering

## License

Same as the parent LmDotnetTools project.
