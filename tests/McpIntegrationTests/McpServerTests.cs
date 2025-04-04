using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;
using AchieveAi.LmDotnetTools.McpMiddleware;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
/// Tests for the MCP server functionality
/// </summary>
public class McpServerTests
{
  [Fact]
  public async Task GreetingTool_SayHello_ReturnsGreeting()
  {
    // Create a simpler test that focuses on the SimpleTestAgent and tool calls
    
    // Create a mock client that implements IMcpClient
    var mockClient = new TestHelpers.MockMcpClient("test-client", "Test Client");
    
    // Create middleware with the mock client
    var clients = new Dictionary<string, IMcpClient>
    {
      ["test-client"] = mockClient,
      ["GreetingTool"] = mockClient
    };
    
    var middleware = await McpMiddleware.McpMiddleware.CreateAsync(clients);
    
    // Create a test agent and inject a tool call message to be returned
    var agent = new SimpleTestAgent();
    var toolCall = new LmCore.Messages.ToolCall("GreetingTool.SayHello", JsonSerializer.Serialize(new { name = "User" }));
    agent.InjectMessage(new ToolsCallMessage { ToolCalls = [toolCall] });
    
    // Create a simple text message as the initial message
    var initialMessage = new TextMessage { Text = "Hello, I need help" };
    var context = new MiddlewareContext([initialMessage]);
    
    Console.WriteLine("Starting test - calling middleware.InvokeAsync");
    
    // Act - Process the message through the middleware
    var response = await middleware.InvokeAsync(context, agent);
    
    Console.WriteLine($"Test completed - response type: {response?.GetType().Name}");
    Console.WriteLine($"Agent received messages count: {agent.ReceivedMessages.Count}");
    
    // Assert
    Assert.NotNull(response);
    var responseText = GetText(response);
    Assert.NotNull(responseText);
    
    // The response should contain a greeting from the MockMcpClient
    Console.WriteLine($"Response text: {responseText}");
    Assert.Contains("Hello", responseText);
    
    // Verify that the agent received the initial message
    Assert.NotEmpty(agent.ReceivedMessages);
    foreach (var msg in agent.ReceivedMessages)
    {
      Console.WriteLine($"Received message type: {msg.GetType().Name}");
    }
    
    var receivedMessage = agent.ReceivedMessages.FirstOrDefault();
    Assert.NotNull(receivedMessage);
    Assert.IsType<TextMessage>(receivedMessage);
    
    // The message received by the agent should be the initial text message
    var textMessage = receivedMessage as TextMessage;
    Assert.NotNull(textMessage);
    Assert.Equal("Hello, I need help", textMessage!.Text);
  }
  
  [Fact]
  public async Task CalculatorTool_Add_ReturnsCorrectResult()
  {
    // Create a mock client directly
    var mockClient = new TestHelpers.MockMcpClient("test-client", "Test Client");
    
    // Prepare arguments for the Add operation
    var arguments = new Dictionary<string, object?>
    {
      { "a", 5.0 },
      { "b", 3.0 }
    };
    
    // Call the tool directly through the mock client
    var response = await mockClient.CallToolAsync("CalculatorTool.Add", arguments);
    
    // Assert
    Assert.NotNull(response);
    Assert.NotNull(response.Content);
    Assert.NotEmpty(response.Content);
    Assert.Equal("text", response.Content[0].Type);
    Assert.NotNull(response.Content[0].Text);
    
    // The result of 5 + 3 should be 8
    var responseText = response.Content[0].Text;
    Assert.Contains("8", responseText);
  }
  
  /// <summary>
  /// Helper method to get text from an IMessage
  /// </summary>
  private static string? GetText(IMessage? message)
  {
    if (message == null) return null;
    
    return message switch
    {
      TextMessage textMessage => textMessage.Text,
      ToolsCallResultMessage toolCallResult => string.Join(Environment.NewLine, 
        toolCallResult.ToolCallResults.Select(tcr => tcr.Result)),
      _ => message.ToString()
    };
  }
}


