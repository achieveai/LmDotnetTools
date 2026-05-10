using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     Pure converter from <see cref="InstructionPlan"/> into the ordered sequence of
///     <see cref="ResponseEvent"/> records that the OpenAI Responses API emits to a client.
/// </summary>
/// <remarks>
///     <para>
///     The writer is transport-agnostic: HTTP+SSE wraps each event in a <c>data: …\n\n</c>
///     envelope; WebSocket emits the same JSON object as a single text frame. Sharing the
///     emitter keeps the two transports byte-equivalent at the event level — a property the
///     ResponsesByteIdentity tests rely on.
///     </para>
///     <para>
///     v1 supports reasoning items, plain text streaming, and function-call streaming. Server-side tools
///     (<c>web_search_call</c>, <c>codex.rate_limits</c>) are out of scope and produce no events;
///     the InstructionPlan items for those flow through unchanged so future expansion is local
///     to this writer.
///     </para>
/// </remarks>
public static class OpenAiResponsesEventStreamWriter
{
    private const int DefaultWordsPerChunk = 5;

    /// <summary>
    ///     Builds the ordered event list for <paramref name="plan"/>. The first event is always
    ///     <c>response.created</c>; the last is always <c>response.completed</c>. Intermediate
    ///     events follow the wire grammar: per output_item — <c>output_item.added</c> →
    ///     (<c>content_part.added</c> → deltas → <c>content_part.done</c>) | (function arg deltas
    ///     → <c>function_call_arguments.done</c>) → <c>output_item.done</c>.
    /// </summary>
    /// <param name="plan">The instruction plan to render.</param>
    /// <param name="model">Model name to embed in lifecycle <c>response</c> objects.</param>
    /// <param name="wordsPerChunk">Soft cap on how many words go into a single text delta.</param>
    /// <param name="responseId">Optional fixed response ID; generated when null.</param>
    public static IReadOnlyList<ResponseEvent> Write(
        InstructionPlan plan,
        string? model = null,
        int wordsPerChunk = DefaultWordsPerChunk,
        string? responseId = null
    )
    {
        ArgumentNullException.ThrowIfNull(plan);

        var effectiveResponseId = responseId ?? $"resp_{Guid.NewGuid():N}";
        var effectiveModel = string.IsNullOrEmpty(model) ? "gpt-mock-responses" : model;
        var effectiveChunkSize = Math.Max(1, wordsPerChunk);

        var events = new List<ResponseEvent>();
        var seq = 0;

        events.Add(
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCreated,
                SequenceNumber = seq++,
                Response = BuildResponseSnapshot(
                    effectiveResponseId,
                    effectiveModel,
                    status: "in_progress",
                    usage: null
                ),
            }
        );

