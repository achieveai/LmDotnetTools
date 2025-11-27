using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Represents a request to the Anthropic API for message completion.
/// </summary>
public record AnthropicRequest
{
    /// <summary>
    ///     The model to use for the completion.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = AnthropicModelNames.Claude3Sonnet;

    /// <summary>
    ///     The messages to include in the request.
    /// </summary>
    [JsonPropertyName("messages")]
    public List<AnthropicMessage> Messages { get; init; } = [];

    /// <summary>
    ///     The system prompt to use for the completion.
    /// </summary>
    [JsonPropertyName("system")]
    public string? System { get; init; }

    /// <summary>
    ///     The maximum number of tokens to generate.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    ///     Controls the randomness of the output.
    /// </summary>
    [JsonPropertyName("temperature")]
    public float Temperature { get; init; } = 1.0f; // For Thinking mode, temperature must be 1.0

    /// <summary>
    ///     Whether to stream the response.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    /// <summary>
    ///     Top-p parameter for nucleus sampling.
    /// </summary>
    [JsonPropertyName("top_p")]
    public float? TopP { get; init; }

    /// <summary>
    ///     Tool definitions for the request.
    /// </summary>
    [JsonPropertyName("tools")]
    public List<AnthropicTool>? Tools { get; init; }

    /// <summary>
    ///     Controls how the model uses tools.
    /// </summary>
    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; init; }

    /// <summary>
    ///     Configuration for extended thinking mode for compatible models.
    /// </summary>
    [JsonPropertyName("thinking")]
    public AnthropicThinking? Thinking { get; init; }

    /// <summary>
    ///     Creates an AnthropicRequest from a list of LmCore messages and options.
    /// </summary>
    /// <param name="messages">The messages to include in the request.</param>
    /// <param name="options">The options for the request.</param>
    /// <returns>A new AnthropicRequest.</returns>
    public static AnthropicRequest FromMessages(IEnumerable<IMessage> messages, GenerateReplyOptions? options = null)
    {
        // Get model from options or use default
        var modelName = AnthropicModelNames.Claude3Sonnet;
        if (options?.ModelId is { Length: > 0 } modelId)
        {
            modelName = modelId;
        }

        ArgumentNullException.ThrowIfNull(messages);

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
            switch (message)
            {
                case CompositeMessage compositeMsg:
                    var (primaryMessage, secondaryMessage) = AddCompositeMessage(compositeMsg);
                    anthropicMessages.Add(primaryMessage);
                    if (secondaryMessage != null)
                    {
                        anthropicMessages.Add(secondaryMessage);
                    }

                    break;
                case ToolsCallAggregateMessage aggregateMsg:
                    var assistantContents = new List<AnthropicContent>();
                    var userContents = new List<AnthropicContent>();

                    foreach (
                        var (toolCallContent, toolResultContent) in ToolCallAggregateMessageToAnthropicMessage(
                            aggregateMsg
                        )
                    )
                    {
                        assistantContents.Add(toolCallContent);
                        userContents.Add(toolResultContent);
                    }

                    anthropicMessages.Add(new AnthropicMessage { Role = "assistant", Content = assistantContents });
                    anthropicMessages.Add(new AnthropicMessage { Role = "user", Content = userContents });
                    break;
                default:
                    // Regular message handling for other message types
                    // Convert to Anthropic message format
                    var role = message.Role switch
                    {
                        Role.User => "user",
                        Role.Assistant => "assistant",
                        Role.Tool => "assistant", // Tool messages are mapped to assistant role
                        _ => "user", // Default to user if unknown
                    };

                    var anthropicMessage = new AnthropicMessage { Role = role };

                    AddBasicMessageContents(message, anthropicMessage);
                    // Add support for other message types as needed

                    if (anthropicMessage.Content.Count > 0)
                    {
                        anthropicMessages.Add(anthropicMessage);
                    }

                    break;
            }
        }

        // Extract the Thinking property from ExtraProperties if present
        AnthropicThinking? thinking = null;
        if (
            options?.ExtraProperties != null
            && options.ExtraProperties.TryGetValue("Thinking", out var thinkingObj)
            && thinkingObj is AnthropicThinking thinkingValue
        )
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
            Thinking = thinking,
        };
    }

    private static (AnthropicMessage primaryMessage, AnthropicMessage? secondaryMessage) AddCompositeMessage(
        CompositeMessage compositeMsg
    )
    {
        var role = compositeMsg.Role switch
        {
            Role.User => "user",
            Role.Assistant => "assistant",
            Role.Tool => "assistant", // Tool messages are mapped to assistant role
            _ => "user", // Default to user if unknown
        };

        var primaryMessage = new AnthropicMessage { Role = role };
        AnthropicMessage? secondaryMessage = null;
        foreach (var message in compositeMsg.Messages)
        {
            if (message is ToolsCallAggregateMessage aggregateMsg)
            {
                if (message.Role != Role.Assistant)
                {
                    throw new ArgumentException("ToolsCallAggregateMessage must be from the Assistant role");
                }

                secondaryMessage ??= new AnthropicMessage { Role = "user" };
                foreach (
                    var (toolCallContent, toolResultContent) in ToolCallAggregateMessageToAnthropicMessage(aggregateMsg)
                )
                {
                    primaryMessage.Content.Add(toolCallContent);
                    secondaryMessage.Content.Add(toolResultContent);
                }
            }
            else
            {
                AddBasicMessageContents(message, primaryMessage);
            }
        }

        return (primaryMessage, secondaryMessage);
    }

    private static void AddBasicMessageContents(IMessage message, AnthropicMessage anthropicMessage)
    {
        // Handle different message types
        if (message is TextMessage txtMsg)
        {
            anthropicMessage.Content.Add(new AnthropicContent { Type = "text", Text = txtMsg.Text });
        }
        else if (message is ToolsCallMessage toolsCallMsg && toolsCallMsg.ToolCalls?.Any() == true)
        {
            // Handle tool calls in assistant messages
            foreach (var toolCall in toolsCallMsg.ToolCalls)
            {
                anthropicMessage.Content.Add(
                    new AnthropicContent
                    {
                        Type = "tool_use",
                        Id = toolCall.ToolCallId,
                        Name = toolCall.FunctionName,
                        Input = JsonSerializer.Deserialize<JsonElement>(toolCall.FunctionArgs ?? "{}"),
                    }
                );
            }
        }
        else if (message is ToolsCallResultMessage toolResultMsg && toolResultMsg.ToolCallResults?.Any() == true)
        {
            // Handle tool results in user messages
            // First add a text content if there is any preceding text
            if (message is ICanGetText textContent && !string.IsNullOrEmpty(textContent.GetText()))
            {
                anthropicMessage.Content.Add(
                    new AnthropicContent { Type = "text", Text = textContent.GetText() ?? string.Empty }
                );
            }

            // Then add tool results
            foreach (var result in toolResultMsg.ToolCallResults)
            {
                anthropicMessage.Content.Add(
                    new AnthropicContent
                    {
                        Type = "tool_result",
                        ToolUseId = result.ToolCallId,
                        Content = result.Result,
                    }
                );
            }
        }
    }

    private static IEnumerable<(
        AnthropicContent toolCallContent,
        AnthropicContent toolResultContent
    )> ToolCallAggregateMessageToAnthropicMessage(ToolsCallAggregateMessage aggregateMsg)
    {
        // First create an assistant message with the tool calls
        if (aggregateMsg.ToolsCallMessage.ToolCalls?.Any() == true)
        {
            foreach (
                var (toolCall, toolResult) in aggregateMsg.ToolsCallMessage.ToolCalls.Zip(
                    aggregateMsg.ToolsCallResult.ToolCallResults
                )
            )
            {
                yield return (
                    new AnthropicContent
                    {
                        Type = "tool_use",
                        Id = toolCall.ToolCallId,
                        Name = toolCall.FunctionName,
                        Input = JsonSerializer.Deserialize<JsonElement>(toolCall.FunctionArgs ?? "{}"),
                    },
                    new AnthropicContent
                    {
                        Type = "tool_result",
                        ToolUseId = toolCall.ToolCallId,
                        Content = toolResult.Result,
                    }
                );
            }
        }
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
                    if (param.Name == null)
                    {
                        continue;
                    }

                    var paramSchema = new JsonObject
                    {
                        ["type"] = GetJsonType(param.ParameterType),
                        ["description"] = param.Description ?? string.Empty,
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
                ["description"] = $"Parameters for {function.Name}",
            };

            // Only add required array if it has items
            if (required.Count > 0)
            {
                inputSchema["required"] = required;
            }

            tools.Add(
                new AnthropicTool
                {
                    Name = function.Name,
                    Description = function.Description,
                    InputSchema = inputSchema,
                }
            );
        }

        return tools;
    }

    private static string GetJsonType(JsonSchemaObject schemaObject)
    {
        return schemaObject == null ? "string" : schemaObject.Type.GetTypeString();
    }
}
