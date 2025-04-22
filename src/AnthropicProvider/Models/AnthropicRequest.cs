namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using System.Text.Json;

/// <summary>
/// Represents a request to the Anthropic API for message completion.
/// </summary>
public record AnthropicRequest
{
  /// <summary>
  /// The model to use for the completion.
  /// </summary>
  [JsonPropertyName("model")]
  public string Model { get; init; } = AnthropicModelNames.Claude3Sonnet;

  /// <summary>
  /// The messages to include in the request.
  /// </summary>
  [JsonPropertyName("messages")]
  public List<AnthropicMessage> Messages { get; init; } = new();

  /// <summary>
  /// The system prompt to use for the completion.
  /// </summary>
  [JsonPropertyName("system")]
  public string? System { get; init; }

  /// <summary>
  /// The maximum number of tokens to generate.
  /// </summary>
  [JsonPropertyName("max_tokens")]
  public int MaxTokens { get; init; } = 4096;

  /// <summary>
  /// Controls the randomness of the output.
  /// </summary>
  [JsonPropertyName("temperature")]
  public float Temperature { get; init; } = 1.0f; // For Thinking mode, temperature must be 1.0

  /// <summary>
  /// Whether to stream the response.
  /// </summary>
  [JsonPropertyName("stream")]
  public bool Stream { get; init; } = false;

  /// <summary>
  /// Top-p parameter for nucleus sampling.
  /// </summary>
  [JsonPropertyName("top_p")]
  public float? TopP { get; init; }

  /// <summary>
  /// Tool definitions for the request.
  /// </summary>
  [JsonPropertyName("tools")]
  public List<AnthropicTool>? Tools { get; init; }

  /// <summary>
  /// Controls how the model uses tools.
  /// </summary>
  [JsonPropertyName("tool_choice")]
  public string? ToolChoice { get; init; }

  /// <summary>
  /// Configuration for extended thinking mode for compatible models.
  /// </summary>
  [JsonPropertyName("thinking")]
  public AnthropicThinking? Thinking { get; init; }

