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
        var generationId = string.IsNullOrEmpty(options?.GenerationId)
            ? Guid.NewGuid().ToString("N")
            : options.GenerationId;
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
            "OpenAiResponsesAgent.GenerateReplyStreamingAsync model={Model} inputItems={Count} inputTypes=[{InputTypes}] tools={ToolCount}",
            request.Model,
            request.Input.Count,
            string.Join(",", request.Input.Select(i => i.Type)),
            request.Tools?.Count ?? 0
        );

        var eventStream = _client.StreamResponseAsync(request, cancellationToken);
        return Task.FromResult(EventStreamToMessages(eventStream, Name, options?.GenerationId, cancellationToken));
    }

    private static async IAsyncEnumerable<IMessage> EventStreamToMessages(
        IAsyncEnumerable<ResponseEvent> events,
        string fromAgent,
        string? runGenerationId,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        // Prefer the run's GenerationId so every message emitted in a run shares one id — the client
        // merge key is kind-runId-generationId-messageOrderIdx, and the provider's opaque per-response
        // id never matches the run, which breaks tool-call grouping (the pillbox bug). Fall back to a
        // synthetic id only when the run advertises none.
        var generationId = string.IsNullOrEmpty(runGenerationId)
            ? Guid.NewGuid().ToString("N")
            : runGenerationId;
        var pendingFunctionCalls = new Dictionary<string, PendingFunctionCall>(StringComparer.Ordinal);
        var textBuffers = new Dictionary<int, StringBuilder>();
        // Tool calls already surfaced, keyed by call_id, so the delta-correlated path and the
        // output_item.done fallback don't double-emit the same call.
        var emittedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var ev in events.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (ev)
            {
                case ResponseLifecycleEvent lifecycle when lifecycle.Type == ResponseEventTypes.ResponseCreated:
                    // Only adopt the provider's opaque response id when the run advertised no
                    // GenerationId; otherwise the run's id must win so messages stay groupable.
                    if (
                        string.IsNullOrEmpty(runGenerationId)
                        && TryReadString(lifecycle.Response, "id", out var responseId)
                    )
                    {
                        generationId = responseId;
                    }

                    break;

                case ResponseOutputItemEvent itemEvent when itemEvent.Type == ResponseEventTypes.OutputItemAdded:
                    if (
                        TryReadString(itemEvent.Item, "type", out var itemType)
                        && itemType == "function_call"
                        && TryReadString(itemEvent.Item, "id", out var fnItemId)
                    )
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

                case ResponseOutputItemEvent itemEvent
                    when itemEvent.Type == ResponseEventTypes.OutputItemDone
                        && TryReadString(itemEvent.Item, "type", out var reasoningItemType)
                        && reasoningItemType == "reasoning":
                    if (TryExtractReasoning(itemEvent.Item, out var reasoning, out var visibility))
                    {
                        yield return new ReasoningMessage
                        {
                            Reasoning = reasoning,
                            Visibility = visibility,
                            Role = Role.Assistant,
                            FromAgent = fromAgent,
                            GenerationId = generationId,
                            MessageOrderIdx = itemEvent.OutputIndex,
                        };
                    }

                    break;

                case ResponseReasoningSummaryTextDeltaEvent reasoningDelta:
                    // Reasoning summary streams as its own event channel (the reasoning output_item's
                    // summary array stays empty until done) — surface each delta as a reasoning update.
                    yield return new ReasoningUpdateMessage
                    {
                        Reasoning = reasoningDelta.Delta,
                        Visibility = ReasoningVisibility.Plain,
                        Role = Role.Assistant,
                        FromAgent = fromAgent,
                        GenerationId = generationId,
                        MessageOrderIdx = reasoningDelta.OutputIndex,
                    };

                    break;

                case ResponseReasoningSummaryTextDoneEvent reasoningDone:
                    yield return new ReasoningMessage
                    {
                        Reasoning = reasoningDone.Text,
                        Visibility = ReasoningVisibility.Plain,
                        Role = Role.Assistant,
                        FromAgent = fromAgent,
                        GenerationId = generationId,
                        MessageOrderIdx = reasoningDone.OutputIndex,
                    };

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
                    if (
                        string.IsNullOrEmpty(finalText)
                        && textBuffers.TryGetValue(doneEvent.OutputIndex, out var accumulated)
                    )
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
                        var callId = completed.CallId ?? completed.ItemId;
                        _ = emittedToolCallIds.Add(callId);

                        yield return new ToolsCallMessage
                        {
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    FunctionName = completed.Name,
                                    FunctionArgs = argsJson,
                                    ToolCallId = callId,
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

                // Fallback / primary path for backends that rotate the per-event item_id so the
                // delta-correlated path above can't match. The terminal
                // output_item.done carries the complete function_call item (name, call_id, arguments),
                // which is all we need. Deduped by call_id against the delta path.
                case ResponseOutputItemEvent fnDone
                    when fnDone.Type == ResponseEventTypes.OutputItemDone
                        && TryReadString(fnDone.Item, "type", out var fnDoneType)
                        && fnDoneType == "function_call":
                    var doneCallId = TryReadString(fnDone.Item, "call_id", out var doneCid)
                        ? doneCid
                        : TryReadString(fnDone.Item, "id", out var doneIid)
                            ? doneIid
                            : null;
                    if (doneCallId is not null && emittedToolCallIds.Add(doneCallId))
                    {
                        yield return new ToolsCallMessage
                        {
                            ToolCalls =
                            [
                                new ToolCall
                                {
                                    FunctionName = TryReadString(fnDone.Item, "name", out var doneName) ? doneName : string.Empty,
                                    FunctionArgs = TryReadString(fnDone.Item, "arguments", out var doneArgs) ? doneArgs : "{}",
                                    ToolCallId = doneCallId,
                                    Index = fnDone.OutputIndex,
                                },
                            ],
                            Role = Role.Assistant,
                            FromAgent = fromAgent,
                            GenerationId = generationId,
                            MessageOrderIdx = fnDone.OutputIndex,
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

    private static bool TryExtractReasoning(JsonElement item, out string reasoning, out ReasoningVisibility visibility)
    {
        reasoning = string.Empty;
        visibility = ReasoningVisibility.Plain;

        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var summaryItem in summary.EnumerateArray())
            {
                if (TryReadString(summaryItem, "text", out var text) && !string.IsNullOrEmpty(text))
                {
                    if (builder.Length > 0)
                    {
                        _ = builder.AppendLine();
                    }

                    _ = builder.Append(text);
                }
            }

            if (builder.Length > 0)
            {
                reasoning = builder.ToString();
                return true;
            }
        }

        if (
            TryReadString(item, "encrypted_content", out var encryptedContent)
            && !string.IsNullOrEmpty(encryptedContent)
        )
        {
            reasoning = encryptedContent;
            visibility = ReasoningVisibility.Encrypted;
            return true;
        }

        return false;
    }

    private static bool TryExtractUsage(JsonElement responseElement, out Usage usage)
    {
        usage = new Usage();
        if (
            responseElement.ValueKind != JsonValueKind.Object
            || !responseElement.TryGetProperty("usage", out var usageEl)
            || usageEl.ValueKind != JsonValueKind.Object
        )
        {
            return false;
        }

        var inputTokens =
            usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.ValueKind == JsonValueKind.Number
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
