# MCP Server AspNetCore Sample

This sample demonstrates how to create an MCP (Model Context Protocol) server using AspNetCore that exposes `IFunctionProvider` implementations as MCP tools.

## Overview

The `AchieveAi.LmDotnetTools.McpServer.AspNetCore` library allows you to:
- Expose existing `IFunctionProvider` implementations as MCP tools
- Host an HTTP MCP server that can be consumed by Claude Code SDK and other MCP clients
- Automatically generate tool schemas from .NET type information
- Configure the server port programmatically

## Running the Sample

### 1. Build and Run

```bash
cd samples/McpServer.AspNetCore.Sample
dotnet run
```

Or with a custom port:

```bash
dotnet run -- --port 8080
```

### 2. Verify Server is Running

Visit the health check endpoint:
```bash
curl http://localhost:5123/health
```

## Available Tools

This sample includes three example function providers:

### WeatherTool
- **Tool**: `get_weather`
- **Description**: Get the current weather for a city
- **Parameters**:
  - `city` (required): The city name
  - `unit` (optional): Temperature unit (celsius or fahrenheit)

### CalculatorTool
- **Tool**: `Calculator-add`
- **Description**: Add two numbers
- **Parameters**:
  - `a` (required): First number
  - `b` (required): Second number

- **Tool**: `Calculator-multiply`
- **Description**: Multiply two numbers
- **Parameters**:
  - `a` (required): First number
  - `b` (required): Second number

### FileInfoTool
- **Tool**: `get_file_info`
- **Description**: Get information about a file
- **Parameters**:
  - `path` (required): The file path

## Integration with Claude Code SDK

### Add to MCP Configuration

Add the following to your Claude Code MCP configuration (usually in `.claude/mcp-config.json` or similar):

```json
{
  "mcpServers": {
    "function-provider-server": {
      "command": "npx",
      "args": ["mcp-remote@latest", "http://localhost:5123/mcp"]
    }
  }
}
```

### Test with mcp-remote

You can test the server using `mcp-remote`:

```bash
npx mcp-remote@latest http://localhost:5123/mcp
```

## Creating Your Own Function Providers

### 1. Implement IFunctionProvider

```csharp
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Schema;

public class MyCustomTool : IFunctionProvider
{
    public string ProviderName => "MyCustomProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "my_tool",
            Description = "What my tool does",
            Parameters = new[]
            {
                new FunctionParameterContract
                {
                    Name = "param1",
                    Description = "First parameter",
                    ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                    IsRequired = true
                }
            }
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = ExecuteAsync,
            ProviderName = ProviderName
        };
    }

    private async Task<string> ExecuteAsync(string argumentsJson)
    {
        // Parse arguments, execute logic, return JSON result
        // ...
    }
}
```

### 2. Register with MCP Server

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register your function provider
builder.Services.AddFunctionProvider<MyCustomTool>();

// Add MCP server
builder.Services.AddMcpServerFromFunctionProviders();

var app = builder.Build();
app.MapMcpFunctionProviders();
await app.RunAsync();
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

## Dependencies

- `AchieveAi.LmDotnetTools.LmCore` - Core function provider interfaces
- `ModelContextProtocol.AspNetCore` - MCP protocol implementation
- ASP.NET Core 8.0 or 9.0

## License

Same as the parent LmDotnetTools project.
