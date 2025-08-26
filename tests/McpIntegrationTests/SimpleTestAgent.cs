using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;

/// <summary>
/// Simple agent implementation for testing
/// </summary>
public class SimpleTestAgent : IAgent
{
    private IMessage? _injectedMessage = null;
    private List<IMessage> _receivedMessages = new();

    public string Id => "test-agent";
    public string? Name => "Test Agent";
    public string? Description => "A test agent for MCP middleware";
    public string? ModelId => "test-model";
    public Dictionary<string, object>? ModelParameters => null;
    public IList<IMessage> History => new List<IMessage>();

    /// <summary>
    /// Gets the messages that were received by this agent
    /// </summary>
    public IReadOnlyList<IMessage> ReceivedMessages => _receivedMessages.AsReadOnly();

    /// <summary>
    /// Inject a message to be returned by this agent
    /// </summary>
    /// <param name="message">The message to return</param>
    public void InjectMessage(IMessage message)
    {
        _injectedMessage = message;
    }

    /// <summary>
    /// Inject a tool call message to be returned by this agent
    /// </summary>
    /// <param name="functionName">The function name to call</param>
    /// <param name="args">The arguments to pass to the function</param>
    public void InjectToolCall(string functionName, object args)
    {
        var serializedArgs = JsonSerializer.Serialize(args);
        var toolCall = new ToolCall(functionName, serializedArgs);
        _injectedMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // Store the received messages for later inspection
        _receivedMessages.AddRange(messages);

        // Return the injected message, or a default if none was provided
        if (_injectedMessage != null)
        {
            return Task.FromResult<IEnumerable<IMessage>>(new[] { _injectedMessage });
        }
        else
        {
            return Task.FromResult<IEnumerable<IMessage>>(
                new[]
                {
                    new TextMessage { Text = "Default response", Role = Role.Assistant },
                }
            );
        }
    }
}
