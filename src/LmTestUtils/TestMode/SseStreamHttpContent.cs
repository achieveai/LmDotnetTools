using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

public sealed class InstructionPlan(string idMessage, int? reasoningLength, List<InstructionMessage> messages)
{
    public string IdMessage { get; } = idMessage;
    public int? ReasoningLength { get; } = reasoningLength;
    public List<InstructionMessage> Messages { get; } = messages;
}

public sealed class InstructionMessage
{
    private InstructionMessage(int? textLength, List<InstructionToolCall>? toolCalls)
    {
        TextLength = textLength;
        ToolCalls = toolCalls;
    }

    public int? TextLength { get; }
    public List<InstructionToolCall>? ToolCalls { get; }

    public static InstructionMessage ForText(int length)
    {
        return new InstructionMessage(length, null);
    }

    public static InstructionMessage ForToolCalls(List<InstructionToolCall> calls)
    {
        return new InstructionMessage(null, calls);
    }
}

public sealed class InstructionToolCall(string name, string argsJson)
{
    public string Name { get; } = name;
    public string ArgsJson { get; } = argsJson;
}

public sealed class SseStreamHttpContent : HttpContent
{
    // Configuration constants with clear intent
    private const int DefaultWordsPerChunk = 5;
    private const int DefaultChunkDelayMs = 100;
    private const int MinWordsPerChunk = 1;
    private const int MinChunkDelayMs = 0;

    private static readonly JsonSerializerOptions _jsonSerializerOptionsWithReasoning =
        OpenClient.S_jsonSerializerOptions;

    private readonly int _chunkDelayMs;
    private readonly InstructionPlan? _instructionPlan;
    private readonly string? _model;
    private readonly bool _reasoningFirst;

    private readonly string _userMessage;
    private readonly int _wordsPerChunk;

    public SseStreamHttpContent(
        string userMessage,
        string? model = null,
        bool reasoningFirst = false,
        int wordsPerChunk = 3,
        int chunkDelayMs = 100
    )
    {
        _userMessage = userMessage;
        _model = model;
        _reasoningFirst = reasoningFirst;
        _wordsPerChunk = Math.Max(MinWordsPerChunk, wordsPerChunk);
        _chunkDelayMs = Math.Max(MinChunkDelayMs, chunkDelayMs);
        _instructionPlan = null;

        Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    }

    public SseStreamHttpContent(
        InstructionPlan instructionPlan,
        string? model = null,
        int wordsPerChunk = 5,
        int chunkDelayMs = 100
    )
    {
        _userMessage = string.Empty;
        _model = model;
        _reasoningFirst = false;
        _wordsPerChunk = Math.Max(MinWordsPerChunk, wordsPerChunk);
        _chunkDelayMs = Math.Max(MinChunkDelayMs, chunkDelayMs);
        _instructionPlan = instructionPlan;

        Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    // Back-compat override signature
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        return SerializeCoreAsync(stream, CancellationToken.None);
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<SseStreamHttpContent>();

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
                    // Expected when cancellation is requested
                    logger.LogDebug("Stream creation cancelled");
                }
                catch (Exception ex)
                {
                    // Log the exception but don't rethrow to avoid crashing background task
                    logger.LogError(ex, "Error during SSE stream creation");
                    error = ex;
                }
                finally
                {
                    // Complete the pipe writer, optionally with error information
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
        // Important: leave the provided stream open so HttpContent can buffer/read it after serialization
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = false };

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var generationId = $"gen-{created}-{Guid.NewGuid().ToString("N")[..16]}";

        async Task WriteSseAsync(object payload)
        {
            var json = JsonSerializer.Serialize(payload, _jsonSerializerOptionsWithReasoning);

            await writer.WriteAsync("data: ");
            await writer.WriteAsync(json);
            await writer.WriteAsync("\n\n");
            await writer.FlushAsync(cancellationToken);
        }

        // Removed initial empty chunk to avoid provider errors on null content

