using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Json.Schema;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Utils;

/// <summary>
/// Factory for creating ChatCompletionRequest objects from IMessage collections and GenerateReplyOptions
/// with support for different LLM providers.
/// </summary>
public static class ChatCompletionRequestFactory
{
    /// <summary>
    /// Creates a ChatCompletionRequest from a collection of messages and options
    /// with automatic provider detection based on model ID prefix or options.Providers.
    /// </summary>
    /// <param name="messages">The messages to include in the request</param>
    /// <param name="options">The generation options</param>
    /// <returns>A properly configured ChatCompletionRequest</returns>
    public static ChatCompletionRequest Create(IEnumerable<IMessage> messages, GenerateReplyOptions? options)
    {
        // Check if we're explicitly using OpenRouter
        bool isOpenRouter = IsOpenRouterRequest(options);
        
        if (isOpenRouter)
        {
            return CreateOpenRouterRequest(messages, options);
        }
        
        // Default to standard OpenAI format
        return CreateStandardRequest(messages, options);
    }
    
    /// <summary>
    /// Creates a standard OpenAI-compatible ChatCompletionRequest
    /// </summary>
    public static ChatCompletionRequest CreateStandardRequest(IEnumerable<IMessage> messages, GenerateReplyOptions? options)
    {
        // Get model name with fallback
        string modelName = GetModelName(options);
        
        // Extract basic parameters
        var temperature = options?.Temperature ?? 0.7f;
        var maxTokens = options?.MaxToken ?? 4096;
        
        // Convert messages to ChatMessage objects
        var chatMessages = ConvertMessagesToChat(messages);

        // Create the base request
        var request = new ChatCompletionRequest(
            modelName,
            chatMessages,
            temperature,
            maxTokens
        );
        
        // Set additional options
        ApplyStandardOptions(request, options);
        
        return request;
    }
    
    /// <summary>
    /// Creates an OpenRouter-specific ChatCompletionRequest
    /// </summary>
    public static ChatCompletionRequest CreateOpenRouterRequest(IEnumerable<IMessage> messages, GenerateReplyOptions? options)
    {
        // First create a standard request
        var request = CreateStandardRequest(messages, options);
        
        // Then apply OpenRouter specific parameters
        ApplyOpenRouterOptions(request, options);
        
        return request;
    }
    
    private static string GetModelName(GenerateReplyOptions? options)
    {
        // First check ModelId
        if (!string.IsNullOrEmpty(options?.ModelId))
        {
            return options.ModelId;
        }
        
        // Then check ExtraProperties for "model"
        if (options?.ExtraProperties != null && 
            options.ExtraProperties.TryGetValue("model", out var modelObj) && 
            modelObj is string modelStr)
        {
            return modelStr;
        }
        
        // Default model
        return "gpt-3.5-turbo";
    }
    
    private static bool IsOpenRouterRequest(GenerateReplyOptions? options)
    {
        // Check if Providers includes "openrouter"
        if (options?.Providers != null && options.Providers.Contains("openrouter"))
        {
            return true;
        }
        
        // Check if ModelId starts with a provider prefix (openrouter format)
        if (!string.IsNullOrEmpty(options?.ModelId) && 
            (options.ModelId.Contains('/') || options.ModelId.StartsWith("anthropic/") || 
             options.ModelId.StartsWith("meta/") || options.ModelId.StartsWith("google/")))
        {
            return true;
        }
        
        // Check if ExtraProperties contains OpenRouter specific keys
        if (options?.ExtraProperties != null && 
            (options.ExtraProperties.ContainsKey("route") || 
             options.ExtraProperties.ContainsKey("models") ||
             options.ExtraProperties.ContainsKey("transforms")))
        {
            return true;
        }
        
        return false;
    }
    
