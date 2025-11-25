using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests.TestHelpers;

/// <summary>
///     Simple agent implementation for testing
/// </summary>
public class SimpleTestAgent : IAgent
{
    private readonly List<IMessage> _receivedMessages = [];
    private IMessage? _injectedMessage;

    public static string Id => "test-agent";
    public static string? Name => "Test Agent";
    public static string? Description => "A test agent for MCP middleware";
    public static string? ModelId => "test-model";
    public static Dictionary<string, object>? ModelParameters => null;
    public static IList<IMessage> History => [];

    /// <summary>
    ///     Gets the messages that were received by this agent
    /// </summary>
    public IReadOnlyList<IMessage> ReceivedMessages => _receivedMessages.AsReadOnly();

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // Store the received messages for later inspection
        _receivedMessages.AddRange(messages);

        // Return the injected message, or a default if none was provided
        return _injectedMessage != null
            ? Task.FromResult<IEnumerable<IMessage>>([_injectedMessage])
            : Task.FromResult<IEnumerable<IMessage>>(
                [new TextMessage { Text = "Default response", Role = Role.Assistant }]
            );
    }

    /// <summary>
    ///     Inject a message to be returned by this agent
    /// </summary>
    /// <param name="message">The message to return</param>
    public void InjectMessage(IMessage message)
    {
        _injectedMessage = message;
    }

    /// <summary>
    ///     Inject a tool call message to be returned by this agent
    /// </summary>
    /// <param name="functionName">The function name to call</param>
    /// <param name="args">The arguments to pass to the function</param>
    public void InjectToolCall(string functionName, object args)
    {
        var serializedArgs = JsonSerializer.Serialize(args);
        var toolCall = new ToolCall { FunctionName = functionName, FunctionArgs = serializedArgs };
        _injectedMessage = new ToolsCallMessage { ToolCalls = [toolCall] };
    }
}
