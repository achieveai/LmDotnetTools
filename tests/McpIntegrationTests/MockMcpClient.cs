using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.McpMiddleware;
using ModelContextProtocol;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;

/// <summary>
/// Mock implementation of IMcpClient for testing
/// </summary>
public class MockMcpClient : IMcpClient
{
  private readonly string _id;
  private readonly string _name;

  public MockMcpClient(string id, string name)
  {
    _id = id;
    _name = name;
  }

  public Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken = default)
  {
    // Return a mock list of tools
    var tools = new List<McpClientTool>
    {
      new() { Name = "GreetingTool.SayHello", Description = "Says hello to a user" },
      new() { Name = "GreetingTool.SayGoodbye", Description = "Says goodbye to a user" },
      new() { Name = "CalculatorTool.Add", Description = "Adds two numbers" },
      new() { Name = "CalculatorTool.Subtract", Description = "Subtracts two numbers" },
      new() { Name = "CalculatorTool.Multiply", Description = "Multiplies two numbers" },
      new() { Name = "CalculatorTool.Divide", Description = "Divides two numbers" }
    };
    
    return Task.FromResult<IList<McpClientTool>>(tools);
  }

  public Task<CallToolResponse> CallToolAsync(
    string toolName,
    Dictionary<string, object?> arguments,
    CancellationToken cancellationToken = default)
  {
    // Log the tool call for debugging
    Console.WriteLine($"MockMcpClient.CallToolAsync called with tool: {toolName}");
    Console.WriteLine($"Arguments: {string.Join(", ", arguments.Select(a => $"{a.Key}={a.Value}"))}");
    
    // Parse the tool name to extract the method part (after the dot)
    string methodName = toolName;
    if (toolName.Contains("."))
    {
      // Extract the part after the last dot (e.g., "Add" from "CalculatorTool.Add")
      methodName = toolName.Substring(toolName.LastIndexOf('.') + 1);
      Console.WriteLine($"Parsed method name: {methodName}");
    }
    
    // Return a mock response based on the method name
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
          response = "8"; // Default mock result for 5 + 3
        }
        break;
        
      case "Subtract":
        response = "2"; // Mock result for 5 - 3
        break;
        
      case "Multiply":
        response = "15"; // Mock result for 5 * 3
        break;
        
      case "Divide":
        response = "1.6666666666666667"; // Mock result for 5 / 3
        break;
        
      default:
        response = $"Unknown tool: {toolName}";
        break;
    }
    
    Console.WriteLine($"MockMcpClient response: {response}");
    
    return Task.FromResult(new CallToolResponse
    {
      Content = new List<Content>
      {
        new Content
        {
          Type = "text",
          Text = response
        }
      }
    });
  }
}
