using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Agents;

/// <summary>
///     Agent that demonstrates multi-turn instruction chain testing using TestSseMessageHandler.
///     Example instruction chain format uses special tags with JSON.
/// </summary>
public sealed class InstructionChainAgent : IStreamingAgent
{
    private readonly OpenClientAgent _agent;
    private readonly ILogger<InstructionChainAgent> _logger;

    public InstructionChainAgent(ILogger<InstructionChainAgent> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Create TestSseMessageHandler for instruction chain testing
        var testHandler = new TestSseMessageHandler(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TestSseMessageHandler>()
        )
        {
            WordsPerChunk = 5, // 5 words per SSE chunk
            ChunkDelayMs = 100, // 100ms delay between chunks
        };

        // Create HttpClient with test handler
        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("https://api.test.local") };

        // Create OpenClient with custom HTTP client
        var openClient = new OpenClient(
            httpClient,
            "https://api.test.local/v1",
            null,
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<OpenClient>()
        );

        // Wrap OpenClient with OpenClientAgent
        _agent = new OpenClientAgent(
            "InstructionChainAgent",
            openClient,
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<OpenClientAgent>()
        );

        _logger.LogInformation("InstructionChainAgent initialized with TestSseMessageHandler");
    }

    public string Name => "InstructionChainAgent";

    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var sessionId = Guid.NewGuid().ToString();
        _logger.LogInformation(
            "InstructionChainAgent starting non-streaming execution for session {SessionId}",
            sessionId
        );

        var messagesList = new List<IMessage>();
        await foreach (var msg in StreamResponseAsync(messages, sessionId, options, cancellationToken))
        {
            messagesList.Add(msg);
        }

        _logger.LogInformation("InstructionChainAgent completed execution for session {SessionId}", sessionId);
        return messagesList;
    }

    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var sessionId = Guid.NewGuid().ToString();
        _logger.LogInformation("InstructionChainAgent starting streaming execution for session {SessionId}", sessionId);

        return await Task.FromResult(StreamResponseAsync(messages, sessionId, options, cancellationToken));
    }

    private async IAsyncEnumerable<IMessage> StreamResponseAsync(
        IEnumerable<IMessage> messages,
        string sessionId,
        GenerateReplyOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var messageList = messages.ToList();

        // Check if the latest message contains an instruction chain
        var latestMessage = messageList.LastOrDefault(m => m.Role == Role.User);
        if (latestMessage is TextMessage textMsg)
        {
            var hasInstructionChain = textMsg.Text.Contains("<|instruction_start|>");
            _logger.LogInformation("Message contains instruction chain: {HasChain}", hasInstructionChain);
        }

        // Use OpenClientAgent to call the test API with TestSseMessageHandler
        var streamEnumerable = await _agent.GenerateReplyStreamingAsync(messageList, options, cancellationToken);

        await foreach (var message in streamEnumerable.ConfigureAwait(false))
        {
            _logger.LogDebug("InstructionChainAgent produced message of type {MessageType}", message.GetType().Name);
            yield return message;
        }

        _logger.LogInformation(
            "InstructionChainAgent completed streaming execution for session {SessionId}",
            sessionId
        );
    }
}
