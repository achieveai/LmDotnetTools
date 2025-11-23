# LmConfigUsageExample

This example demonstrates how to use the LmConfig library to configure and use different language model providers, including the new **ClaudeAgentSDK provider**.

## Examples Included

1. **File-Based Configuration Loading** - Traditional configuration from models.json
2. **Embedded Resource Configuration** - Loading config from embedded resources
3. **Stream Factory Configuration** - Loading config from any stream source
4. **IOptions Pattern Configuration** - Using .NET's IOptions pattern
5. **Provider Availability Checking** - Checking which providers are available
6. **ModelId Resolution** - Demonstrating model ID translation
7. **ClaudeAgentSDK Provider with MCP Tools** - NEW! Test the ClaudeAgentSDK provider

## Running the Examples

### Prerequisites

For most examples:
- .NET 9.0 SDK
- Environment variables set for API keys (e.g., `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`)

**For the ClaudeAgentSDK example (Example 7), you additionally need:**
- Node.js installed (v18 or higher)
- Claude Agent SDK CLI installed globally:
  ```bash
  npm install -g @anthropic-ai/claude-agent-sdk
  ```
- `ANTHROPIC_API_KEY` environment variable set
- A `.mcp.json` configuration file in the project root (provided)

### Running the Example

```bash
cd example/LmConfigUsageExample
dotnet run
```

All 7 examples will run sequentially.

## ClaudeAgentSDK Provider - Example 7

### What It Does

Example 7 demonstrates the ClaudeAgentSDK provider, which:
- Uses the official `@anthropic-ai/claude-agent-sdk` Node.js CLI
- Provides access to **MCP (Model Context Protocol) tools**
- Maintains a long-lived Node.js process for efficient interaction
- Supports streaming responses with reasoning, tool calls, and results
- Automatically manages JSONL communication between .NET and Node.js

### Configuration

The ClaudeAgentSDK provider is configured in `models.json`:

```json
{
  "id": "claude-sonnet-4-5",
  "is_reasoning": true,
  "capabilities": {
    "thinking": { ... },
    "multimodal": { ... },
    "function_calling": {
      "supports_tools": true,
      "supported_tool_types": ["mcp"]
    },
    ...
  },
  "providers": [
    {
      "name": "ClaudeAgentSDK",
      "model_name": "claude-sonnet-4-5-20250929",
      "priority": 1,
      ...
    }
  ]
}
```

### MCP Server Configuration (`.mcp.json`)

The `.mcp.json` file defines which MCP servers are available to the agent:

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": [
        "-y",
        "@modelcontextprotocol/server-filesystem",
        "D:/Source/repos/LmDotnetTools/example/LmConfigUsageExample"
      ]
    },
    "fetch": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-fetch"]
    }
  }
}
```

This configuration provides:
- **filesystem** - File system access tools (read, write, list files, etc.)
- **fetch** - HTTP fetching tools for web requests

### Example Output

When you run Example 7, you'll see:

1. Provider availability check
2. Model resolution (claude-sonnet-4-5 → ClaudeAgentSDK)
3. Agent creation confirmation
4. Streaming response showing:
   - **Thinking** - Claude's reasoning process
   - **Tool Calls** - MCP tools being invoked
   - **Tool Results** - Results from MCP servers
   - **Text** - Final assistant response
   - **Usage** - Token consumption

### How to Use the ClaudeAgentSDK Provider

**Basic Usage:**

```csharp
using AchieveAi.LmDotnetTools.LmConfig.Services;
using Microsoft.Extensions.DependencyInjection;

// Setup DI
var services = new ServiceCollection();
services.AddLmConfig(configuration);

var serviceProvider = services.BuildServiceProvider();
var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

// Resolve the ClaudeAgentSDK provider
var resolution = await modelResolver.ResolveProviderAsync("claude-sonnet-4-5");

// Create the agent
var factory = serviceProvider.GetRequiredService<IProviderAgentFactory>();
var agent = factory.CreateStreamingAgent(resolution);

// Use the agent
var messages = new List<IMessage>
{
    new TextMessage { Text = "List the files in the current directory", Role = Role.User }
};

var options = new GenerateReplyOptions
{
    ModelId = "claude-sonnet-4-5",
    Temperature = 0.7f
};

// Stream the response
var streamTask = await agent.GenerateReplyStreamingAsync(messages, options);
await foreach (var msg in streamTask)
{
    // Handle different message types
    switch (msg)
    {
        case TextMessage text:
            Console.Write(text.Text);
            break;
        case ReasoningMessage reasoning:
            Console.WriteLine($"[Thinking: {reasoning.Reasoning}]");
            break;
        case ToolsCallMessage toolCall:
            Console.WriteLine($"[Tool: {toolCall.ToolCalls[0].FunctionName}]");
            break;
        case ToolsCallResultMessage result:
            Console.WriteLine($"[Result: {result.ToolCallResults[0].Result}]");
            break;
        case UsageMessage usage:
            Console.WriteLine($"[Tokens: {usage.Usage.TotalTokens}]");
            break;
    }
}

// Dispose when done
if (agent is IDisposable disposable)
{
    disposable.Dispose();
}
```

### Customizing MCP Servers

To add or modify MCP servers, edit `.mcp.json`:

```json
{
  "mcpServers": {
    "your-custom-server": {
      "command": "node",
      "args": ["path/to/your/mcp-server.js"]
    }
  }
}
```

See the [MCP documentation](https://modelcontextprotocol.io) for available MCP servers.

### Troubleshooting

**Provider not available:**
- Ensure `ANTHROPIC_API_KEY` is set
- Verify Node.js is installed: `node --version`
- Verify Claude Agent SDK is installed: `npm list -g @anthropic-ai/claude-agent-sdk`
- Check `.mcp.json` exists in the output directory

**MCP servers not working:**
- Verify MCP server packages are installed (they're installed on-demand via `npx -y`)
- Check file paths in `.mcp.json` are correct
- Review logs for MCP server startup errors

**Performance issues:**
- The Node.js process is reused across requests (long-lived)
- First request may be slower due to MCP server initialization
- Subsequent requests should be faster

## Additional Resources

- [LmConfig Documentation](../../src/LmConfig/docs/)
- [ClaudeAgentSDK Implementation Status](../../scratchpad/conversation_memories/ClaudeAgentSDK-Provider-Implementation/README.md)
- [Model Context Protocol](https://modelcontextprotocol.io)
- [Anthropic Claude SDK](https://github.com/anthropics/claude-agent-sdk)
