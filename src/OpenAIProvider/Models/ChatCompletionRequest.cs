using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Json.More;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public record ChatCompletionRequest
{
    public ChatCompletionRequest(
        string model,
        IEnumerable<ChatMessage> messages,
        double temperature = 0.7,
        int maxTokens = 4096,
        JsonObject? additionalParameters = null)
    {
        Model = model;
        Messages = messages.ToList();
        Temperature = temperature;
        MaxTokens = maxTokens;
        AdditionalParameters = additionalParameters;
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

    [JsonExtensionData]
    public JsonObject? AdditionalParameters { get; }

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
                Stream = options.Stream ?? false,
                SafePrompt = options.SafePrompt,
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
            case CompositeMessage mmMessage:
                return [new ChatMessage {
                    Role = ChatMessage.ToRoleEnum(mmMessage.Role),
                    Content = new Union<string, Union<TextContent, ImageContent>[]>(
                        mmMessage.GetMessages()!.Select(c =>
                            c switch
                            {
                                ICanGetBinary im => new Union<TextContent, ImageContent>(
                                    new ImageContent(im.GetBinary().ToDataUrl()!)),
                                ICanGetText tm => new Union<TextContent, ImageContent>(
                                    new TextContent(tm.GetText()!)),
                                _ => throw new ArgumentException("Unsupported message type")
                            }).ToArray())
                }];
            case ToolsCallResultMessage toolCallResultMessage:
                return toolCallResultMessage.ToolCallResults
                    .Where(tc => tc.Result != null)
                    .Select(tc => {
                        var functionCall = new FunctionContent.FunctionCall(tc.ToolCall.FunctionName, tc.ToolCall.FunctionArgs);
                        var toolCallId = tc.ToolCall.ToolCallId ?? functionCall.ComputeToolCallId();
                        return 
                            new ChatMessage {
                                Role = RoleEnum.Tool,
                                ToolCallId = toolCallId,
                                Content = new Union<string, Union<TextContent, ImageContent>[]>(tc.Result!)
                            };
                    });
            case ToolCallAggregateMessage toolCallAggregateMessage:
                return FromMessage(toolCallAggregateMessage.ToolCallMessage)
                    .Concat(FromMessage(toolCallAggregateMessage.ToolCallResult));
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
                                tc.FunctionName,
                                tc.FunctionArgs
                            ))
                        ).ToList()
                }];
            default:
                throw new ArgumentException("Unsupported message type");
        }
    }

    private static JsonObject? CreateAdditionalParameters(GenerateReplyOptions options)
    {
        // Only create JsonObject if there are actual parameters to add
        bool hasProviders = options.Providers != null;
        bool hasExtraProperties = options.ExtraProperties.Count > 0;
    
        if (!hasProviders && !hasExtraProperties)
        {
            return null;
        }
    
        var parameters = new JsonObject();
    
        if (hasProviders)
        {
            parameters["provider"] = new JsonObject {
                ["order"] = new JsonArray(options.Providers!.Select(p => JsonValue.Create(p)).ToArray()),
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
    
        return parameters;
    }
} 