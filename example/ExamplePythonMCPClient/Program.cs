using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient;

class Program
{
  // For a real application, you would store this in a secure configuration
  private const string S_openAiKey = "REPLACE_WITH_YOUR_OPENAI_KEY";
  
  static async Task Main(string[] args)
  {
    Console.WriteLine("Example Python MCP Client Demo");
    
    // Create the MCP client to connect to the Python server
    var mcpOptions = new McpServerConfig
    {
      Id = "python-mcp",
      Name = "Python MCP Server",
      TransportType = TransportTypes.StdIo,
      TransportOptions = new Dictionary<string, string>
      {
        ["command"] = "uvx d:/Source/repos/LmDotnetTools/McpServers/PythonMCPServer"
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
      var openClient = new OpenClient(S_openAiKey, "https://api.openai.com/v1");
      var llmAgent = new OpenClientAgent("GPT-4o", openClient);
      
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
        Temperature = 0.7f,
        MaxToken = 2000,
        ExtraProperties = System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty.Add("model", "gpt-4o")
      };
      
      var reply = await agentWithMcp.GenerateReplyAsync(messages, options);
      
      // Display the response
      if (reply is TextMessage textReply)
      {
        Console.WriteLine("\nAgent Response:\n");
        Console.WriteLine(textReply.Text);
      }
      else if (reply is ToolsCallMessage toolsCallMessage)
      {
        Console.WriteLine("\nAgent is making tool calls:\n");
        foreach (var toolCall in toolsCallMessage.ToolCalls)
        {
          Console.WriteLine($"Tool: {toolCall.FunctionName}");
          Console.WriteLine($"Arguments: {toolCall.FunctionArgs}");
          
          // Execute the tool call
          var result = await mcpClient.CallToolAsync(
            toolCall.FunctionName,
            JsonSerializer.Deserialize<Dictionary<string, object?>>(toolCall.FunctionArgs) ?? new Dictionary<string, object?>(),
            CancellationToken.None);
          
          Console.WriteLine("\nTool Response:\n");
          
          // Extract text from the content items
          string responseText = "";
          if (result.Content != null)
          {
            foreach (var content in result.Content)
            {
              if (content.Type == "text" && !string.IsNullOrEmpty(content.Text))
              {
                responseText += content.Text;
                Console.WriteLine(content.Text);
              }
            }
          }
          else
          {
            responseText = "[No content in response]";
            Console.WriteLine(responseText);
          }
          
          // Add the tool response back to messages
          messages.Add(toolsCallMessage);
          messages.Add(new TextMessage
          {
            Role = Role.Function,
            FromAgent = toolCall.FunctionName,
            Text = responseText
          });
          
          // Get the agent's final response
          reply = await agentWithMcp.GenerateReplyAsync(messages, options);
          
          if (reply is TextMessage finalTextReply)
          {
            Console.WriteLine("\nFinal Agent Response:\n");
            Console.WriteLine(finalTextReply.Text);
          }
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"\nError: {ex.Message}");
      Console.WriteLine(ex.StackTrace);
    }
    
    Console.WriteLine("\nPress any key to exit...");
    Console.ReadKey();
  }
}