        events.Add(
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseInProgress,
                SequenceNumber = seq++,
                Response = BuildResponseSnapshot(
                    effectiveResponseId,
                    effectiveModel,
                    status: "in_progress",
                    usage: null
                ),
            }
        );

        var outputIndex = 0;
        if (plan.ReasoningLength is int reasoningLength && reasoningLength > 0)
        {
            var reasoningText = string.Join(' ', GenerateLoremWords(reasoningLength));
            AppendReasoningOutput(events, ref seq, outputIndex, reasoningText);
            outputIndex++;
        }

        foreach (var msg in plan.Messages)
        {
            if (msg.ToolCalls is { Count: > 0 } toolCalls)
            {
                foreach (var call in toolCalls)
                {
                    AppendFunctionCall(events, ref seq, outputIndex, call, effectiveChunkSize);
                    outputIndex++;
                }

                continue;
            }

            if (msg.TextLength is int textLen && textLen > 0)
            {
                var text = string.Join(' ', GenerateLoremWords(textLen));
                AppendTextOutput(events, ref seq, outputIndex, text, effectiveChunkSize);
                outputIndex++;
                continue;
            }

            if (!string.IsNullOrEmpty(msg.ExplicitText))
            {
                AppendTextOutput(events, ref seq, outputIndex, msg.ExplicitText, effectiveChunkSize);
                outputIndex++;
            }
        }

        events.Add(
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCompleted,
                SequenceNumber = seq++,
                Response = BuildResponseSnapshot(
                    effectiveResponseId,
                    effectiveModel,
                    status: "completed",
                    usage: new UsageSnapshot(InputTokens: 100, OutputTokens: 50)
                ),
            }
        );

        return events;
    }

    private static void AppendReasoningOutput(
        List<ResponseEvent> events,
        ref int seq,
        int outputIndex,
        string reasoningText
    )
    {
        var itemId = $"rs_{Guid.NewGuid():N}";
        var itemJson = new JsonObject
        {
            ["id"] = itemId,
            ["type"] = "reasoning",
            ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = reasoningText }),
        };

        events.Add(
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                SequenceNumber = seq++,
                OutputIndex = outputIndex,
                Item = ToJsonElement(itemJson),
            }
        );

        events.Add(
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                SequenceNumber = seq++,
                OutputIndex = outputIndex,
                Item = ToJsonElement(itemJson),
            }
        );
    }

    private static void AppendTextOutput(
        List<ResponseEvent> events,
        ref int seq,
        int outputIndex,
        string text,
        int wordsPerChunk
    )
    {
        var itemId = $"msg_{Guid.NewGuid():N}";
        var contentIndex = 0;

        var itemAddedJson = new JsonObject
        {
            ["id"] = itemId,
            ["type"] = "message",
            ["status"] = "in_progress",
            ["role"] = "assistant",
            ["content"] = new JsonArray(),
        };

        events.Add(
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                SequenceNumber = seq++,
                OutputIndex = outputIndex,
                Item = ToJsonElement(itemAddedJson),
            }
        );

        var partAddedJson = new JsonObject { ["type"] = "output_text", ["text"] = string.Empty };

        events.Add(
            new ResponseContentPartEvent
            {
                Type = ResponseEventTypes.ContentPartAdded,
                SequenceNumber = seq++,
                ItemId = itemId,
                OutputIndex = outputIndex,
                ContentIndex = contentIndex,
                Part = ToJsonElement(partAddedJson),
            }
        );

        foreach (var chunk in ChunkWords(text, wordsPerChunk))
        {
            events.Add(
                new ResponseOutputTextDeltaEvent
                {
                    Type = ResponseEventTypes.OutputTextDelta,
                    SequenceNumber = seq++,
                    ItemId = itemId,
                    OutputIndex = outputIndex,
                    ContentIndex = contentIndex,
                    Delta = chunk,
                }
            );
        }

        events.Add(
            new ResponseOutputTextDoneEvent
            {
                Type = ResponseEventTypes.OutputTextDone,
                SequenceNumber = seq++,
                ItemId = itemId,
                OutputIndex = outputIndex,
                ContentIndex = contentIndex,
                Text = text,
            }
        );

        var partDoneJson = new JsonObject { ["type"] = "output_text", ["text"] = text };

        events.Add(
            new ResponseContentPartEvent
            {
                Type = ResponseEventTypes.ContentPartDone,
                SequenceNumber = seq++,
                ItemId = itemId,
                OutputIndex = outputIndex,
                ContentIndex = contentIndex,
                Part = ToJsonElement(partDoneJson),
            }
        );

        var itemDoneJson = new JsonObject
        {
            ["id"] = itemId,
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }),
        };

        events.Add(
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                SequenceNumber = seq++,
                OutputIndex = outputIndex,
                Item = ToJsonElement(itemDoneJson),
            }
        );
    }

    private static void AppendFunctionCall(
        List<ResponseEvent> events,
        ref int seq,
        int outputIndex,
        InstructionToolCall call,
        int wordsPerChunk
    )
    {
        var itemId = $"fc_{Guid.NewGuid():N}";
        var callId = $"call_{Guid.NewGuid():N}";
        var argsJson = string.IsNullOrEmpty(call.ArgsJson) ? "{}" : call.ArgsJson;

        var itemAddedJson = new JsonObject
        {
            ["id"] = itemId,
            ["type"] = "function_call",
            ["status"] = "in_progress",
            ["call_id"] = callId,
            ["name"] = call.Name,
            ["arguments"] = string.Empty,
        };

        events.Add(
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                SequenceNumber = seq++,
                OutputIndex = outputIndex,
                Item = ToJsonElement(itemAddedJson),
            }
        );

        // Stream the JSON arguments in roughly word-chunk-sized slices so the wire shape mirrors
        // what real GPT-5/o-series responses look like. Word count is a poor unit for JSON; we
        // approximate with `wordsPerChunk * 5` characters which lands near a typical JSON token.
        var sliceSize = Math.Max(1, wordsPerChunk * 5);
        for (var i = 0; i < argsJson.Length; i += sliceSize)
        {
            var chunkLen = Math.Min(sliceSize, argsJson.Length - i);
            var chunk = argsJson.Substring(i, chunkLen);
            events.Add(
                new ResponseFunctionCallArgumentsDeltaEvent
                {
                    Type = ResponseEventTypes.FunctionCallArgumentsDelta,
                    SequenceNumber = seq++,
                    ItemId = itemId,
                    OutputIndex = outputIndex,
                    Delta = chunk,
                }
            );
        }

        events.Add(
            new ResponseFunctionCallArgumentsDoneEvent
            {
                Type = ResponseEventTypes.FunctionCallArgumentsDone,
                SequenceNumber = seq++,
                ItemId = itemId,
                OutputIndex = outputIndex,
                Arguments = argsJson,
            }
        );

        var itemDoneJson = new JsonObject
        {
            ["id"] = itemId,
            ["type"] = "function_call",
            ["status"] = "completed",
            ["call_id"] = callId,
            ["name"] = call.Name,
            ["arguments"] = argsJson,
        };

        events.Add(
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                SequenceNumber = seq++,
                OutputIndex = outputIndex,
                Item = ToJsonElement(itemDoneJson),
            }
        );
    }

    private static JsonElement BuildResponseSnapshot(
        string responseId,
        string model,
        string status,
        UsageSnapshot? usage
    )
    {
        var node = new JsonObject
        {
            ["id"] = responseId,
            ["object"] = "response",
            ["status"] = status,
            ["model"] = model,
            ["output"] = new JsonArray(),
        };

        if (usage is { } u)
        {
            node["usage"] = new JsonObject
            {
                ["input_tokens"] = u.InputTokens,
                ["output_tokens"] = u.OutputTokens,
                ["total_tokens"] = u.InputTokens + u.OutputTokens,
            };
        }

        return ToJsonElement(node);
    }

    private static JsonElement ToJsonElement(JsonNode node)
    {
        return JsonSerializer.SerializeToElement(node);
    }

    private static IEnumerable<string> ChunkWords(string text, int wordsPerChunk)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i += wordsPerChunk)
        {
            var take = Math.Min(wordsPerChunk, words.Length - i);
            var slice = string.Join(' ', words, i, take);
            // Preserve inter-chunk whitespace so deltas concat back to the original text.
            yield return i == 0 ? slice : ' ' + slice;
        }
    }

    private static IEnumerable<string> GenerateLoremWords(int wordCount)
    {
        var lorem = (
            "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor "
            + "incididunt ut labore et dolore magna aliqua ut enim ad minim veniam quis nostrud "
            + "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat duis aute irure "
            + "dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur "
            + "excepteur sint occaecat cupidatat non proident sunt in culpa qui officia deserunt mollit anim id est laborum"
        ).Split(' ');

        for (var i = 0; i < wordCount; i++)
        {
            yield return lorem[i % lorem.Length];
        }
    }

    private readonly record struct UsageSnapshot(int InputTokens, int OutputTokens);
}