        var choices = Enumerable.Empty<Choice>();
        var maxMessageIdx = 0;
        if (_instructionPlan is not null)
        {
            choices = SerializeInstructionPlanAsync(_instructionPlan);
            maxMessageIdx = _instructionPlan.Messages.Count - 1;
        }
        else
        {
            var reasoning = string.Empty;
            var text = $"<|user_pre|><|text_message|> {_userMessage}";

            if (_reasoningFirst)
            {
                reasoning = $"<|user_pre|><|reasoning|> {_userMessage}";
                reasoning += string.Join(" ", GenerateReasoningTokens(_wordsPerChunk));
                reasoning += $"<|user_post|><|reasoning|> {_userMessage}";
            }

            var loremWordCount = CalculateStableWordCount(_userMessage);
            text += string.Join(" ", GenerateLoremChunks(loremWordCount, _wordsPerChunk));

            if (!string.IsNullOrEmpty(_userMessage))
            {
                text += $"<|user_post|><|text_message|> {_userMessage}";
            }

            if (reasoning.Length > 0)
            {
                choices = choices.Concat(ChunkReasoningText(0, reasoning, _wordsPerChunk));
            }

            if (text.Length > 0)
            {
                choices = choices.Concat(ChunkTextMessage(0, text, _wordsPerChunk));
            }

            maxMessageIdx = 0;
        }

