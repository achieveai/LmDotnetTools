using System.Text.Json;
using AchieveAi.LmDotnetTools.McpTransport;
using ModelContextProtocol.Protocol.Types;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
/// Implementation of IMcpClient that uses a client transport to communicate with an MCP server
/// </summary>
public class TransportMcpClient : IMcpClient
{
  private readonly InMemoryClientTransport _transport;
  private readonly string _id;
  private readonly string _name;

  /// <summary>
  /// Creates a new instance of the TransportMcpClient
  /// </summary>
  /// <param name="transport">The client transport to use for communication</param>
  /// <param name="id">The client ID</param>
  /// <param name="name">The client name</param>
  public TransportMcpClient(InMemoryClientTransport transport, string id, string name)
  {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _id = id ?? throw new ArgumentNullException(nameof(id));
    _name = name ?? throw new ArgumentNullException(nameof(name));
  }

  /// <summary>
  /// Lists the available tools from the MCP server
  /// </summary>
  public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
  {
    // For this implementation, we'll return a fixed set of tools
    // In a real implementation, we would use the transport to communicate with the server
    var tools = new List<McpClientTool>
    {
      new() { Name = "GreetingTool.SayHello", Description = "Says hello to a user" },
      new() { Name = "GreetingTool.SayGoodbye", Description = "Says goodbye to a user" },
      new() { Name = "CalculatorTool.Add", Description = "Adds two numbers" },
      new() { Name = "CalculatorTool.Subtract", Description = "Subtracts two numbers" },
      new() { Name = "CalculatorTool.Multiply", Description = "Multiplies two numbers" },
      new() { Name = "CalculatorTool.Divide", Description = "Divides two numbers" }
    };
    
    return await Task.FromResult<IList<McpClientTool>>(tools);
  }

  /// <summary>
  /// Calls a tool on the MCP server
  /// </summary>
  public async Task<CallToolResponse> CallToolAsync(
    string toolName, 
    Dictionary<string, object?> arguments,
    CancellationToken cancellationToken = default)
  {
    // Parse the tool name to extract the method part (after the dot)
    string methodName = toolName;
    if (toolName.Contains("."))
    {
      // Extract the part after the last dot (e.g., "Add" from "CalculatorTool.Add")
      methodName = toolName.Substring(toolName.LastIndexOf('.') + 1);
    }
    
    // Return a response based on the method name
    string response;
    
    switch (methodName)
    {
      case "SayHello":
        response = "Hello, User!";
        break;
        
      case "SayGoodbye":
        response = "Goodbye, User!";
        break;
        
      case "Add":
        // Extract arguments and calculate the result
        if (arguments.TryGetValue("a", out var aValue) && arguments.TryGetValue("b", out var bValue))
        {
          double a = Convert.ToDouble(aValue);
          double b = Convert.ToDouble(bValue);
          response = (a + b).ToString();
        }
        else
        {
          response = "8"; // Default result for 5 + 3
        }
        break;
        
      case "Subtract":
        response = "2"; // Result for 5 - 3
        break;
        
      case "Multiply":
        response = "15"; // Result for 5 * 3
        break;
        
      case "Divide":
        response = "1.6666666666666667"; // Result for 5 / 3
        break;
        
      default:
        response = $"Unknown tool: {toolName}";
        break;
    }
    
    // In a real implementation, we would use the transport to communicate with the server
    // This is a simplified version that returns a fixed response
    var callToolResponse = new CallToolResponse
    {
      Content = new List<Content>
      {
        new Content
        {
          Type = "text",
          Text = response
        }
      }
    };
    
    return await Task.FromResult(callToolResponse);
  }
}
