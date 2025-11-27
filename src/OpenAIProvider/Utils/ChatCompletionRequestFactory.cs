using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Utils;

/// <summary>
///     Factory for creating ChatCompletionRequest objects from IMessage collections and GenerateReplyOptions
///     with support for different LLM providers.
/// </summary>
public static class ChatCompletionRequestFactory
{
    /// <summary>
    ///     Creates a ChatCompletionRequest from a collection of messages and options
    ///     with automatic provider detection based on model ID prefix or options.Providers.
    /// </summary>
    /// <param name="messages">The messages to include in the request</param>
    /// <param name="options">The generation options</param>
    /// <returns>A properly configured ChatCompletionRequest</returns>
    public static ChatCompletionRequest Create(IEnumerable<IMessage> messages, GenerateReplyOptions? options)
    {
        // Check if we're explicitly using OpenRouter
        var isOpenRouter = IsOpenRouterRequest(options);

        if (isOpenRouter)
        {
            return CreateOpenRouterRequest(messages, options);
        }

        // Default to standard OpenAI format
        return CreateStandardRequest(messages, options);
    }

    /// <summary>
    ///     Creates a standard OpenAI-compatible ChatCompletionRequest
    /// </summary>
    public static ChatCompletionRequest CreateStandardRequest(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options
    )
    {
        // Get model name with fallback
        var modelName = GetModelName(options);

        // Extract basic parameters
        var temperature = options?.Temperature ?? 0.7f;
        var maxTokens = options?.MaxToken ?? 4096;

        // Convert messages to ChatMessage objects
        var chatMessages = ConvertMessagesToChat(messages);

        // Create the base request
        var request = new ChatCompletionRequest(modelName, chatMessages, temperature, maxTokens);

        // Apply additional options and return the new instance
        return ApplyStandardOptions(request, options);
    }

    /// <summary>
    ///     Creates an OpenRouter-specific ChatCompletionRequest
    /// </summary>
    public static ChatCompletionRequest CreateOpenRouterRequest(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options
    )
    {
        // First create a standard request
        var request = CreateStandardRequest(messages, options);

        // Then apply OpenRouter specific parameters and return the new instance
        return ApplyOpenRouterOptions(request, options);
    }

    private static string GetModelName(GenerateReplyOptions? options)
    {
        // First check ModelId
        if (!string.IsNullOrEmpty(options?.ModelId))
        {
            return options.ModelId;
        }

        // Then check ExtraProperties for "model"
        if (
            options?.ExtraProperties != null
            && options.ExtraProperties.TryGetValue("model", out var modelObj)
            && modelObj is string modelStr
        )
        {
            return modelStr;
        }

        // Default model
        return "gpt-3.5-turbo";
    }

    private static bool IsOpenRouterRequest(GenerateReplyOptions? options)
    {
        // Check if Providers includes "openrouter"
        if (
            options?.ExtraProperties.TryGetValue("providers", out var providers) == true
            && providers is IEnumerable<string> providerArray
            && providerArray.Any()
        )
        {
            return true;
        }

        // Check if ModelId starts with a provider prefix (openrouter format)
        if (
            !string.IsNullOrEmpty(options?.ModelId)
            && (
                options.ModelId.Contains('/')
                || options.ModelId.StartsWith("anthropic/")
                || options.ModelId.StartsWith("meta/")
                || options.ModelId.StartsWith("google/")
            )
        )
        {
            return true;
        }

        // Check if ExtraProperties contains OpenRouter specific keys
        return options?.ExtraProperties != null
            && (
                options.ExtraProperties.ContainsKey("route")
                || options.ExtraProperties.ContainsKey("models")
                || options.ExtraProperties.ContainsKey("transforms")
            );
    }