    private static List<ChatMessage> ConvertMessagesToChat(IEnumerable<IMessage> messages)
    {
        return messages.Select(message => {
            // Map role
            var role = message.Role == Role.User ? RoleEnum.User :
                       message.Role == Role.System ? RoleEnum.System :
                       message.Role == Role.Function ? RoleEnum.Tool :
                       RoleEnum.Assistant;

            var chatMessage = new ChatMessage { 
                Role = role,
                Name = message.FromAgent
            };

            // Convert based on message type
            if (message is TextMessage textMessage)
            {
                // Use simple string content
                chatMessage.Content = ChatMessage.CreateContent(textMessage.Text);
            }
            else if (message is ToolsCallMessage toolsCallMessage && toolsCallMessage.ToolCalls != null)
            {
                // Convert tool calls
                chatMessage.ToolCalls = new List<FunctionContent>();
                
                foreach (var tc in toolsCallMessage.ToolCalls)
                {
                    var toolId = tc.ToolCallId ?? Guid.NewGuid().ToString();
                    var functionCall = new FunctionContent.FunctionCall(tc.FunctionName, tc.FunctionArgs);
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
        }).ToList();
    }
    
    private static void ApplyStandardOptions(ChatCompletionRequest request, GenerateReplyOptions? options)
    {
        if (options == null) return;
        
        // Apply standard parameters
        if (options.TopP.HasValue)
            request.TopP = options.TopP.Value;
        
        if (options.StopSequence != null)
            request.Stop = options.StopSequence;
        
        if (options.Stream.HasValue)
            request.Stream = options.Stream.Value;
        
        if (options.SafePrompt.HasValue)
            request.SafePrompt = options.SafePrompt.Value;
        
        if (options.RandomSeed.HasValue)
            request.RandomSeed = options.RandomSeed.Value;
        
        // Convert function definitions to tools
        if (options.Functions != null && options.Functions.Length > 0)
        {
            request.Tools = new List<FunctionTool>();
            
            foreach (var f in options.Functions)
            {
                // Create new function definition with the available properties
                var def = new FunctionDefinition(
                    f.Name,
                    f.Description ?? string.Empty,
                    f.Parameters as JsonSchema
                );
                
                request.Tools.Add(new FunctionTool(def));
            }
        }
        
        // Check for response format
        if (options.ExtraProperties != null && 
            options.ExtraProperties.TryGetValue("response_format", out var formatObj) && 
            formatObj is Dictionary<string, object?> formatDict &&
            formatDict.TryGetValue("type", out var typeObj) &&
            typeObj is string typeStr)
        {
            request.ResponseFormat = new ResponseFormat { Type = typeStr };
        }
    }
    
    private static void ApplyOpenRouterOptions(ChatCompletionRequest request, GenerateReplyOptions? options)
    {
        if (options?.ExtraProperties == null) return;
        
        // Create a new JsonObject for the additional parameters
        var jsonObject = new JsonObject();
        
        // Add the existing AdditionalParameters if any
        if (request.AdditionalParameters != null)
        {
            var existingJson = request.AdditionalParameters.ToJsonString();
            var deserializedObject = JsonSerializer.Deserialize<JsonObject>(existingJson);
            if (deserializedObject != null)
            {
                foreach (var prop in deserializedObject)
                {
                    jsonObject[prop.Key] = prop.Value;
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
                        jsonObject["transforms"] = JsonValue.Create(transforms);
                    break;
                case "route":
                    if (kvp.Value is string route)
                        jsonObject["route"] = route;
                    break;
                case "models":
                    if (kvp.Value is string[] modelPreferences)
                        jsonObject["models"] = JsonValue.Create(modelPreferences);
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
            }
        }
        
        // Create a new request with the updated additional parameters
        var newRequest = new ChatCompletionRequest(
            request.Model,
            request.Messages,
            request.Temperature,
            request.MaxTokens,
            jsonObject
        );
        
        // Copy over other properties from the original request
        newRequest.Stream = request.Stream;
        newRequest.N = request.N;
        newRequest.Stop = request.Stop;
        newRequest.PresencePenalty = request.PresencePenalty;
        newRequest.FrequencyPenalty = request.FrequencyPenalty;
        newRequest.LogitBias = request.LogitBias;
        newRequest.User = request.User;
        newRequest.TopP = request.TopP;
        newRequest.SafePrompt = request.SafePrompt;
        newRequest.RandomSeed = request.RandomSeed;
        newRequest.Tools = request.Tools;
        newRequest.ToolChoice = request.ToolChoice;
        newRequest.ResponseFormat = request.ResponseFormat;
        
        // Replace the original request with the new one
        request = newRequest;
    }
} 