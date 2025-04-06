# Example Python MCP Client

This example demonstrates how to use the AchieveAi.LmDotnetTools libraries to:

1. Connect to a Python MCP (Model Context Protocol) server
2. List available tools from the server
3. Create an OpenAI-based LLM agent
4. Integrate the MCP tools with the LLM agent using middleware
5. Execute a conversation with tool calls

## Prerequisites

- .NET 9.0 SDK
- A running instance of the Python MCP Server (from `McpServers/PythonMCPServer`)
- An OpenAI API key

## How to Run

1. Make sure the Python MCP Server is running. See the instructions in `McpServers/PythonMCPServer`.

2. Update the Program.cs file with your OpenAI API key:
   ```csharp
   private const string S_openAiKey = "YOUR_OPENAI_KEY_HERE";
   ```

3. Build and run the example:
   ```
   dotnet build
   dotnet run
   ```

## Example Flow

The example demonstrates:

1. Connecting to the MCP server and listing available tools
2. Creating an LLM agent with OpenAI
3. Setting up the middleware pipeline to enable tool calls
4. Asking the agent to write a Python function and executing it using the MCP server
5. Handling the results of the tool call and returning them to the agent
6. Getting the agent's final response

## Key Components

- **LmCore**: Provides the core abstractions for agents, messages, and middleware
- **OpenAIProvider**: Implements the OpenAI client and agent
- **McpMiddleware**: Connects to the MCP server and handles tool calls

## Learn More

For more details about the libraries used in this example, explore the source code in:
- `src/LmCore`
- `src/OpenAIProvider`
- `src/McpMiddleware`
