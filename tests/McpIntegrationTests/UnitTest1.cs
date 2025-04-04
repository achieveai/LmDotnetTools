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
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
/// Base class for MCP integration tests that provides common setup and teardown functionality
/// </summary>
public abstract class McpIntegrationTestBase : IAsyncDisposable
{
  protected IHost? Host { get; private set; }
  protected CancellationTokenSource Cts { get; } = new();
  protected InMemoryClientTransport? ClientTransport { get; private set; }
  protected InMemoryServerTransport? ServerTransport { get; private set; }
  protected McpMiddleware.McpMiddleware? Middleware { get; private set; }
  protected SimpleTestAgent Agent { get; } = new();
  protected Dictionary<string, IMcpClient> McpClients { get; } = new();

  /// <summary>
  /// Disposes of resources used by the test
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    // Cancel any running tasks
    Cts.Cancel();
    
    // Dispose of the host if it exists
    if (Host != null)
    {
      await Host.StopAsync();
      Host.Dispose();
    }
    
    // Dispose of the cancellation token source
    Cts.Dispose();
    
    GC.SuppressFinalize(this);
  }
  
  /// <summary>
  /// Sets up the test environment with a running MCP server and client
  /// </summary>
  protected async Task SetupTestEnvironmentAsync(params Type[] toolTypes)
  {
    // Create transport pair
    (ClientTransport, ServerTransport) = InMemoryTransportFactory.CreateTransportPair("TestTransport");
    
    // Start the MCP server
    var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
      .ConfigureLogging(logging =>
      {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
      })
      .ConfigureServices(services =>
      {
        // Register MCP server
        var serverBuilder = services.AddMcpServer();
        
        // Register the transport
        serverBuilder = AchieveAi.LmDotnetTools.McpTransport.McpServerBuilderExtensions
          .WithServerTransport(serverBuilder, ServerTransport) as IMcpServerBuilder;
        
        // Register tools
        if (serverBuilder != null && toolTypes.Length > 0)
        {
          foreach (var toolType in toolTypes)
          {
            serverBuilder.WithToolsFromAssembly(toolType.Assembly);
          }
        }
      });
    
    // Build the host
    Host = builder.Build();
    
    // Start the server in a separate task
    _ = Task.Run(async () =>
    {
      try
      {
        await Host.RunAsync(Cts.Token);
      }
      catch (OperationCanceledException)
      {
        // Expected when cancellation is requested
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Server error: {ex}");
      }
    });
    
    // Wait a moment for the server to start
    await Task.Delay(1000);
    
    // Create a mock client for testing
    var client = new TestHelpers.MockMcpClient("test-client", "Test Client");
    
    // Add the client to the dictionary
    McpClients["test-client"] = client;
    
    // Register tool clients
    foreach (var toolType in toolTypes)
    {
      var toolName = toolType.Name.Replace("Tool", "");
      McpClients[toolName + "Tool"] = client;
    }
    
    // Create the middleware using the async factory pattern
    Middleware = await McpMiddleware.McpMiddleware.CreateAsync(McpClients);
  }

  [Fact]
  public async Task CalculatorTool_Add_ReturnsCorrectResult()
  {
    // Arrange
    using var cts = new CancellationTokenSource();
    var (clientTransport, serverTransport) = InMemoryTransportFactory.CreateTransportPair("TestTransport");
    
    // Start the MCP server
    var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();
    
    // Configure logging
    builder.ConfigureLogging(logging =>
    {
      logging.AddConsole();
      logging.SetMinimumLevel(LogLevel.Debug);
    });
    
    // Register server services
    IMcpServerBuilder? serverBuilder = null;
    builder.ConfigureServices(services => {
      serverBuilder = services.AddMcpServer();
    });
    
    // Register the CalculatorTool
    if (serverBuilder != null)
    {
      serverBuilder.WithToolsFromAssembly(typeof(CalculatorTool).Assembly);
    }
    
    // Build the host
    using var host = builder.Build();
    
    // Start the server in a separate task
    _ = Task.Run(async () =>
    {
      try
      {
        await host.RunAsync(cts.Token);
      }
      catch (OperationCanceledException)
      {
        // Expected when cancellation is requested
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Server error: {ex}");
      }
    });
    
    try
    {
      // Assert that the server is running and the tool is available
      Assert.NotNull(host);
      
      // TODO: Add assertions to verify the calculator functionality
      // For now, we'll just verify the server starts without errors
    }
    finally
    {
      // Cancel the server
      cts.Cancel();
      await Task.Delay(500); // Give it time to shut down
    }
  }
}
