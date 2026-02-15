using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Logging;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.AnthropicProvider.Utils;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RequestResponseDumpWriter = AchieveAi.LmDotnetTools.LmCore.Utils.RequestResponseDumpWriter;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Agents;

/// <summary>
///     Agent that interacts with the Anthropic Claude API.
/// </summary>
public class AnthropicAgent : IStreamingAgent, IDisposable
{
    private static readonly JsonSerializerOptions s_dumpJsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateForProduction();

    private readonly IAnthropicClient _client;
    private readonly ILogger<AnthropicAgent> _logger;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AnthropicAgent" /> class.
    /// </summary>
    /// <param name="name">The name of the agent.</param>
    /// <param name="client">The client to use for API calls.</param>
    /// <param name="logger">Optional logger for the agent.</param>
    public AnthropicAgent(string name, IAnthropicClient client, ILogger<AnthropicAgent>? logger = null)
    {
        Name = name;
        _client = client;
        _logger = logger ?? NullLogger<AnthropicAgent>.Instance;
    }

    /// <summary>
    ///     Gets the name of the agent.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Disposes the client.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var messageList = messages.ToList();
        var modelId = options?.ModelId ?? "claude-3-5-sonnet-20241022";

        _logger.LogInformation(
            LogEventIds.AgentRequestInitiated,
            "API request initiated: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Type={RequestType}",
            modelId,
            Name,
            messageList.Count,
            "Non-streaming"
        );

        try
        {
            var startTime = DateTime.UtcNow;
            var request = AnthropicRequest.FromMessages(messages, options);
            var dumpWriter = RequestResponseDumpWriter.Create(options?.RequestResponseDumpFileName, s_dumpJsonOptions, _logger);
            dumpWriter?.WriteRequest(request);

            _logger.LogDebug(
                LogEventIds.RequestConversion,
                "Request converted: Model={Model}, MaxTokens={MaxTokens}, Temperature={Temperature}, SystemPrompt={HasSystemPrompt}",
                request.Model,
                request.MaxTokens,
                request.Temperature,
                !string.IsNullOrEmpty(request.System)
            );

            var response = await _client.CreateChatCompletionsAsync(request, cancellationToken);
            dumpWriter?.WriteResponse(response);

            var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var promptTokens = response.Usage?.InputTokens ?? 0;
            var completionTokens = response.Usage?.OutputTokens ?? 0;

            _logger.LogInformation(
                LogEventIds.AgentRequestCompleted,
                "API request completed: Model={Model}, Agent={AgentName}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, Duration={Duration}ms",
                modelId,
                Name,
                promptTokens,
                completionTokens,
                duration
            );

            // Convert to messages using the Models namespace extension
            var resultMessages = response.ToMessages(Name).Select(m => m.WithIds(options)).ToList();

            _logger.LogDebug(
                LogEventIds.MessageTransformation,
                "Messages transformed: Agent={AgentName}, ResponseMessageCount={MessageCount}, ResponseId={ResponseId}",
                Name,
                resultMessages.Count,
                response.Id
            );

            return resultMessages;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                LogEventIds.ApiCallFailed,
                ex,
                "API call failed: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Error={Error}",
                modelId,
                Name,
                messageList.Count,
                ex.Message
            );
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var messageList = messages.ToList();
        var modelId = options?.ModelId ?? "claude-3-5-sonnet-20241022";

        _logger.LogInformation(
            LogEventIds.AgentRequestInitiated,
            "API request initiated: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Type={RequestType}",
            modelId,
            Name,
            messageList.Count,
            "Streaming"
        );

