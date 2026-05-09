using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;

/// <summary>
///     Streaming agent that maps the unified <see cref="IMessage"/> model onto the OpenAI
///     Responses API's <c>response.create</c> request and consumes the resulting event stream
///     as text/tool-call updates.
/// </summary>
/// <remarks>
///     <para>
///     The agent is intentionally thin: text deltas surface as <see cref="TextUpdateMessage"/>,
///     a finalized text part surfaces as <see cref="TextMessage"/>, and a completed
///     function-call output_item surfaces as <see cref="ToolsCallMessage"/>. Server-side tools
///     (<c>web_search_call</c>, <c>codex.rate_limits</c>) are out of scope and pass through as
///     ignored events; see issue #34 follow-ups.
///     </para>
/// </remarks>
public sealed class OpenAiResponsesAgent : IStreamingAgent, IDisposable
{
    private readonly IOpenAiResponsesClient _client;
    private readonly ILogger<OpenAiResponsesAgent> _logger;

    public OpenAiResponsesAgent(
        string name,
        IOpenAiResponsesClient client,
        ILogger<OpenAiResponsesAgent>? logger = null
    )
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<OpenAiResponsesAgent>.Instance;
    }

    public string Name { get; }

    public void Dispose()
    {
        _client.Dispose();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var stream = await GenerateReplyStreamingAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var aggregateText = new StringBuilder();
        var generationId = Guid.NewGuid().ToString("N");
        var resultMessages = new List<IMessage>();

        await foreach (var update in stream.ConfigureAwait(false))
        {
            switch (update)
            {
                case TextUpdateMessage tu:
                    _ = aggregateText.Append(tu.Text);
                    if (tu.GenerationId is not null)
                    {
                        generationId = tu.GenerationId;
                    }

                    break;
                case TextMessage tm:
                    resultMessages.Add(tm);
                    _ = aggregateText.Clear();
                    break;
                case ToolsCallMessage tc:
                    resultMessages.Add(tc);
                    break;
                default:
                    resultMessages.Add(update);
                    break;
            }
        }

        if (aggregateText.Length > 0)
        {
            resultMessages.Add(
                new TextMessage
                {
                    Text = aggregateText.ToString(),
                    Role = Role.Assistant,
                    FromAgent = Name,
                    GenerationId = generationId,
                }
            );
        }

        return resultMessages;
    }

    /// <inheritdoc />
    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = MessageMapper.BuildRequest(messages, options);
        _logger.LogDebug(
            "OpenAiResponsesAgent.GenerateReplyStreamingAsync model={Model} inputItems={Count} tools={ToolCount}",
            request.Model,
            request.Input.Count,
            request.Tools?.Count ?? 0
        );

        var eventStream = _client.StreamResponseAsync(request, cancellationToken);
        return Task.FromResult(EventStreamToMessages(eventStream, Name, cancellationToken));
    }

    private static async IAsyncEnumerable<IMessage> EventStreamToMessages(
        IAsyncEnumerable<ResponseEvent> events,
        string fromAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var generationId = Guid.NewGuid().ToString("N");
        var pendingFunctionCalls = new Dictionary<string, PendingFunctionCall>(StringComparer.Ordinal);
        var textBuffers = new Dictionary<int, StringBuilder>();

        await foreach (var ev in events.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (ev)
            {
                case ResponseLifecycleEvent lifecycle when lifecycle.Type == ResponseEventTypes.ResponseCreated:
                    if (TryReadString(lifecycle.Response, "id", out var responseId))
                    {
                        generationId = responseId;
                    }

                    break;

                case ResponseOutputItemEvent itemEvent when itemEvent.Type == ResponseEventTypes.OutputItemAdded:
                    if (TryReadString(itemEvent.Item, "type", out var itemType)
                        && itemType == "function_call"
                        && TryReadString(itemEvent.Item, "id", out var fnItemId))
                    {
                        pendingFunctionCalls[fnItemId] = new PendingFunctionCall(
                            ItemId: fnItemId,
                            CallId: TryReadString(itemEvent.Item, "call_id", out var cid) ? cid : null,
                            Name: TryReadString(itemEvent.Item, "name", out var fnName) ? fnName : string.Empty,
                            ArgsBuilder: new StringBuilder(
                                TryReadString(itemEvent.Item, "arguments", out var startArgs) ? startArgs : string.Empty
                            ),
                            OutputIndex: itemEvent.OutputIndex
                        );
                    }

                    break;

                case ResponseOutputTextDeltaEvent deltaEvent:
                    if (!textBuffers.TryGetValue(deltaEvent.OutputIndex, out var textBuf))
                    {
                        textBuf = new StringBuilder();
                        textBuffers[deltaEvent.OutputIndex] = textBuf;
                    }

                    _ = textBuf.Append(deltaEvent.Delta);
                    yield return new TextUpdateMessage
                    {
                        Text = deltaEvent.Delta,
                        Role = Role.Assistant,
                        FromAgent = fromAgent,
                        GenerationId = generationId,
                        MessageOrderIdx = deltaEvent.OutputIndex,
                    };
                    break;

                case ResponseOutputTextDoneEvent doneEvent:
                    var finalText = doneEvent.Text;
                    if (string.IsNullOrEmpty(finalText)
                        && textBuffers.TryGetValue(doneEvent.OutputIndex, out var accumulated))
                    {
                        finalText = accumulated.ToString();
                    }

                    yield return new TextMessage
                    {
                        Text = finalText,
                        Role = Role.Assistant,
                        FromAgent = fromAgent,
                        GenerationId = generationId,
                        MessageOrderIdx = doneEvent.OutputIndex,
                    };
                    _ = textBuffers.Remove(doneEvent.OutputIndex);
                    break;

                case ResponseFunctionCallArgumentsDeltaEvent argsDelta:
                    if (pendingFunctionCalls.TryGetValue(argsDelta.ItemId, out var pending))
                    {
                        _ = pending.ArgsBuilder.Append(argsDelta.Delta);
                    }

                    break;

                case ResponseFunctionCallArgumentsDoneEvent argsDone:
                    if (pendingFunctionCalls.Remove(argsDone.ItemId, out var completed))
                    {
                        var argsJson = string.IsNullOrEmpty(argsDone.Arguments)
                            ? completed.ArgsBuilder.ToString()
                            : argsDone.Arguments;

                        yield return new ToolsCallMessage
                        {
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    FunctionName = completed.Name,
                                    FunctionArgs = argsJson,
                                    ToolCallId = completed.CallId ?? completed.ItemId,
                                    Index = completed.OutputIndex,
                                },
                            ],
                            Role = Role.Assistant,
                            FromAgent = fromAgent,
                            GenerationId = generationId,
                            MessageOrderIdx = completed.OutputIndex,
                        };
                    }

                    break;

                case ResponseLifecycleEvent completedLifecycle
                    when completedLifecycle.Type == ResponseEventTypes.ResponseCompleted:
                    if (TryExtractUsage(completedLifecycle.Response, out var usage))
                    {
                        yield return new UsageMessage
                        {
                            Usage = usage,
                            Role = Role.Assistant,
                            FromAgent = fromAgent,
                            GenerationId = generationId,
                        };
                    }

                    break;

                default:
                    // Unmodeled or pass-through event (response.content_part.*, response.failed,
                    // server-tool events). No native message mapping in v1; the agent silently
                    // skips while consumers can still read the raw stream via IOpenAiResponsesClient.
                    break;
            }
        }
    }

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var prop))
        {
            return false;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryExtractUsage(JsonElement responseElement, out Usage usage)
    {
        usage = new Usage();
        if (responseElement.ValueKind != JsonValueKind.Object
            || !responseElement.TryGetProperty("usage", out var usageEl)
            || usageEl.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var inputTokens = usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.ValueKind == JsonValueKind.Number
            ? inEl.GetInt32()
            : 0;
        var outputTokens =
            usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.ValueKind == JsonValueKind.Number
                ? outEl.GetInt32()
                : 0;
        var totalTokens =
            usageEl.TryGetProperty("total_tokens", out var totEl) && totEl.ValueKind == JsonValueKind.Number
                ? totEl.GetInt32()
                : inputTokens + outputTokens;

        usage = new Usage
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = totalTokens,
        };
        return true;
    }

    private sealed record PendingFunctionCall(
        string ItemId,
        string? CallId,
        string Name,
        StringBuilder ArgsBuilder,
        int OutputIndex
    );
}
