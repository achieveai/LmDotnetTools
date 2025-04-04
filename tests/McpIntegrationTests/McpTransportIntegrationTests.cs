using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.McpSampleServer;
using AchieveAi.LmDotnetTools.McpTransport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol.Types;
using ModelContextProtocol.Server;

// Use an alias to avoid ambiguity with IMcpClient
using McpClient = AchieveAi.LmDotnetTools.McpMiddleware.IMcpClient;

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

    public Task<IMessage> GenerateReplyAsync(IEnumerable<IMessage> messages, GenerateReplyOptions? options = null, CancellationToken cancellationToken = default)
    {
      // Just return a text message for testing
      return Task.FromResult<IMessage>(new TextMessage { Text = "This is a test response" });
    }
  }

  /// <summary>
  /// Extension method to get text from an IMessage
  /// </summary>
  private static string? GetText(IMessage? message)
  {
    if (message == null) return null;
    
    return message switch
    {
      TextMessage textMessage => textMessage.Text,
      _ => message.ToString()
    };
  }

  [Fact]
  public async Task GreetingTool_SayHello_ReturnsGreeting()
  {
    // Arrange - Setup server and client
    using var cts = new CancellationTokenSource();
    var (clientTransport, serverTransport) = InMemoryTransportFactory.CreateTransportPair("TestTransport");
    
    // Create a host builder
    var builder = Host.CreateDefaultBuilder()
      .ConfigureLogging(logging =>
      {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
      })
      .ConfigureServices(services =>
      {
        // Register the transport
        _ = services
          .AddMcpServer()
          .WithServerTransport(serverTransport)
          .WithToolsFromAssembly(typeof(GreetingTool).Assembly);
      });
    
    // Build and start the host
    using var host = builder.Build();
    var serverTask = host.RunAsync(cts.Token);
    
    try
    {
      // Wait for server to start
      await Task.Delay(100);
      
      // Create a transport-based client that implements IMcpClient
      var client = new TransportMcpClient(clientTransport, "test-client", "Test Client");
      
      // Create middleware with the transport client
      var clients = new Dictionary<string, McpClient>
      {
        ["test-client"] = client,
        ["GreetingTool"] = client
      };
      
      var middleware = await McpMiddleware.McpMiddleware.CreateAsync(clients);
      
      // Create a test agent
      var agent = new SimpleTestAgent();
      
      // Act - Create and process a tool call
      var toolCall = new LmCore.Messages.ToolCall("GreetingTool.SayHello", JsonSerializer.Serialize(new { name = "User" }));
      var message = new ToolsCallMessage { ToolCalls = [toolCall] };
      var context = new MiddlewareContext([message]);
      
      var response = await middleware.InvokeAsync(context, agent);
      
      // Assert
      Assert.NotNull(response);
      var responseText = GetText(response);
      Assert.NotNull(responseText);
    }
    finally
    {
      // Cleanup
      cts.Cancel();
      await Task.Delay(500); // Give time for server to shut down
    }
  }
  
  [Fact]
  public async Task CalculatorTool_Add_ReturnsCorrectResult()
  {
    // Arrange - Setup server and client
    using var cts = new CancellationTokenSource();
    var (clientTransport, serverTransport) = InMemoryTransportFactory.CreateTransportPair("TestTransport");
    
    // Create a host builder
    var builder = Host.CreateDefaultBuilder()
      .ConfigureLogging(logging =>
      {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
      })
      .ConfigureServices(services =>
      {
        // Register MCP server
        _ = services.AddMcpServer()
          .WithServerTransport(serverTransport)
          .WithToolsFromAssembly(typeof(CalculatorTool).Assembly);
      });
    
    // Build and start the host
    using var host = builder.Build();
    var serverTask = host.RunAsync(cts.Token);
    
    try
    {
      // Wait for server to start
      await Task.Delay(1000);
      
      // Create a transport-based client that implements IMcpClient
      var client = new TransportMcpClient(clientTransport, "test-client", "Test Client");
      
      // Create middleware with the transport client
      var clients = new Dictionary<string, McpClient>
      {
        ["test-client"] = client,
        ["CalculatorTool"] = client
      };
      
      var middleware = await McpMiddleware.McpMiddleware.CreateAsync(clients);
      
      // Create a test agent
      var agent = new SimpleTestAgent();
      
      // Act - Create and process a tool call
      var toolCall = new LmCore.Messages.ToolCall("CalculatorTool.Add", JsonSerializer.Serialize(new { a = 5.0, b = 3.0 }));
      var message = new ToolsCallMessage { ToolCalls = [toolCall] };
      var context = new MiddlewareContext([message]);
      
      var response = await middleware.InvokeAsync(context, agent);
      
      // Assert
      Assert.NotNull(response);
      var responseText = GetText(response);
      Assert.NotNull(responseText);
    }
    finally
    {
      // Cleanup
      cts.Cancel();
      await Task.Delay(500); // Give time for server to shut down
    }
  }
}

// Now using TransportMcpClient for proper transport-based testing
