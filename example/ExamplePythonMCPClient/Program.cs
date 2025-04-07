using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using DotNetEnv;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;
using System.IO;
using System.Collections.Immutable;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient;

class Program
{
  static async Task Main()
  {
    // Load environment variables from .env file
    LoadEnvironmentVariables();
    
    string API_KEY = Environment.GetEnvironmentVariable("LLM_API_KEY")!;
    string API_URL = Environment.GetEnvironmentVariable("LLM_API_BASE_URL")!;
    Console.WriteLine("Example Python MCP Client Demo");
    
    // Create the MCP client to connect to the Python server
    var mcpOptions = new McpServerConfig
    {
      Id = "python-mcp",
      Name = "Python MCP Server",
      TransportType = TransportTypes.StdIo,
      TransportOptions = new Dictionary<string, string>
      {
        ["command"] = "uvx d:/Source/repos/LmDotnetTools/McpServers/PythonMCPServer --image pyexec"
      }
    };
    
    var mcpClient = await McpClientFactory.CreateAsync(mcpOptions);
    
    try
    {
      // List available tools from the MCP server
      Console.WriteLine("\nListing available tools from the MCP server...");
      var tools = await mcpClient.ListToolsAsync();
      
      foreach (var tool in tools)
      {
        Console.WriteLine($"- {tool.Name}: {tool.Description}");
      }
      
      // Create an OpenAI client
      var openClient = new OpenClient(API_KEY, API_URL);
      var llmAgent = new OpenClientAgent("meta-llama/llama-4-maverick", openClient);
      
      // Create the agent pipeline with MCP middleware
      var mcpClientDictionary = new Dictionary<string, ModelContextProtocol.Client.IMcpClient>
      {
        { mcpOptions.Id, mcpClient }
      };
      
      // Create the middleware using the factory
      var mcpMiddlewareFactory = new McpMiddlewareFactory();
      var mcpMiddleware = await mcpMiddlewareFactory.CreateFromClientsAsync(mcpClientDictionary);
      var agentWithMcp = llmAgent.WithMiddleware(mcpMiddleware);
      
      // Example messages for the agent
      var messages = new List<IMessage>
      {
        new TextMessage
        {
          Role = Role.System,
          Text = "You are a helpful assistant that can use tools to help users. When you need to execute Python code, use the execute_python_in_container tool."
        },
        new TextMessage
        {
          Role = Role.User,
          Text = "Write a Python function to calculate the Fibonacci sequence up to n terms and then call it for n=10."
        }
      };
      
      // Generate a response with the agent using MCP middleware for tool calls
      Console.WriteLine("\nGenerating a response with the agent...");
      
      var options = new GenerateReplyOptions
      {
        ModelId = "meta-llama/llama-4-maverick",
        Temperature = 0.7f,
        MaxToken = 2000,
        ExtraProperties = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty
      };
      
      var reply = await agentWithMcp.GenerateReplyAsync(messages, options);
      
      // Display the response
      if (reply is TextMessage textReply)
      {
        Console.WriteLine("\nAgent Response:\n");
        Console.WriteLine(textReply.Text);
      }
      else if (reply is ToolCallAggregateMessage toolsCallMessage)
      {
        Console.WriteLine("\nAgent is making tool calls:\n");
        foreach (var (toolCall, result) in toolsCallMessage.ToolCallMessage.ToolCalls.Zip(toolsCallMessage.ToolCallResult.ToolCallResults))
        {
          Console.WriteLine($"Tool: {toolCall.FunctionName}");
          Console.WriteLine($"Arguments: {toolCall.FunctionArgs}");
          
          Console.WriteLine("\nTool Response:\n");
          Console.WriteLine(result);
        }
      }

      messages.Add(reply);
      var reply2 = await agentWithMcp.GenerateReplyAsync(messages, options);
      Console.WriteLine("\nAgent Response:\n");
      Console.WriteLine(reply2 switch {
        ICanGetText txtReply => txtReply.GetText(),
        ToolsCallResultMessage toolsCallMessage => string.Join("\n", toolsCallMessage.ToolCallResults.Select(tc => tc.Result)),
        _ => reply.ToString()
      });
    }
    catch (Exception ex)
    {
      Console.WriteLine($"\nError: {ex.Message}");
      Console.WriteLine(ex.StackTrace);
    }
    
    Console.WriteLine("\nPress any key to exit...");
    Console.ReadKey();
  }
  
  /// <summary>
  /// Loads environment variables from .env file in the project root
  /// </summary>
  /// <remarks>
  /// Tries multiple locations to find the .env file:
  /// 1. Current directory
  /// 2. Project directory
  /// 3. Solution root directory
  /// </remarks>
  private static void LoadEnvironmentVariables()
  {
    var curPath = Environment.CurrentDirectory;
    while (curPath != null && !string.IsNullOrEmpty(curPath) && !File.Exists(Path.Combine(curPath, ".env")))
    {
      curPath = Path.GetDirectoryName(curPath);
    }

    _ = curPath != null && !string.IsNullOrEmpty(curPath) && File.Exists(Path.Combine(curPath, ".env"))
      ? DotNetEnv.Env.Load(Path.Combine(curPath, ".env"))
      : throw new FileNotFoundException(".env file not found in the current directory or any parent directories.");
  }
}