        // Use the same generation ID for all chunks in this response
        choices = choices.Concat([BuildFinishChunk(maxMessageIdx)]);
        foreach (var choice in choices)
        {
            var responseMessage = new ChatCompletionResponse
            {
                Id = generationId,
                VarObject = "chat.completion.chunk",
                Created = (int)created,
                Model = _model ?? "test-model",
                Choices = [choice],
            };

            await WriteSseAsync(responseMessage);
            await Task.Delay(_chunkDelayMs, cancellationToken);
        }

        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(cancellationToken);
    }

    private static Choice BuildFinishChunk(int lastIdx)
    {
        return new Choice
        {
            Index = lastIdx,
            Delta = new ChatMessage { Role = RoleEnum.Assistant, Content = string.Empty },
            FinishReason = Choice.FinishReasonEnum.Stop,
        };
    }

    private IEnumerable<Choice> SerializeInstructionPlanAsync(InstructionPlan plan)
    {
        var choices = Enumerable.Empty<Choice>();

        // Emit reasoning first if configured at plan level (before any messages)
        if (plan.ReasoningLength is int rlen && rlen > 0)
        {
            var reasoning = string.Join(" ", GenerateLoremChunks(rlen, _wordsPerChunk));
            choices = choices.Concat(ChunkReasoningText(0, reasoning, _wordsPerChunk));
        }

        for (var msgIndex = 0; msgIndex < plan.Messages.Count; msgIndex++)
        {
            var message = plan.Messages[msgIndex];
            if (message.TextLength is int textLen)
            {
                if (textLen > 0)
                {
                    var text = string.Join(" ", GenerateLoremChunks(textLen, _wordsPerChunk));
                    choices = choices.Concat(ChunkTextMessage(msgIndex, text, _wordsPerChunk));
                }
            }
            else if (message.ToolCalls is not null)
            {
                choices = choices.Concat(
                    ChunkToolCalls(msgIndex, message.ToolCalls.Select(tc => (tc.Name, tc.ArgsJson)), _wordsPerChunk)
                );
            }
        }

        return choices;
    }

    private static int CalculateStableWordCount(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? string.Empty));
        var val = BitConverter.ToUInt32(bytes, 0);
        return 5 + (int)(val % 100u);
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

    private static IEnumerable<string> GenerateReasoningTokens(int wordsPerChunk)
    {
        var basis = new[]
        {
            "The",
            " user",
            " says",
            ":",
            " \"",
            "Setup",
            " message",
            " ",
            "1",
            " for",
            " existing",
            " conversation",
            " test",
            "\"",
            " then",
            " \"",
            "Setup",
            " message",
            " ",
            "2",
            " for",
            " existing",
            " conversation",
            "\"",
            ".",
            " They",
            " likely",
            " want",
            " me",
            " to",
            " the",
            " existing",
            " conversation",
            " after",
            " refresh",
            " \".",
            " They",
            " likely",
            " want",
            " me",
            " to",
            " respond",
            " to",
            " a",
            " previous",
            " conversation",
            ".",
            " But",
            " we",
            " don't",
            " have",
            " the",
            " previous",
            " conversation",
            " context",
            ".",
        };

        for (var i = 0; i < basis.Length; i += wordsPerChunk)
        {
            var chunkTokens = basis.Skip(i).Take(Math.Min(wordsPerChunk, basis.Length - i)).ToList();
            yield return string.Join(string.Empty, chunkTokens);
        }
    }

    private static IEnumerable<Choice> ChunkReasoningText(int index, string reasoning, int wordsPerChunk)
    {
        var tokens = reasoning.Split(' ');
        var useReasoning = true; // reasoning.GetHashCode() % 2 == 0;
        for (var i = 0; i < tokens.Length; i += wordsPerChunk)
        {
            var chunkTokens = string.Join(' ', tokens.Skip(i).Take(Math.Min(wordsPerChunk, tokens.Length - i)));
            yield return useReasoning
                ? new Choice
                {
                    Index = index,
                    Delta = new ChatMessage
                    {
                        Role = RoleEnum.Assistant,
                        Reasoning = chunkTokens,
                        Content = string.Empty,
                    },
                }
                : new Choice
                {
                    Index = index,
                    Delta = new ChatMessage
                    {
                        Role = RoleEnum.Assistant,
                        ReasoningDetails =
                        [
                            new ChatMessage.ReasoningDetail { Type = "reasoning.summary", Summary = chunkTokens },
                        ],
                        Content = string.Empty,
                    },
                };
        }

        yield return new Choice
        {
            Index = index,
            Delta = new ChatMessage
            {
                Role = RoleEnum.Assistant,
                ReasoningDetails =
                [
                    new ChatMessage.ReasoningDetail
                    {
                        Type = "reasoning.encrypted",
                        Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(reasoning)),
                    },
                ],
                Content = string.Empty,
            },
        };
    }

    private static IEnumerable<Choice> ChunkTextMessage(int index, string textContent, int wordsPerChunk)
    {
        var tokens = textContent.Split(' ');
        for (var i = 0; i < tokens.Length; i += wordsPerChunk)
        {
            var chunkTokens = string.Join(' ', tokens.Skip(i).Take(Math.Min(wordsPerChunk, tokens.Length - i)));
            yield return new Choice
            {
                Index = index,
                Delta = new ChatMessage
                {
                    Role = RoleEnum.Assistant,
                    Content = new Union<string, Union<TextContent, ImageContent>[]>(chunkTokens),
                },
            };
        }
    }

    private static IEnumerable<Choice> ChunkToolCalls(
        int index,
        IEnumerable<(string functionName, string argsJson)> toolCalls,
        int wordsPerChunk
    )
    {
        var allToolCalls = new List<FunctionContent>();
        var idx = -1;

        foreach (var (functionName, argsJson) in toolCalls)
        {
            idx++;
            var tool_call_id = Guid.NewGuid().ToString();

            // Store complete tool call for final message
            allToolCalls.Add(
                new FunctionContent(tool_call_id, new FunctionCall(functionName, argsJson)) { Index = idx }
            );

            // First chunk: function name with empty arguments
            // This matches OpenAI's actual behavior
            yield return new Choice
            {
                Index = index,
                Delta = new ChatMessage
                {
                    Role = RoleEnum.Assistant,
                    ToolCalls =
                    [
                        new FunctionContent(tool_call_id, new FunctionCall(functionName, string.Empty)) { Index = idx },
                    ],
                },
            };

            // Subsequent chunks: NO function name, only argument fragments
            // OpenAI omits the function name in subsequent chunks (not empty string)
            for (var i = 0; i < argsJson.Length; i += wordsPerChunk)
            {
                var len = Math.Min(wordsPerChunk, argsJson.Length - i);
                yield return new Choice
                {
                    Index = index,
                    Delta = new ChatMessage
                    {
                        Role = RoleEnum.Assistant,
                        ToolCalls =
                        [
                            new FunctionContent(tool_call_id, new FunctionCall(null, argsJson.Substring(i, len)))
                            {
                                Index = idx,
                            },
                        ],
                    },
                };
            }
        }
    }
}