  /// <summary>
  /// Creates an AnthropicRequest from a list of LmCore messages and options.
  /// </summary>
  /// <param name="messages">The messages to include in the request.</param>
  /// <param name="options">The options for the request.</param>
  /// <returns>A new AnthropicRequest.</returns>
  public static AnthropicRequest FromMessages(
    IEnumerable<IMessage> messages,
    GenerateReplyOptions? options = null)
  {
    // Get model from options or use default
    string modelName = AnthropicModelNames.Claude3Sonnet;
    if (options?.ModelId is { Length: > 0 } modelId)
    {
      modelName = modelId;
    }

    // Map LmCore messages to Anthropic messages
    var anthropicMessages = new List<AnthropicMessage>();
    string? systemPrompt = null;

    foreach (var message in messages)
    {
      // Extract system prompt if present
      if (message.Role == Role.System && message is TextMessage textMessage)
      {
        systemPrompt = textMessage.Text;
        continue;
      }

      // Special handling for ToolsCallAggregateMessage - separate into two messages
      if (message is ToolsCallAggregateMessage aggregateMsg)
      {
        // First create an assistant message with the tool calls
        if (aggregateMsg.ToolsCallMessage.ToolCalls?.Any() == true)
        {
          var assistantMessage = new AnthropicMessage { Role = "assistant" };
          
          foreach (var toolCall in aggregateMsg.ToolsCallMessage.ToolCalls)
          {
            assistantMessage.Content.Add(new AnthropicContent
            {
              Type = "tool_use",
              Id = toolCall.ToolCallId,
              Name = toolCall.FunctionName,
              Input = JsonSerializer.Deserialize<JsonElement>(toolCall.FunctionArgs ?? "{}")
            });
          }
          
          if (assistantMessage.Content.Count > 0)
          {
            anthropicMessages.Add(assistantMessage);
          }
        }
        
        // Then create a user message with the tool results
        if (aggregateMsg.ToolsCallResult.ToolCallResults?.Any() == true)
        {
          var userMessage = new AnthropicMessage { Role = "user" };
          
          // First add a text content if there is any preceding text
          if (aggregateMsg.ToolsCallResult is ICanGetText textContent && !string.IsNullOrEmpty(textContent.GetText()))
          {
            userMessage.Content.Add(new AnthropicContent
            {
              Type = "text",
              Text = textContent.GetText() ?? string.Empty
            });
          }

          // Then add tool results
          foreach (var result in aggregateMsg.ToolsCallResult.ToolCallResults)
          {
            userMessage.Content.Add(new AnthropicContent
            {
              Type = "tool_result",
              ToolUseId = result.ToolCallId,
              Content = result.Result
            });
          }
          
          if (userMessage.Content.Count > 0)
          {
            anthropicMessages.Add(userMessage);
          }
        }
        
        continue; // Skip the rest of the loop since we've handled this message
      }

      // Regular message handling for other message types
      // Convert to Anthropic message format
      var role = message.Role switch
      {
        Role.User => "user",
        Role.Assistant => "assistant",
        Role.Tool => "assistant", // Tool messages are mapped to assistant role
        _ => "user" // Default to user if unknown
      };

      var anthropicMessage = new AnthropicMessage { Role = role };

      // Handle different message types
      if (message is TextMessage txtMsg)
      {
        anthropicMessage.Content.Add(new AnthropicContent
        {
          Type = "text",
          Text = txtMsg.Text
        });
      }
      else if (message is ToolsCallMessage toolsCallMsg && toolsCallMsg.ToolCalls?.Any() == true)
      {
        // Handle tool calls in assistant messages
        foreach (var toolCall in toolsCallMsg.ToolCalls)
        {
          anthropicMessage.Content.Add(new AnthropicContent
          {
            Type = "tool_use",
            Id = toolCall.ToolCallId,
            Name = toolCall.FunctionName,
            Input = JsonSerializer.Deserialize<JsonElement>(toolCall.FunctionArgs ?? "{}")
          });
        }
      }
      else if (message is ToolsCallResultMessage toolResultMsg && toolResultMsg.ToolCallResults?.Any() == true)
      {
        // Handle tool results in user messages
        // First add a text content if there is any preceding text
        if (message is ICanGetText textContent && !string.IsNullOrEmpty(textContent.GetText()))
        {
          anthropicMessage.Content.Add(new AnthropicContent
          {
            Type = "text",
            Text = textContent.GetText() ?? string.Empty
          });
        }

        // Then add tool results
        foreach (var result in toolResultMsg.ToolCallResults)
        {
          anthropicMessage.Content.Add(new AnthropicContent
          {
            Type = "tool_result",
            ToolUseId = result.ToolCallId,
            Content = result.Result
          });
        }
      }
      // Add support for other message types as needed

      if (anthropicMessage.Content.Count > 0)
      {
        anthropicMessages.Add(anthropicMessage);
      }
    }

    // Extract the Thinking property from ExtraProperties if present
    AnthropicThinking? thinking = null;
    if (options?.ExtraProperties != null && options.ExtraProperties.TryGetValue("Thinking", out var thinkingObj) && 
        thinkingObj is AnthropicThinking thinkingValue)
    {
      thinking = thinkingValue;
    }
    
    // Create the request with options
    return new AnthropicRequest
    {
      Model = modelName,
      Messages = anthropicMessages,
      System = systemPrompt,
      MaxTokens = options?.MaxToken ?? 4096,
      Temperature = options?.Temperature ?? 0.7f,
      TopP = options?.TopP,
      Stream = false, // Set by caller if streaming is needed
      // Add tool configuration if functions are provided
      Tools = options?.Functions != null ? MapFunctionsToTools(options.Functions) : null,
      // Add thinking configuration if present
      Thinking = thinking
    };
  }

  private static List<AnthropicTool>? MapFunctionsToTools(FunctionContract[]? functions)
  {
    if (functions == null || functions.Length == 0)
    {
      return null;
    }

    var tools = new List<AnthropicTool>();
    
    foreach (var function in functions)
    {
      var properties = new JsonObject();
      var required = new JsonArray();
      
      if (function.Parameters != null)
      {
        foreach (var param in function.Parameters)
        {
          if (param.Name == null) continue;
          
          var paramSchema = new JsonObject
          {
            ["type"] = GetJsonType(param.ParameterType),
            ["description"] = param.Description ?? string.Empty
          };
          
          properties[param.Name] = paramSchema;
          
          // Add to required array if parameter is required
          if (param.IsRequired)
          {
            required.Add(param.Name);
          }
        }
      }
      
      // Create input_schema object following JSON Schema format
      var inputSchema = new JsonObject
      {
        ["type"] = "object",
        ["properties"] = properties,
        ["additionalProperties"] = true,
        ["description"] = $"Parameters for {function.Name}"
      };
      
      // Only add required array if it has items
      if (required.Count > 0)
      {
        inputSchema["required"] = required;
      }

      tools.Add(new AnthropicTool
      {
        Name = function.Name,
        Description = function.Description,
        InputSchema = inputSchema
      });
    }
    
    return tools;
  }

  private static string GetJsonType(JsonSchemaObject schemaObject)
  {
    if (schemaObject == null) return "string";
    
    return schemaObject.Type.ToLower() switch
    {
      "string" => "string",
      "integer" => "integer",
      "number" => "number",
      "boolean" => "boolean",
      "object" => "object",
      "array" => "array",
      _ => "string"
    };
  }
}
