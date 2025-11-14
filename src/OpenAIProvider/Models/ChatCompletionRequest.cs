using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public record ChatCompletionRequest
{
    /// <summary>
    /// Parameterless constructor for JSON deserialization
    /// </summary>
    public ChatCompletionRequest()
    {
        Model = string.Empty;
        Messages = [];
        Temperature = 0.7;
        MaxTokens = 4096;
        AdditionalParameters = ImmutableDictionary<string, object>.Empty;
    }

    public ChatCompletionRequest(
        string model,
        IEnumerable<ChatMessage> messages,
        double temperature = 0.7,
        int maxTokens = 4096,
        IDictionary<string, object>? additionalParameters = null
    )
    {
        Model = model;
        Messages = messages.ToList();
        Temperature = temperature;
        MaxTokens = maxTokens;

        // Initialize the additional parameters
        if (additionalParameters != null)
        {
            AdditionalParameters = additionalParameters.ToImmutableDictionary();
        }
    }

    [JsonPropertyName("model")]
    public string Model { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("stream"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; init; }

    [JsonPropertyName("n")]
    public int N { get; init; } = 1;

    [JsonPropertyName("stop")]
    public string[]? Stop { get; init; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; init; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; init; }

    [JsonPropertyName("logit_bias")]
    public Dictionary<string, int>? LogitBias { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; init; }

    [JsonPropertyName("safe_prompt")]
    public bool? SafePrompt { get; init; }

    [JsonPropertyName("random_seed")]
    public int? RandomSeed { get; init; }

    [JsonPropertyName("tools")]
    public List<FunctionTool>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    public ToolChoiceEnum? ToolChoice { get; init; }

    [JsonPropertyName("response_format")]
    public ResponseFormat? ResponseFormat { get; init; }

    // Public property that exposes as ImmutableDictionary with caching
    [JsonExtensionData]
    private Dictionary<string, object> AdditionalParametersInternal
    {
        get { return AdditionalParameters.ToDictionary(); }
        init { AdditionalParameters = value.ToImmutableDictionary(); }
    }

    [JsonIgnore]
    public ImmutableDictionary<string, object> AdditionalParameters { get; init; } =
        ImmutableDictionary<string, object>.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; }

    public static ChatCompletionRequest FromMessages(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options,
        string? model = null
    )
    {
        // Translate with reasoning merge logic
        messages = messages
            .Select(m =>
                m switch
                {
                    CompositeMessage cm => cm.Messages.Any(mm => mm is UsageMessage)
                        ? new CompositeMessage
                        {
                            Role = cm.Role,
                            Messages = cm.Messages.Where(mm => mm is not UsageMessage).ToImmutableList(),
                        }
                        : m,
                    UsageMessage _ => null, // skip usage messages
                    _ => m,
                }
            )
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        var chatMessages = MergeReasoningIntoAssistant(messages).ToList();
        return options != null
            ? new ChatCompletionRequest(
                options.ModelId,
                chatMessages,
                options.Temperature ?? 0.0,
                options.MaxToken ?? 4096,
                CreateAdditionalParameters(options)
            )
            {
                TopP = options.TopP,
                Stream =
                    options.ExtraProperties.TryGetValue("stream", out var stream) && stream is bool
                        ? (bool)stream
                        : false,
                SafePrompt =
                    options.ExtraProperties.TryGetValue("safe_prompt", out var safePrompt) && safePrompt is bool
                        ? (bool)safePrompt
                        : null,
                RandomSeed = options.RandomSeed,
                ResponseFormat = options.ResponseFormat,
                Temperature = options.Temperature ?? 0.0,
                MaxTokens = options.MaxToken ?? 4096,
                Stop = options.StopSequence ?? [],
                Tools = options.Functions?.Select(fc => new FunctionTool(fc.ToOpenFunctionDefinition())).ToList(),
            }
            : new ChatCompletionRequest(model ?? "", chatMessages, temperature: 0.7f, maxTokens: 1024);
    }

    public static IEnumerable<ChatMessage> FromMessage(IMessage message)
    {
        switch (message)
        {
            case CompositeMessage compositeMsg:
                if (
                    compositeMsg.Messages.Any(m =>
                        m is ToolsCallAggregateMessage || m is ToolsCallMessage || m is ToolsCallResultMessage
                    )
                )
                {
                    return compositeMsg.Messages.SelectMany(FromMessage);
                }
                else
                {
                    List<ChatMessage> chatMessages =
                    [
                        new ChatMessage
                        {
                            Role = ChatMessage.ToRoleEnum(compositeMsg.Role),
                            Content = new Union<string, Union<TextContent, ImageContent>[]>(
                                compositeMsg
                                    .Messages.Where(m => m is not ReasoningMessage)
                                    .Select(m =>
                                        m switch
                                        {
                                            TextMessage textMessage => new Union<TextContent, ImageContent>(
                                                new TextContent(textMessage.Text)
                                            ),
                                            ImageMessage imageMessage => new Union<TextContent, ImageContent>(
                                                new ImageContent(imageMessage.ImageData.ToDataUrl()!)
                                            ),
                                            _ => throw new ArgumentException("Unsupported message type"),
                                        }
                                    )
                                    .ToArray()
                            ),
                        },
                    ];

                    var hasEncryptedReasoning = compositeMsg
                        .Messages.OfType<ReasoningMessage>()
                        .Any(r => r.Visibility == ReasoningVisibility.Encrypted);

                    var reasoningMessage = compositeMsg
                        .Messages.OfType<ReasoningMessage>()
                        .Where(r =>
                            hasEncryptedReasoning
                                ? r.Visibility == ReasoningVisibility.Encrypted
                                : r.Visibility == ReasoningVisibility.Plain
                        )
                        .FirstOrDefault();

                    if (reasoningMessage != null)
                    {
                        if (hasEncryptedReasoning)
                        {
                            chatMessages[0].ReasoningDetails =
                            [
                                new ChatMessage.ReasoningDetail
                                {
                                    Type = "reasoning.encrypted",
                                    Data = reasoningMessage.Reasoning,
                                },
                            ];
                        }
                        else
                        {
                            chatMessages[0].Reasoning = reasoningMessage.Reasoning;
                        }
                    }

                    return chatMessages;
                }
            case ImageMessage imageMessage:
                return
                [
                    new ChatMessage
                    {
                        Role = ChatMessage.ToRoleEnum(imageMessage.Role),
                        Content = new Union<string, Union<TextContent, ImageContent>[]>(
                            [
                                new Union<TextContent, ImageContent>(
                                    new ImageContent(imageMessage.ImageData.ToDataUrl()!)
                                ),
                            ]
                        ),
                    },
                ];
            case ToolsCallResultMessage toolCallResultMessage:
                return toolCallResultMessage
                    .ToolCallResults.Where(tc => tc.Result != null)
                    .Select(tc =>
                    {
                        var toolCallId = tc.ToolCallId;
                        return new ChatMessage
                        {
                            Role = RoleEnum.Tool,
                            ToolCallId = toolCallId,
                            Content = new Union<string, Union<TextContent, ImageContent>[]>(tc.Result!),
                        };
                    });
            case ToolsCallAggregateMessage toolCallAggregateMessage:
                return FromMessage(toolCallAggregateMessage.ToolsCallMessage)
                    .Concat(FromMessage(toolCallAggregateMessage.ToolsCallResult));
            case ICanGetText textMessage:
            {
                var cm = new ChatMessage
                {
                    Role = ChatMessage.ToRoleEnum(textMessage.Role),
                    Content =
                        textMessage.GetText() != null
                            ? new Union<string, Union<TextContent, ImageContent>[]>(textMessage.GetText()!)
                            : null,
                };

                if (
                    textMessage.Metadata != null
                    && textMessage.Metadata.TryGetValue("reasoning", out var rVal)
                    && rVal is string rStr
                )
                {
                    cm.Reasoning = rStr;
                }
                else if (
                    textMessage.Metadata != null
                    && textMessage.Metadata.TryGetValue("reasoning_details", out var dVal)
                    && dVal is List<ChatMessage.ReasoningDetail> details
                )
                {
                    cm.ReasoningDetails = details;
                }

                return [cm];
            }
            case ICanGetToolCalls toolCallMessage:
                var toolChat = new ChatMessage
                {
                    Role = RoleEnum.Assistant,
                    ToolCalls = toolCallMessage
                        .GetToolCalls()!
                        .Select(tc => new FunctionContent(
                            tc.ToolCallId ?? "call_" + $"tool_{tc.FunctionName}_{tc.FunctionArgs}".GetHashCode(),
                            new FunctionCall(tc.FunctionName!, tc.FunctionArgs!)
                        )
                        {
                            Index = tc.Index,
                        })
                        .ToList(),
                };

                if (
                    toolCallMessage.Metadata != null
                    && toolCallMessage.Metadata.TryGetValue("reasoning", out var tr)
                    && tr is string rs
                )
                {
                    toolChat.Reasoning = rs;
                }
                else if (
                    toolCallMessage.Metadata != null
                    && toolCallMessage.Metadata.TryGetValue("reasoning_details", out var rd)
                    && rd is List<ChatMessage.ReasoningDetail> list
                )
                {
                    toolChat.ReasoningDetails = list;
                }

                return [toolChat];
            case UsageMessage _:
                return [];
            default:
                throw new ArgumentException("Unsupported message type");
        }
    }

    private static IEnumerable<ChatMessage> MergeReasoningIntoAssistant(IEnumerable<IMessage> source)
    {
        var reasoningBuffer = new List<ReasoningMessage>();

        foreach (var m in source)
        {
            switch (m)
            {
                case ReasoningMessage r:
                    reasoningBuffer.Add(r);
                    continue;
                case ReasoningUpdateMessage u:
                    reasoningBuffer.Add(
                        new ReasoningMessage
                        {
                            Role = u.Role,
                            Reasoning = u.Reasoning,
                            FromAgent = u.FromAgent,
                            GenerationId = u.GenerationId,
                            Visibility = ReasoningVisibility.Plain,
                        }
                    );
                    continue;

                case TextMessage txt:
                case ToolsCallMessage tc:
                case ToolsCallAggregateMessage agg:
                {
                    var produced = FromMessage(m).ToList();
                    foreach (var ch in produced)
                    {
                        MergeReasoning(reasoningBuffer, ch);
                        yield return ch;
                    }
                    break;
                }

                default:
                    // For other message types (system/user/tool etc.) just forward conversion without merging
                    if (m is TextUpdateMessage || m is ToolsCallUpdateMessage || m is ReasoningUpdateMessage)
                    {
                        // never send update messages in ChatCompletionRequest
                        continue;
                    }

                    foreach (var ch in FromMessage(m))
                    {
                        yield return ch;
                    }

                    break;
            }
        }

        if (reasoningBuffer.Count > 0)
        {
            var chatMessage = new ChatMessage
            {
                Role = RoleEnum.Assistant,
                Content = new Union<string, Union<TextContent, ImageContent>[]>("\n\n"),
            };

            MergeReasoning(reasoningBuffer, chatMessage);
            yield return chatMessage;
        }
    }

    private static void MergeReasoning(List<ReasoningMessage> reasoningBuffer, ChatMessage? ch)
    {
        if (ch == null || reasoningBuffer.Count == 0)
        {
            return;
        }

        {
            // Prefer encrypted reasoning blocks. If any encrypted messages exist, drop plain ones.
            List<ReasoningMessage> selected;
            if (reasoningBuffer.Any(p => p.Visibility == ReasoningVisibility.Encrypted))
            {
                selected = reasoningBuffer.Where(p => p.Visibility == ReasoningVisibility.Encrypted).ToList();
            }
            else if (reasoningBuffer.Any(p => p.Visibility == ReasoningVisibility.Summary))
            {
                selected = reasoningBuffer.Where(p => p.Visibility == ReasoningVisibility.Summary).ToList();
            }
            else
            {
                selected = reasoningBuffer.ToList();
            }

            if (selected.Count == 1 && selected[0].Visibility == ReasoningVisibility.Plain)
            {
                // Single plain-text reasoning ⇒ emit "reasoning" field
                ch.Reasoning = selected[0].Reasoning ?? string.Empty;
            }
            else
            {
                // Multiple or encrypted ⇒ use reasoning_details array
                ch.ReasoningDetails = selected
                    .Select(p => new ChatMessage.ReasoningDetail
                    {
                        Type =
                            p.Visibility == ReasoningVisibility.Encrypted ? "reasoning.encrypted"
                            : p.Visibility == ReasoningVisibility.Summary ? "reasoning.summary"
                            : "reasoning",
                        Data = p.Reasoning ?? string.Empty,
                    })
                    .ToList();
            }
            reasoningBuffer.Clear();
        }
    }

    private static Dictionary<string, object>? CreateAdditionalParameters(GenerateReplyOptions options)
    {
        // Only create JsonObject if there are actual parameters to add
        bool hasExtraProperties = options.ExtraProperties.Count > 0;
        bool hasProviders =
            options.ExtraProperties.TryGetValue("providers", out var providers)
            && providers is IEnumerable<string> providerList
            && providerList.Any();

        if (!hasProviders && !hasExtraProperties)
        {
            return null;
        }

        var parameters = new JsonObject();

        if (hasProviders)
        {
            parameters["provider"] = new JsonObject
            {
                ["order"] = new JsonArray(((IEnumerable<string>)providers!).Select(p => JsonValue.Create(p)).ToArray()),
                ["allow_fallbacks"] = false,
            };
        }

        if (hasExtraProperties)
        {
            foreach (var kvp in options.ExtraProperties)
            {
                if (kvp.Value is null)
                {
                    parameters[kvp.Key] = null;
                }
                else if (kvp.Value is JsonNode node)
                {
                    parameters[kvp.Key] = node;
                }
                else
                {
                    // Use JsonSerializer to convert arbitrary objects (including dictionaries/arrays) into JsonNode
                    parameters[kvp.Key] = System.Text.Json.JsonSerializer.SerializeToNode(kvp.Value);
                }
            }
        }

        return parameters
            .Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value!))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}