        try
        {
            var request = AnthropicRequest.FromMessages(messages, options) with { Stream = true };
            var dumpWriter = RequestResponseDumpWriter.Create(options?.RequestResponseDumpFileName, s_dumpJsonOptions, _logger);
            dumpWriter?.WriteRequest(request);

            _logger.LogDebug(
                LogEventIds.RequestConversion,
                "Streaming request converted: Model={Model}, MaxTokens={MaxTokens}, Temperature={Temperature}, SystemPrompt={HasSystemPrompt}",
                request.Model,
                request.MaxTokens,
                request.Temperature,
                !string.IsNullOrEmpty(request.System)
            );

            // Return the streaming response as an IAsyncEnumerable
            return await Task.FromResult(GenerateStreamingMessages(request, options, dumpWriter, cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                LogEventIds.ApiCallFailed,
                ex,
                "Streaming API call failed: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Error={Error}",
                modelId,
                Name,
                messageList.Count,
                ex.Message
            );
            throw;
        }
    }

    private async IAsyncEnumerable<IMessage> GenerateStreamingMessages(
        AnthropicRequest request,
        GenerateReplyOptions? options,
        RequestResponseDumpWriter? dumpWriter,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var startTime = DateTime.UtcNow;
        var chunkCount = 0;
        var modelId = request.Model ?? "claude-3-5-sonnet-20241022";

        // Create a parser to track state across events
        var parser = new AnthropicStreamParser(_logger);

        IAsyncEnumerable<AnthropicStreamEvent> streamEvents;
        try
        {
            streamEvents = await _client.StreamingChatCompletionsAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                LogEventIds.StreamingError,
                ex,
                "Streaming initialization error: Model={Model}, Agent={AgentName}, Error={Error}",
                modelId,
                Name,
                ex.Message
            );
            throw;
        }

        await foreach (var streamEvent in streamEvents)
        {
            dumpWriter?.AppendResponseChunk(streamEvent);
            IEnumerable<IMessage> messages;
            try
            {
                chunkCount++;

                _logger.LogDebug(
                    LogEventIds.StreamingEventProcessed,
                    "Streaming event processed: Agent={AgentName}, ChunkNumber={ChunkNumber}, EventType={EventType}",
                    Name,
                    chunkCount,
                    streamEvent.GetType().Name
                );

                // Process the event directly without serialization/deserialization
                messages = parser.ProcessStreamEvent(streamEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    LogEventIds.ParserFailure,
                    ex,
                    "Parser failure during streaming: Agent={AgentName}, ChunkNumber={ChunkNumber}, Error={Error}",
                    Name,
                    chunkCount,
                    ex.Message
                );
                throw;
            }

            foreach (var message in messages)
            {
                _logger.LogDebug(
                    LogEventIds.MessageTransformation,
                    "Message transformed: Agent={AgentName}, MessageType={MessageType}",
                    Name,
                    message.GetType().Name
                );

                // Set the agent name for all messages
                if (message is TextMessage textMessage)
                {
                    // yield return textMessage with { FromAgent = Name };
                }
                else if (message is TextUpdateMessage textUpdateMessage)
                {
                    yield return (textUpdateMessage with { FromAgent = Name }).WithIds(options);
                }
                else if (message is ToolsCallUpdateMessage toolsCallMessage)
                {
                    yield return (toolsCallMessage with { FromAgent = Name }).WithIds(options);
                }
                else if (message is ToolsCallMessage) { }
                else
                {
                    yield return message.WithIds(options);
                }
            }
        }

        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

        _logger.LogInformation(
            LogEventIds.AgentStreamingCompleted,
            "Streaming completed: Model={Model}, Agent={AgentName}, ChunkCount={ChunkCount}, Duration={Duration}ms",
            modelId,
            Name,
            chunkCount,
            duration
        );
    }

    /// <summary>
    ///     Disposes the client.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_client is IDisposable disposableClient)
                {
                    try
                    {
                        disposableClient.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            LogEventIds.ClientDisposalError,
                            ex,
                            "Error disposing client: Agent={AgentName}, Error={Error}",
                            Name,
                            ex.Message
                        );
                    }
                }
            }

            _disposed = true;
        }
    }
}
