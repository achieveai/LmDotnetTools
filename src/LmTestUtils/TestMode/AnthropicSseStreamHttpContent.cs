using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     SSE stream HTTP content generator for Anthropic API format.
///     Generates proper Anthropic SSE events including server_tool_use and server_tool_result.
/// </summary>
public sealed class AnthropicSseStreamHttpContent : HttpContent
{
    private const int DefaultWordsPerChunk = 5;
    private const int MinWordsPerChunk = 1;
    private const int MinChunkDelayMs = 0;

    private readonly int _chunkDelayMs;
    private readonly InstructionPlan? _instructionPlan;
    private readonly string? _model;
    private readonly int _wordsPerChunk;

    public AnthropicSseStreamHttpContent(
        InstructionPlan instructionPlan,
        string? model = null,
        int wordsPerChunk = 5,
        int chunkDelayMs = 100
    )
    {
        _instructionPlan = instructionPlan;
        _model = model ?? "claude-sonnet-4-5-20250929";
        _wordsPerChunk = Math.Max(MinWordsPerChunk, wordsPerChunk);
        _chunkDelayMs = Math.Max(MinChunkDelayMs, chunkDelayMs);

        Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        return SerializeCoreAsync(stream, CancellationToken.None);
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AnthropicSseStreamHttpContent>();

        _ = Task.Run(
            async () =>
            {
                Exception? error = null;
                try
                {
                    using var writerStream = pipe.Writer.AsStream(false);
                    await SerializeCoreAsync(writerStream, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogDebug("Stream creation cancelled");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during Anthropic SSE stream creation");
                    error = ex;
                }
                finally
                {
                    await pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
                }
            },
            CancellationToken.None
        );

        return pipe.Reader.AsStream(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        return Task.FromResult(CreateContentReadStream(CancellationToken.None));
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(CreateContentReadStream(cancellationToken));
    }

    private async Task SerializeCoreAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = false };

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var messageId = $"msg_{Guid.NewGuid():N}";

        async Task WriteSseEventAsync(string eventType, object payload)
        {
            var json = JsonSerializer.Serialize(payload);

            await writer.WriteAsync($"event: {eventType}\n");
            await writer.WriteAsync($"data: {json}\n\n");
            await writer.FlushAsync(cancellationToken);

            if (_chunkDelayMs > 0)
            {
                await Task.Delay(_chunkDelayMs, cancellationToken);
            }
        }

        // Write message_start event (includes cache metrics from InstructionPlan)
        await WriteSseEventAsync(
            "message_start",
            new
            {
                type = "message_start",
                message = new
                {
                    id = messageId,
                    type = "message",
                    role = "assistant",
                    model = _model,
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                    usage = new
                    {
                        input_tokens = 100,
                        output_tokens = 0,
                        cache_creation_input_tokens = _instructionPlan?.CacheCreationInputTokens ?? 0,
                        cache_read_input_tokens = _instructionPlan?.CacheReadInputTokens ?? 0,
                    },
                },
            }
        );

        if (_instructionPlan != null)
        {
            var contentIndex = 0;
            var serverToolUseIds = new Dictionary<string, string>();

            // Emit thinking block first if reasoning is configured
            if (_instructionPlan.ReasoningLength is int rlen && rlen > 0)
            {
                var thinkingText = string.Join(" ", GenerateLoremChunks(rlen, _wordsPerChunk));
                await WriteThinkingEventsAsync(writer, thinkingText, contentIndex, cancellationToken);
                contentIndex++;
            }

            foreach (var message in _instructionPlan.Messages)
            {
                if (message.ServerToolUse != null)
                {
                    await WriteServerToolUseEventsAsync(
                        writer,
                        message.ServerToolUse,
                        contentIndex,
                        serverToolUseIds,
                        cancellationToken
                    );
                    contentIndex++;
                }
                else if (message.ServerToolResult != null)
                {
                    await WriteServerToolResultEventsAsync(
                        writer,
                        message.ServerToolResult,
                        contentIndex,
                        serverToolUseIds,
                        cancellationToken
                    );
                    contentIndex++;
                }
                else if (message.TextWithCitations != null)
                {
                    await WriteTextWithCitationsEventsAsync(
                        writer,
                        message.TextWithCitations,
                        contentIndex,
                        cancellationToken
                    );
                    contentIndex++;
                }
                else if (message.TextLength is int len && len > 0)
                {
                    var text = string.Join(" ", GenerateLoremChunks(len, _wordsPerChunk));
                    await WriteTextEventsAsync(writer, text, contentIndex, cancellationToken);
                    contentIndex++;
                }
                else if (!string.IsNullOrEmpty(message.ExplicitText))
                {
                    await WriteTextEventsAsync(writer, message.ExplicitText!, contentIndex, cancellationToken);
                    contentIndex++;
                }
                else if (message.ToolCalls != null)
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        await WriteToolUseEventsAsync(writer, toolCall, contentIndex, cancellationToken);
                        contentIndex++;
                    }
                }
            }
        }

        // Write message_delta event with usage
        await WriteSseEventAsync(
            "message_delta",
            new
            {
                type = "message_delta",
                delta = new { stop_reason = "end_turn", stop_sequence = (string?)null },
                usage = new { input_tokens = 100, output_tokens = 50 },
            }
        );

        // Write message_stop event
        await WriteSseEventAsync("message_stop", new { type = "message_stop" });
    }

    private async Task WriteServerToolUseEventsAsync(
        StreamWriter writer,
        InstructionServerToolUse toolUse,
        int contentIndex,
        Dictionary<string, string> toolUseIds,
        CancellationToken ct
    )
    {
        var id = toolUse.Id ?? $"srvtoolu_{Guid.NewGuid():N}";
        toolUseIds[toolUse.Name] = id;

        // content_block_start
        var startEvent = new
        {
            type = "content_block_start",
            index = contentIndex,
            content_block = new
            {
                type = "server_tool_use",
                id,
                name = toolUse.Name,
                input = new { },
            },
        };

        await writer.WriteAsync("event: content_block_start\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(startEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);

        // input_json_delta events if there's input
        if (toolUse.Input.HasValue && toolUse.Input.Value.ValueKind != JsonValueKind.Undefined)
        {
            var inputJson = toolUse.Input.Value.GetRawText();

            // Stream the input JSON in chunks
            for (var i = 0; i < inputJson.Length; i += _wordsPerChunk * 5)
            {
                var chunkLen = Math.Min(_wordsPerChunk * 5, inputJson.Length - i);
                var chunk = inputJson.Substring(i, chunkLen);

                var deltaEvent = new
                {
                    type = "content_block_delta",
                    index = contentIndex,
                    delta = new { type = "input_json_delta", partial_json = chunk },
                };

                await writer.WriteAsync("event: content_block_delta\n");
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(deltaEvent)}\n\n");
                await writer.FlushAsync(ct);
                await DelayAsync(ct);
            }
        }

        // content_block_stop
        var stopEvent = new { type = "content_block_stop", index = contentIndex };

        await writer.WriteAsync("event: content_block_stop\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(stopEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);
    }

    private async Task WriteServerToolResultEventsAsync(
        StreamWriter writer,
        InstructionServerToolResult result,
        int contentIndex,
        Dictionary<string, string> toolUseIds,
        CancellationToken ct
    )
    {
        var resultType = result.Name switch
        {
            "web_search" => "web_search_tool_result",
            "web_fetch" => "web_fetch_tool_result",
            "bash_code_execution" => "bash_code_execution_tool_result",
            "text_editor_code_execution" => "text_editor_code_execution_tool_result",
            _ => $"{result.Name}_tool_result",
        };

        var toolUseId = result.ToolUseId ?? (toolUseIds.TryGetValue(result.Name, out var id) ? id : $"srvtoolu_{Guid.NewGuid():N}");

        object content;
        if (result.ErrorCode != null)
        {
            content = new { type = $"{resultType}_error", error_code = result.ErrorCode };
        }
        else if (result.Result.HasValue)
        {
            content = JsonSerializer.Deserialize<object>(result.Result.Value.GetRawText()) ?? new { };
        }
        else
        {
            content = new { };
        }

        var startEvent = new
        {
            type = "content_block_start",
            index = contentIndex,
            content_block = new
            {
                type = resultType,
                tool_use_id = toolUseId,
                content,
            },
        };

        await writer.WriteAsync("event: content_block_start\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(startEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);

        // content_block_stop
        var stopEvent = new { type = "content_block_stop", index = contentIndex };

        await writer.WriteAsync("event: content_block_stop\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(stopEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);
    }

    private async Task WriteTextWithCitationsEventsAsync(
        StreamWriter writer,
        InstructionTextWithCitations textWithCitations,
        int contentIndex,
        CancellationToken ct
    )
    {
        var text = textWithCitations.Text
            ?? (
                textWithCitations.Length is int len
                    ? string.Join(" ", GenerateLoremChunks(len, _wordsPerChunk))
                    : string.Empty
            );

        object? citations = null;
        if (textWithCitations.Citations != null && textWithCitations.Citations.Count > 0)
        {
            citations = textWithCitations.Citations
                .Select(c => new
                {
                    type = c.Type,
                    url = c.Url,
                    title = c.Title,
                    cited_text = c.CitedText,
                })
                .ToList();
        }

        // content_block_start with citations
        var startEvent = citations != null
            ? new
            {
                type = "content_block_start",
                index = contentIndex,
                content_block = new { type = "text", text = "", citations },
            }
            : (object)
                new
                {
                    type = "content_block_start",
                    index = contentIndex,
                    content_block = new { type = "text", text = "" },
                };

        await writer.WriteAsync("event: content_block_start\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(startEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);

        // text_delta events
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i += _wordsPerChunk)
        {
            var chunk = string.Join(' ', words.Skip(i).Take(Math.Min(_wordsPerChunk, words.Length - i)));
            if (i > 0)
            {
                chunk = " " + chunk;
            }

            var deltaEvent = new
            {
                type = "content_block_delta",
                index = contentIndex,
                delta = new { type = "text_delta", text = chunk },
            };

            await writer.WriteAsync("event: content_block_delta\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(deltaEvent)}\n\n");
            await writer.FlushAsync(ct);
            await DelayAsync(ct);
        }

        // content_block_stop
        var stopEvent = new { type = "content_block_stop", index = contentIndex };

        await writer.WriteAsync("event: content_block_stop\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(stopEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);
    }

    private async Task WriteTextEventsAsync(StreamWriter writer, string text, int contentIndex, CancellationToken ct)
    {
        // content_block_start
        var startEvent = new
        {
            type = "content_block_start",
            index = contentIndex,
            content_block = new { type = "text", text = "" },
        };

        await writer.WriteAsync("event: content_block_start\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(startEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);

        // text_delta events
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i += _wordsPerChunk)
        {
            var chunk = string.Join(' ', words.Skip(i).Take(Math.Min(_wordsPerChunk, words.Length - i)));
            if (i > 0)
            {
                chunk = " " + chunk;
            }

            var deltaEvent = new
            {
                type = "content_block_delta",
                index = contentIndex,
                delta = new { type = "text_delta", text = chunk },
            };

            await writer.WriteAsync("event: content_block_delta\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(deltaEvent)}\n\n");
            await writer.FlushAsync(ct);
            await DelayAsync(ct);
        }

        // content_block_stop
        var stopEvent = new { type = "content_block_stop", index = contentIndex };

        await writer.WriteAsync("event: content_block_stop\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(stopEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);
    }

    private async Task WriteThinkingEventsAsync(
        StreamWriter writer,
        string thinkingText,
        int contentIndex,
        CancellationToken ct
    )
    {
        // content_block_start for thinking
        var startEvent = new
        {
            type = "content_block_start",
            index = contentIndex,
            content_block = new { type = "thinking", thinking = "" },
        };

        await writer.WriteAsync("event: content_block_start\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(startEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);

        // thinking_delta events
        var words = thinkingText.Split(' ');
        for (var i = 0; i < words.Length; i += _wordsPerChunk)
        {
            var chunk = string.Join(' ', words.Skip(i).Take(Math.Min(_wordsPerChunk, words.Length - i)));
            if (i > 0)
            {
                chunk = " " + chunk;
            }

            var deltaEvent = new
            {
                type = "content_block_delta",
                index = contentIndex,
                delta = new { type = "thinking_delta", thinking = chunk },
            };

            await writer.WriteAsync("event: content_block_delta\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(deltaEvent)}\n\n");
            await writer.FlushAsync(ct);
            await DelayAsync(ct);
        }

        // Emit signature_delta to simulate Anthropic encrypted reasoning payload
        var signatureBytes = SHA256.HashData(Encoding.UTF8.GetBytes(thinkingText));
        var signature = Convert.ToBase64String(signatureBytes);
        var signatureEvent = new
        {
            type = "content_block_delta",
            index = contentIndex,
            delta = new { type = "signature_delta", signature },
        };

        await writer.WriteAsync("event: content_block_delta\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(signatureEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);

        // content_block_stop
        var stopEvent = new { type = "content_block_stop", index = contentIndex };

        await writer.WriteAsync("event: content_block_stop\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(stopEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);
    }

    private async Task WriteToolUseEventsAsync(
        StreamWriter writer,
        InstructionToolCall toolCall,
        int contentIndex,
        CancellationToken ct
    )
    {
        var id = $"toolu_{Guid.NewGuid():N}";

        // content_block_start
        var startEvent = new
        {
            type = "content_block_start",
            index = contentIndex,
            content_block = new
            {
                type = "tool_use",
                id,
                name = toolCall.Name,
                input = new { },
            },
        };

        await writer.WriteAsync("event: content_block_start\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(startEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);

        // input_json_delta events
        var inputJson = toolCall.ArgsJson;
        for (var i = 0; i < inputJson.Length; i += _wordsPerChunk * 5)
        {
            var chunkLen = Math.Min(_wordsPerChunk * 5, inputJson.Length - i);
            var chunk = inputJson.Substring(i, chunkLen);

            var deltaEvent = new
            {
                type = "content_block_delta",
                index = contentIndex,
                delta = new { type = "input_json_delta", partial_json = chunk },
            };

            await writer.WriteAsync("event: content_block_delta\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(deltaEvent)}\n\n");
            await writer.FlushAsync(ct);
            await DelayAsync(ct);
        }

        // content_block_stop
        var stopEvent = new { type = "content_block_stop", index = contentIndex };

        await writer.WriteAsync("event: content_block_stop\n");
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(stopEvent)}\n\n");
        await writer.FlushAsync(ct);
        await DelayAsync(ct);
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        if (_chunkDelayMs > 0)
        {
            await Task.Delay(_chunkDelayMs, ct);
        }
    }

    private static IEnumerable<string> GenerateLoremChunks(int totalWords, int wordsPerChunk)
    {
        if (totalWords <= 0)
        {
            yield break;
        }

        var lorem = (
            "lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor "
            + "incididunt ut labore et dolore magna aliqua ut enim ad minim veniam quis nostrud "
            + "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat duis aute irure "
            + "dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur "
            + "excepteur sint occaecat cupidatat non proident sunt in culpa qui officia deserunt mollit anim id est laborum"
        ).Split(' ');

        var words = new List<string>(totalWords);
        for (var i = 0; i < totalWords; i++)
        {
            words.Add(lorem[i % lorem.Length]);
        }

        for (var i = 0; i < words.Count; i += wordsPerChunk)
        {
            var chunkWords = words.Skip(i).Take(Math.Min(wordsPerChunk, words.Count - i));
            yield return string.Join(' ', chunkWords);
        }
    }
}
