using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
///     Tests for the MCP server functionality
/// </summary>
public class McpServerTests
{
    public static readonly string ServerLocation = Path.Combine(
        Path.GetDirectoryName(typeof(McpServerTests).Assembly.Location)!,
        "AchieveAi.LmDotnetTools.McpSampleServer.exe"
    );

    [Fact]
    public async Task GreetingTool_SayHello_ReturnsGreeting()
    {
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "test-server",
                Command = ServerLocation,
                Arguments = Array.Empty<string>(),
            }
        );

        var client = await McpClient.CreateAsync(transport);
        try
        {
            // Create middleware with the mock client
            var clients = new Dictionary<string, McpClient> { ["test_client"] = client, ["GreetingTool"] = client };

            var middleware = await McpMiddleware.McpMiddleware.CreateAsync(clients);

            // Create a test agent and inject a tool call message to be returned
            var agent = new SimpleTestAgent();
            var toolCall = new ToolCall
            {
                FunctionName = "GreetingTool.SayHello",
                FunctionArgs = JsonSerializer.Serialize(new { name = "User" }),
            };
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
            var firstMessage = response.FirstOrDefault();
            Assert.NotNull(firstMessage);
            var responseText = firstMessage.GetText();
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
            _ = Assert.IsType<TextMessage>(receivedMessage);

            // The message received by the agent should be the initial text message
            var textMessage = receivedMessage as TextMessage;
            Assert.NotNull(textMessage);
            Assert.Equal("Hello, I need help", textMessage!.Text);
        }
        finally
        {
            // Cleanup - Dispose the client to stop the server
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task CalculatorTool_Add_ReturnsCorrectResult()
    {
        // Create a client using the new transport API
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "test-server",
                Command = ServerLocation,
                Arguments = Array.Empty<string>(),
            }
        );

        var client = await McpClient.CreateAsync(transport);
        try
        {
            // Prepare arguments for the Add operation
            var arguments = new Dictionary<string, object?> { { "a", 5.0 }, { "b", 3.0 } };

            var tools = await client.ListToolsAsync();

            // Call the tool directly through the mock client
            var response = await client.CallToolAsync("Add", arguments);

            // Assert
            Assert.NotNull(response);
            Assert.NotNull(response.Content);
            Assert.NotEmpty(response.Content);
            Assert.Equal("text", response.Content[0].Type);
            Assert.NotNull(((TextContentBlock)response.Content[0]).Text);

            // The result of 5 + 3 should be 8
            var responseText = ((TextContentBlock)response.Content[0]).Text;
            Assert.Contains("8", responseText);
        }
        finally
        {
            // Cleanup - Dispose the client to stop the server
            await client.DisposeAsync();
        }
    }
}
