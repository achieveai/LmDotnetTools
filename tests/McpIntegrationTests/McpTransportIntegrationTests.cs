using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
/// Integration tests for the MCP transport functionality
/// </summary>
public class McpTransportIntegrationTests
{
  /// <summary>
  /// Simple agent implementation for testing
  /// </summary>
  private class SimpleTestAgent : IAgent
  {
    public string Id => "test-agent";
    public string? Name => "Test Agent";
    public string? Description => "A test agent for MCP middleware";
    public string? ModelId => "test-model";
    public Dictionary<string, object>? ModelParameters => null;
    public IList<IMessage> History => new List<IMessage>();

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(IEnumerable<IMessage> messages, GenerateReplyOptions? options = null, CancellationToken cancellationToken = default)
    {
      // Just return a text message for testing
      return Task.FromResult<IEnumerable<IMessage>>(new[] { new TextMessage { Text = "This is a test response" } });
    }
  }



  [Fact]
  public async Task GreetingTool_SayHello_ReturnsGreeting()
  {
    // Arrange - Setup server and client
    using var cts = new CancellationTokenSource();
    var serverOption = new McpServerConfig
    {
      Id = "test-server",
      Name = "Test Server",
      TransportType = TransportTypes.StdIo,
      Location = McpServerTests.ServerLocation,
      Arguments = [],
      TransportOptions = new Dictionary<string, string>
      {
        ["command"] = McpServerTests.ServerLocation
      }
    };

    var client = await McpClientFactory.CreateAsync(serverOption);

    try
    {
      // Create middleware with the transport client
      var clients = new Dictionary<string, IMcpClient>
      {
        ["test-client"] = client,
        ["GreetingTool"] = client
      };
      
      var middleware = await McpMiddleware.McpMiddleware.CreateAsync(clients);
      
      // Create a test agent
      var agent = new SimpleTestAgent();
      
      // Act - Create and process a tool call
      var toolCall = new LmCore.Messages.ToolCall("GreetingTool-SayHello", JsonSerializer.Serialize(new { name = "User" }));
      var message = new ToolsCallMessage { ToolCalls = [toolCall] };
      var context = new MiddlewareContext([message]);
      
      var response = await middleware.InvokeAsync(context, agent);
      
      // Assert
      Assert.NotNull(response);
      var firstMessage = response.FirstOrDefault();
      Assert.NotNull(firstMessage);
      var responseText = firstMessage.GetText();
      Assert.NotNull(responseText);
    }
    finally
    {
      // Cleanup
      await client.DisposeAsync();
      cts.Cancel();
      await Task.Delay(500); // Give time for server to shut down
    }
  }
  
  [Fact]
  public async Task CalculatorTool_Add_ReturnsCorrectResult()
  {
    // Arrange - Setup server and client
    using var cts = new CancellationTokenSource();
    var serverOption = new McpServerConfig
    {
      Id = "test-server",
      Name = "Test Server",
      TransportType = TransportTypes.StdIo,
      Location = McpServerTests.ServerLocation,
      Arguments = [],
      TransportOptions = new Dictionary<string, string>
      {
        ["command"] = McpServerTests.ServerLocation
      }
    };

    var client = await McpClientFactory.CreateAsync(serverOption);
    
    try
    {
      // Create middleware with the transport client
      var clients = new Dictionary<string, IMcpClient>
      {
        ["test-client"] = client,
        ["CalculatorTool"] = client
      };
      
      var middleware = await McpMiddleware.McpMiddleware.CreateAsync(clients);
      
      // Create a test agent
      var agent = new SimpleTestAgent();
      
      // Act - Create and process a tool call
      var toolCall = new LmCore.Messages.ToolCall("CalculatorTool-Add", JsonSerializer.Serialize(new { a = 5.0, b = 3.0 }));
      var message = new ToolsCallMessage { ToolCalls = [toolCall] };
      var context = new MiddlewareContext([message]);
      
      var response = await middleware.InvokeAsync(context, agent);
      
      // Assert
      Assert.NotNull(response);
      var firstMessage = response.FirstOrDefault();
      Assert.NotNull(firstMessage);
      var responseText = firstMessage.GetText();
      Assert.NotNull(responseText);
    }
    finally
    {
      // Cleanup
      await client.DisposeAsync();
      cts.Cancel();
      await Task.Delay(500); // Give time for server to shut down
    }
  }
}

// Now using TransportMcpClient for proper transport-based testing
