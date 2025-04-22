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
    public ChatCompletionRequest(
        string model,
        IEnumerable<ChatMessage> messages,
        double temperature = 0.7,
        int maxTokens = 4096,
        IDictionary<string, object>? additionalParameters = null)
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
    private Dictionary<string, object> AdditionalParametersInternal {
        get { return AdditionalParameters.ToDictionary(); } 
        init { AdditionalParameters = value.ToImmutableDictionary(); }
    }

    [JsonIgnore]
    public ImmutableDictionary<string, object> AdditionalParameters  { get; init; } = ImmutableDictionary<string, object>.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; private set; }

    public static ChatCompletionRequest FromMessages(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options,
        string? model = null)
    {
        var chatMessages = messages.SelectMany(m => FromMessage(m)).ToList();
        if (options != null)
        {
            return new ChatCompletionRequest(
                options.ModelId,
                chatMessages,
                options.Temperature ?? 0.0,
                options.MaxToken ?? 4096,
                CreateAdditionalParameters(options)
            ) {
                TopP = options.TopP,
                Stream = options.ExtraProperties
                    .TryGetValue("stream", out var stream) && stream is bool ? (bool)stream : false,
                SafePrompt = options.ExtraProperties
                    .TryGetValue("safe_prompt", out var safePrompt) && safePrompt is bool
                    ? (bool)safePrompt : null,
                RandomSeed = options.RandomSeed,
                ResponseFormat = options.ResponseFormat,
                Temperature = options.Temperature ?? 0.0,
                MaxTokens = options.MaxToken ?? 4096,
                Stop = options.StopSequence ?? Array.Empty<string>(),
                Tools = options.Functions?
                    .Select(fc => new FunctionTool(fc.ToOpenFunctionDefinition()))
                    .ToList()
            };
        }

        return new ChatCompletionRequest(
            model ?? "",
            chatMessages,
            temperature: 0.7f,
            maxTokens: 1024);
    }

    public static IEnumerable<ChatMessage> FromMessage(IMessage message)
    {
        switch (message)
        {
            case ImageMessage imageMessage:
                return [new ChatMessage {
                    Role = ChatMessage.ToRoleEnum(imageMessage.Role),
                    Content = new Union<string, Union<TextContent, ImageContent>[]>(
                        [new Union<TextContent, ImageContent>(
                            new ImageContent(imageMessage.ImageData.ToDataUrl()!))])
                }];
            case ToolsCallResultMessage toolCallResultMessage:
                return toolCallResultMessage.ToolCallResults
                    .Where(tc => tc.Result != null)
                    .Select(tc => {
                        var toolCallId = tc.ToolCallId;
                        return 
                            new ChatMessage {
                                Role = RoleEnum.Tool,
                                ToolCallId = toolCallId,
                                Content = new Union<string, Union<TextContent, ImageContent>[]>(tc.Result!)
                            };
                    });
            case ToolsCallAggregateMessage toolCallAggregateMessage:
                return FromMessage(toolCallAggregateMessage.ToolsCallMessage)
                    .Concat(FromMessage(toolCallAggregateMessage.ToolsCallResult));
            case ICanGetText textMessage:
                return [new ChatMessage {
                    Role = ChatMessage.ToRoleEnum(textMessage.Role),
                    Content = textMessage.GetText() != null 
                        ? new Union<string, Union<TextContent, ImageContent>[]>(textMessage.GetText()!)
                        : null
                }];
            case ICanGetToolCalls toolCallMessage:
                return [new ChatMessage {
                    Role = RoleEnum.Assistant,
                    ToolCalls = toolCallMessage.GetToolCalls()!.Select(tc =>
                        new FunctionContent(
                            tc.ToolCallId ?? "call_" + $"tool_{tc.FunctionName}_{tc.FunctionArgs}".GetHashCode(),
                            new FunctionContent.FunctionCall(
                                tc.FunctionName!,
                                tc.FunctionArgs!
                            ))
                        ).ToList()
                }];
            case UsageMessage _:
                return [];
            default:
                throw new ArgumentException("Unsupported message type");
        }
    }

    private static Dictionary<string, object>? CreateAdditionalParameters(GenerateReplyOptions options)
    {
        // Only create JsonObject if there are actual parameters to add
        bool hasExtraProperties = options.ExtraProperties.Count > 0;
        bool hasProviders = options.ExtraProperties.TryGetValue("providers", out var providers)
            && providers is IEnumerable<string> providerList
            && providerList.Any();
    
        if (!hasProviders && !hasExtraProperties)
        {
            return null;
        }
    
        var parameters = new JsonObject();
    
        if (hasProviders)
        {
            parameters["provider"] = new JsonObject {
                ["order"] = new JsonArray(((IEnumerable<string>)providers!).Select(p => JsonValue.Create(p)).ToArray()),
                ["allow_fallbacks"] = false
            };
        }
    
        if (hasExtraProperties)
        {
            foreach (var kvp in options.ExtraProperties)
            {
                parameters[kvp.Key] = JsonValue.Create(kvp.Value);
            }
        }
    
        return parameters
            .Select(kvp => new KeyValuePair<string, object>(kvp.Key, kvp.Value!))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
} 