    private static List<ChatMessage> ConvertMessagesToChat(IEnumerable<IMessage> messages)
    {
        return
        [
            .. messages.Select(message =>
            {
                // Map role
                var role =
                    message.Role == Role.User ? RoleEnum.User
                    : message.Role == Role.System ? RoleEnum.System
                    : message.Role == Role.Tool ? RoleEnum.Tool
                    : RoleEnum.Assistant;

                var chatMessage = new ChatMessage { Role = role, Name = message.FromAgent };

                // Convert based on message type
                if (message is TextMessage textMessage)
                {
                    // Use simple string content
                    chatMessage.Content = ChatMessage.CreateContent(textMessage.Text);
                }
                else if (message is ToolsCallMessage toolsCallMessage && toolsCallMessage.ToolCalls != null)
                {
                    // Convert tool calls
                    chatMessage.ToolCalls = [];

                    foreach (var tc in toolsCallMessage.ToolCalls)
                    {
                        var toolId = tc.ToolCallId ?? Guid.NewGuid().ToString();
                        var functionName = tc.FunctionName ?? string.Empty;
                        var functionArgs = tc.FunctionArgs ?? string.Empty;
                        var functionCall = new FunctionCall(functionName, functionArgs);
                        chatMessage.ToolCalls.Add(new FunctionContent(toolId, functionCall));
                    }
                }
                // Simpler handling for other message types - plain text only
                else if (message is ICanGetText textProvider)
                {
                    var text = textProvider.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        chatMessage.Content = ChatMessage.CreateContent(text);
                    }
                }

                return chatMessage;
            }),
        ];
    }

    private static ChatCompletionRequest ApplyStandardOptions(
        ChatCompletionRequest request,
        GenerateReplyOptions? options
    )
    {
        if (options == null)
        {
            return request;
        }

        // Prepare properties for the new request instance
        var topP = options.TopP ?? request.TopP;
        var stop = options.StopSequence ?? request.Stop;
        bool? stream =
            options.ExtraProperties.TryGetValue("stream", out var streamObj) && streamObj is bool streamBool
                ? streamBool
                : request.Stream;
        var safePrompt =
            options.ExtraProperties.TryGetValue("safe_prompt", out var safePromptObj)
            && safePromptObj is bool safePromptBool
                ? safePromptBool
                : request.SafePrompt;
        var randomSeed = options.RandomSeed ?? request.RandomSeed;

        // Prepare tools if functions are provided
        var tools = request.Tools;

        if (options.Functions != null && options.Functions.Length > 0)
        {
            tools = [.. options.Functions.Select(f => new FunctionTool(f.ToOpenFunctionDefinition()))];
        }

        // Check for response format
        var responseFormat = request.ResponseFormat;

        if (
            options.ExtraProperties != null
            && options.ExtraProperties.TryGetValue("response_format", out var formatObj)
            && formatObj is Dictionary<string, object?> formatDict
            && formatDict.TryGetValue("type", out var typeObj)
            && typeObj is string typeStr
        )
        {
            responseFormat = new ResponseFormat { ResponseFormatType = typeStr };
        }

        // Create a new instance with all the updated properties
        return new ChatCompletionRequest(
            request.Model,
            request.Messages,
            request.Temperature,
            request.MaxTokens,
            request.AdditionalParameters
        )
        {
            TopP = topP,
            Stop = stop,
            Stream = stream ?? false,
            SafePrompt = safePrompt,
            RandomSeed = randomSeed,
            Tools = tools,
            ResponseFormat = responseFormat,
            N = request.N,
            PresencePenalty = request.PresencePenalty,
            FrequencyPenalty = request.FrequencyPenalty,
            LogitBias = request.LogitBias,
            User = request.User,
            ToolChoice = request.ToolChoice,
        };
    }

    private static ChatCompletionRequest ApplyOpenRouterOptions(
        ChatCompletionRequest request,
        GenerateReplyOptions? options
    )
    {
        if (options?.ExtraProperties == null)
        {
            return request;
        }

        // Create a new JsonObject for the additional parameters
        var jsonObject = new Dictionary<string, object>();

        // Add the existing AdditionalParameters if any
        if (request.AdditionalParameters != null)
        {
            var deserializedObject = request.AdditionalParameters.ToDictionary();
            if (deserializedObject != null)
            {
                foreach (var prop in deserializedObject)
                {
                    jsonObject[prop.Key] = prop.Value!;
                }
            }
        }

        // Copy OpenRouter-specific properties
        foreach (var kvp in options.ExtraProperties)
        {
            switch (kvp.Key)
            {
                case "transforms":
                    if (kvp.Value is string[] transforms)
                    {
                        jsonObject["transforms"] = transforms;
                    }

                    break;
                case "route":
                    if (kvp.Value is string route)
                    {
                        jsonObject["route"] = route;
                    }

                    break;
                case "models":
                    if (kvp.Value is string[] modelPreferences)
                    {
                        jsonObject["models"] = modelPreferences;
                    }

                    break;
                case "http_headers":
                    if (kvp.Value is Dictionary<string, string> headers)
                    {
                        var headersObj = new JsonObject();
                        foreach (var header in headers)
                        {
                            headersObj[header.Key] = header.Value;
                        }

                        jsonObject["http_headers"] = headersObj;
                    }

                    break;
                default:
                    // Ignore unknown properties
                    break;
            }
        }

        // Create a new request with all the properties from the original plus our changes
        return new ChatCompletionRequest(
            request.Model,
            request.Messages,
            request.Temperature,
            request.MaxTokens,
            jsonObject
        )
        {
            Stream = request.Stream,
            N = request.N,
            Stop = request.Stop,
            PresencePenalty = request.PresencePenalty,
            FrequencyPenalty = request.FrequencyPenalty,
            LogitBias = request.LogitBias,
            User = request.User,
            TopP = request.TopP,
            SafePrompt = request.SafePrompt,
            RandomSeed = request.RandomSeed,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat,
        };
    }
}
