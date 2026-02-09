using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.AnthropicProvider.Utils;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Helper class for parsing Anthropic SSE stream events into IMessage objects
/// </summary>
public class AnthropicStreamParser
{
    private readonly Dictionary<int, StreamingContentBlock> _contentBlocks = [];
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger _logger;
    private readonly List<IMessage> _messages = [];
    private string _messageId = string.Empty;
    private string _model = string.Empty;
    private string _role = "assistant";
    private AnthropicUsage? _usage;

    /// <summary>
    ///     Creates a new instance of the AnthropicStreamParser
    /// </summary>
    public AnthropicStreamParser(ILogger? logger = null)
    {
        _jsonOptions = AnthropicJsonSerializerOptionsFactory.CreateUniversal();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     Processes a raw SSE event string and returns any resulting IMessage updates
    /// </summary>
    /// <param name="eventType">The SSE event type</param>
    /// <param name="data">The SSE event data (JSON string)</param>
    /// <returns>A list of message updates (empty if none produced by this event)</returns>
    public List<IMessage> ProcessEvent(string eventType, string data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return [];
        }

        try
        {
            // Parse the JSON data
            var json = JsonNode.Parse(data);
            if (json == null)
            {
                return [];
            }

            var eventTypeFromJson = json["type"]?.GetValue<string>();
            if (string.IsNullOrEmpty(eventTypeFromJson))
            {
                return [];
            }

            switch (eventTypeFromJson)
            {
                case "message_start":
                    return HandleMessageStart(json);
                case "content_block_start":
                    return HandleContentBlockStart(json);
                case "content_block_delta":
                    return HandleContentBlockDelta(json);
                case "content_block_stop":
                    return HandleContentBlockStop(json);
                case "message_delta":
                    return HandleMessageDelta(json);
                case "message_stop":
                    return HandleMessageStop(json);
                case "ping":
                    return []; // Ignore ping events
                default:
                    Console.Error.WriteLine($"Unknown event type: {eventTypeFromJson}");
                    return [];
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error parsing SSE data: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    ///     Processes a strongly-typed AnthropicStreamEvent and returns any resulting IMessage updates
    /// </summary>
    /// <param name="streamEvent">The strongly-typed stream event</param>
    /// <returns>A list of message updates (empty if none produced by this event)</returns>
    public List<IMessage> ProcessStreamEvent(AnthropicStreamEvent streamEvent)
    {
        return streamEvent switch
        {
            AnthropicMessageStartEvent messageStartEvent => HandleTypedMessageStart(messageStartEvent),
            AnthropicContentBlockStartEvent contentBlockStartEvent => HandleTypedContentBlockStart(
                contentBlockStartEvent
            ),
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent => HandleTypedContentBlockDelta(
                contentBlockDeltaEvent
            ),
            AnthropicContentBlockStopEvent contentBlockStopEvent => HandleTypedContentBlockStop(contentBlockStopEvent),
            AnthropicMessageDeltaEvent messageDeltaEvent => HandleTypedMessageDelta(messageDeltaEvent),
            AnthropicMessageStopEvent => HandleTypedMessageStop(),
            AnthropicPingEvent => [], // Ignore ping events
            AnthropicErrorEvent errorEvent => HandleTypedError(errorEvent),
            _ => [], // Unknown event type
        };
    }

    private List<IMessage> HandleMessageStart(JsonNode json)
    {
        var message = json["message"];
        if (message == null)
        {
            return [];
        }

        // Store message properties
        _messageId = message["id"]?.GetValue<string>() ?? string.Empty;
        _model = message["model"]?.GetValue<string>() ?? string.Empty;
        _role = message["role"]?.GetValue<string>() ?? "assistant";

        // Get usage if available
        if (message["usage"] != null)
        {
            _usage = JsonSerializer.Deserialize<AnthropicUsage>(message["usage"]!.ToJsonString(), _jsonOptions);
        }

        // No messages to return yet
        return [];
    }

    private List<IMessage> HandleContentBlockStart(JsonNode json)
    {
        var index = json["index"]?.GetValue<int>() ?? 0;
        var contentBlock = json["content_block"];
        if (contentBlock == null)
        {
            return [];
        }

        var blockType = contentBlock["type"]?.GetValue<string>() ?? string.Empty;

        // Create and store the content block
        _contentBlocks[index] = new StreamingContentBlock
        {
            Index = index,
            Type = blockType,
            Id = contentBlock["id"]?.GetValue<string>(),
            Name = contentBlock["name"]?.GetValue<string>(),
            Input = contentBlock["input"] is JsonObject inputObj ? inputObj : null,
        };

        // Check for citations on text blocks
        if (contentBlock["citations"] != null)
        {
            try
            {
                var citationsJson = contentBlock["citations"]!.ToJsonString();
                _logger.LogDebug(
                    "Parsing citations from content_block_start: {CitationsJson}",
                    citationsJson
                );
                _contentBlocks[index].Citations = JsonSerializer.Deserialize<List<Citation>>(
                    citationsJson,
                    _jsonOptions
                );
                _logger.LogDebug(
                    "Parsed {CitationCount} citations for block {Index}",
                    _contentBlocks[index].Citations?.Count ?? 0,
                    index
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse citations from content_block_start");
            }
        }
        else
        {
            _logger.LogDebug(
                "No citations found on content_block_start for block {Index} (type={BlockType})",
                index,
                blockType
            );
        }

        // For tool_use blocks, create a ToolsCallUpdateMessage instead of immediately finalizing
        if (blockType == "tool_use" && !string.IsNullOrEmpty(_contentBlocks[index].Id))
        {
            var input = contentBlock["input"]?.AsObject();
            var toolUpdate = new ToolsCallUpdateMessage
            {
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                ToolCallUpdates =
                [
                    new ToolCallUpdate
                    {
                        ToolCallId = _contentBlocks[index].Id!,
                        Index = index,
                        // Only include FunctionName if it's available
                        FunctionName = !string.IsNullOrEmpty(_contentBlocks[index].Name)
                            ? _contentBlocks[index].Name
                            : null,
                        // Only include FunctionArgs if Input is available
                        FunctionArgs = input != null && input.Count > 0 ? input.ToJsonString() : null,
                    },
                ],
            };

            return [toolUpdate];
        }

        // Handle server_tool_use blocks (built-in tools like web_search, web_fetch)
        // Don't emit ServerToolUseMessage here — defer to content_block_stop so we can
        // include input accumulated from input_json_delta events.
        if (blockType == "server_tool_use")
        {
            // Store tool_use_id for correlating with results (may be empty — synthetic ID generated at stop)
            _contentBlocks[index].ToolUseId = _contentBlocks[index].Id ?? string.Empty;

            // Store input from content_block_start if present (for non-streaming / complete input)
            var inputNode = contentBlock["input"];
            if (inputNode is JsonObject serverInputObj)
            {
                _contentBlocks[index].Input = serverInputObj;
            }
            else if (inputNode != null)
            {
                // Store as-is even if not a JsonObject (defensive for non-standard providers)
                _contentBlocks[index].Input = inputNode;
            }

            return [];
        }

        // Handle server tool result blocks (web_search_tool_result, web_fetch_tool_result, etc.)
        if (IsServerToolResultType(blockType))
        {
            var rawToolUseId = contentBlock["tool_use_id"]?.GetValue<string>() ?? string.Empty;
            var toolName = GetToolNameFromResultType(blockType);
            var toolUseId = ResolveServerToolUseId(rawToolUseId, toolName);
            var resultContent = contentBlock["content"];
            var isError = false;
            string? errorCode = null;

            // Check if this is an error result (content is an object with type ending in "_error")
            if (resultContent is JsonObject resultObj)
            {
                var contentType = resultObj["type"]?.GetValue<string>();
                if (contentType?.EndsWith("_error") == true)
                {
                    isError = true;
                    errorCode = resultObj["error_code"]?.GetValue<string>();
                }
            }

            var serverToolResult = new ServerToolResultMessage
            {
                ToolUseId = toolUseId,
                ToolName = toolName,
                Result = resultContent != null
                    ? JsonSerializer.Deserialize<JsonElement>(resultContent.ToJsonString())
                    : default,
                IsError = isError,
                ErrorCode = errorCode,
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
            };

            _messages.Add(serverToolResult);
            return [serverToolResult];
        }

        return [];
    }

    private static bool IsServerToolResultType(string blockType)
    {
        return blockType is "web_search_tool_result"
            or "web_fetch_tool_result"
            or "bash_code_execution_tool_result"
            or "text_editor_code_execution_tool_result";
    }

    private static string GetToolNameFromResultType(string resultType)
    {
        return resultType switch
        {
            "web_search_tool_result" => "web_search",
            "web_fetch_tool_result" => "web_fetch",
            "bash_code_execution_tool_result" => "bash_code_execution",
            "text_editor_code_execution_tool_result" => "text_editor_code_execution",
            _ => resultType.Replace("_tool_result", ""),
        };
    }

    private List<IMessage> HandleContentBlockDelta(JsonNode json)
    {
        var index = json["index"]?.GetValue<int>() ?? 0;
        var delta = json["delta"];
        if (delta == null)
        {
            return [];
        }

        var deltaType = delta["type"]?.GetValue<string>() ?? string.Empty;

        // Make sure we have a content block for this index
        if (!_contentBlocks.TryGetValue(index, out var block))
        {
            block = new StreamingContentBlock { Index = index, Type = "text" };
            _contentBlocks[index] = block;
        }

        // Handle different delta types
        switch (deltaType)
        {
            case "text_delta":
                {
                    var text = delta["text"]?.GetValue<string>() ?? string.Empty;
                    block.Text += text;

                    // Return a TextUpdateMessage for the delta
                    var textUpdate = new TextUpdateMessage
                    {
                        Text = text,
                        Role = ParseRole(_role),
                        FromAgent = _messageId,
                        GenerationId = _messageId,
                        IsThinking = false,
                    };

                    return [textUpdate];
                }

            case "thinking_delta":
                {
                    var thinkingText = delta["thinking"]?.GetValue<string>() ?? string.Empty;
                    block.Text = thinkingText; // Replace with latest thinking

                    // Return a TextUpdateMessage for the thinking update
                    var thinkingUpdate = new TextUpdateMessage
                    {
                        Text = thinkingText,
                        Role = ParseRole(_role),
                        FromAgent = _messageId,
                        GenerationId = _messageId,
                        IsThinking = true,
                    };

                    return [thinkingUpdate];
                }

            case "input_json_delta":
                {
                    return HandleJsonDelta(block, delta["partial_json"]?.GetValue<string>() ?? string.Empty);
                }

            default:
                // Unknown delta type, ignore
                return [];
        }
    }

    private List<IMessage> HandleContentBlockStop(JsonNode json)
    {
        var index = json["index"]?.GetValue<int>() ?? 0;

        // Check if we have a content block for this index
        if (!_contentBlocks.TryGetValue(index, out var block))
        {
            _logger.LogWarning(
                "content_block_stop received for unknown block index {Index}. " +
                "This may indicate a dropped content_block_start event.",
                index
            );
            return [];
        }

        // Handle tool use blocks
        if (block.Type == "tool_use")
        {
            return FinalizeToolUseBlock(block);
        }

        // Handle server_tool_use blocks — emit ServerToolUseMessage with accumulated input from input_json_delta
        if (block.Type == "server_tool_use")
        {
            return FinalizeServerToolUseBlock(block);
        }

        // For text blocks, create a final TextMessage (or TextWithCitationsMessage if citations present)
        if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
        {
            // Check if we have citations - if so, create TextWithCitationsMessage
            if (block.Citations != null && block.Citations.Count > 0)
            {
                var citationsMessage = new TextWithCitationsMessage
                {
                    Text = block.Text,
                    Citations = block.Citations
                        .Select(c => new CitationInfo
                        {
                            Type = c.Type,
                            Url = c.Url,
                            Title = c.Title,
                            CitedText = c.CitedText,
                            StartIndex = c.StartCharIndex,
                            EndIndex = c.EndCharIndex,
                        })
                        .ToImmutableList(),
                    Role = ParseRole(_role),
                    FromAgent = _messageId,
                    GenerationId = _messageId,
                };

                // Apply usage if available
                if (_usage != null)
                {
                    citationsMessage = citationsMessage with { Metadata = CreateUsageMetadata() };
                }

                _messages.Add(citationsMessage);
                return [citationsMessage];
            }

            // No citations - regular text message
            var textMessage = new TextMessage
            {
                Text = block.Text,
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                IsThinking = false,
            };

            // Apply usage if available
            if (_usage != null)
            {
                textMessage = textMessage with { Metadata = CreateUsageMetadata() };
            }

            _messages.Add(textMessage);
            return [textMessage];
        }

        // For thinking blocks
        if (block.Type == "thinking" && !string.IsNullOrEmpty(block.Text))
        {
            var thinkingMessage = new TextMessage
            {
                Text = block.Text,
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                IsThinking = true,
            };

            _messages.Add(thinkingMessage);
            return [thinkingMessage];
        }

        return [];
    }

    private List<IMessage> HandleMessageDelta(JsonNode json)
    {
        var delta = json["delta"];
        if (delta == null)
        {
            return [];
        }

        // Check for stop_reason and usage
        var stopReason = delta["stop_reason"]?.GetValue<string>();
        var usage = json["usage"];

        if (usage != null)
        {
            _usage = JsonSerializer.Deserialize<AnthropicUsage>(usage.ToJsonString(), _jsonOptions);

            // Don't proceed if deserialization failed
            if (_usage == null)
            {
                return [];
            }

            // Create a usage message directly instead of an empty TextUpdateMessage with metadata
            var usageMessage = new UsageMessage
            {
                Usage = new Usage
                {
                    PromptTokens = _usage.InputTokens,
                    CompletionTokens = _usage.OutputTokens,
                    TotalTokens = _usage.InputTokens + _usage.OutputTokens,
                },
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
            };

            return [usageMessage];
        }

        return [];
    }

    private static List<IMessage> HandleMessageStop(JsonNode json)
    {
        // We've already handled everything in other events
        return [];
    }

    /// <summary>
    ///     Gets all messages accumulated so far
    /// </summary>
    public List<IMessage> GetAllMessages()
    {
        return [.. _messages];
    }

    private static Role ParseRole(string role)
    {
        return role.ToLower() switch
        {
            "assistant" => Role.Assistant,
            "user" => Role.User,
            "system" => Role.System,
            "tool" => Role.Tool,
            _ => Role.None,
        };
    }

    // Shared helper methods

    /// <summary>
    ///     Creates usage metadata for consistent structure across message types
    /// </summary>
    private ImmutableDictionary<string, object> CreateUsageMetadata()
    {
        return _usage == null
            ? ImmutableDictionary<string, object>.Empty
            : ImmutableDictionary<string, object>.Empty.Add(
                "usage",
                new
                {
                    _usage.InputTokens,
                    _usage.OutputTokens,
                    TotalTokens = _usage.InputTokens + _usage.OutputTokens,
                }
            );
    }

    /// <summary>
    ///     Creates a usage message from the current usage data
    /// </summary>
    private UsageMessage CreateUsageMessage(string? generationId = null)
    {
        return _usage == null
            ? throw new InvalidOperationException("Cannot create usage message without usage data")
            : new UsageMessage
            {
                Usage = new Usage
                {
                    PromptTokens = _usage.InputTokens,
                    CompletionTokens = _usage.OutputTokens,
                    TotalTokens = _usage.InputTokens + _usage.OutputTokens,
                },
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = generationId ?? _messageId,
            };
    }

    /// <summary>
    ///     Handles JSON delta updates, common code for both typed and untyped handlers
    /// </summary>
    private List<IMessage> HandleJsonDelta(StreamingContentBlock block, string partialJson)
    {
        // Skip empty delta
        if (string.IsNullOrEmpty(partialJson))
        {
            return [];
        }

        // Accumulate the partial JSON
        block.JsonAccumulator.AddDelta(partialJson);

        // If we have a tool_use block, generate an update message
        if (block.Type == "tool_use" && !string.IsNullOrEmpty(block.Id))
        {
            // Try to update the block's Input when JSON is complete
            if (block.JsonAccumulator.IsComplete && block.Input == null)
            {
                block.Input = block.JsonAccumulator.GetParsedInput();
            }

            // Create a tool call update with the current partial JSON
            var toolUpdate = new ToolsCallUpdateMessage
            {
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                ToolCallUpdates =
                [
                    new ToolCallUpdate
                    {
                        ToolCallId = block.Id!,
                        Index = block.Index,
                        // Only include FunctionName if it's available and not null
                        FunctionName = !string.IsNullOrEmpty(block.Name) ? block.Name : null,
                        // Include the raw JSON as it's being built
                        FunctionArgs = partialJson,
                    },
                ],
            };

            return [toolUpdate];
        }

        // For server_tool_use blocks, accumulate input but don't emit updates
        // (the final ServerToolUseMessage with input will be emitted at content_block_stop)
        if (block.Type == "server_tool_use")
        {
            if (block.JsonAccumulator.IsComplete && block.Input == null)
            {
                block.Input = block.JsonAccumulator.GetParsedInput();
            }

            return [];
        }

        return [];
    }

    /// <summary>
    ///     Finalizes a tool use block, shared between typed and untyped handlers
    /// </summary>
    private List<IMessage> FinalizeToolUseBlock(StreamingContentBlock block)
    {
        if (string.IsNullOrEmpty(block.Id))
        {
            return [];
        }

        // Final attempt to parse any accumulated JSON
        if (block.Input == null && block.JsonAccumulator.IsComplete)
        {
            block.Input = block.JsonAccumulator.GetParsedInput();
        }

        // Create a final ToolsCallMessage - note that we now allow null Input
        // for tools/functions that don't require arguments
        var toolMessage = CreateToolsCallMessage(block);
        _messages.Add(toolMessage);
        return [toolMessage];
    }

    /// <summary>
    ///     Finalizes a server_tool_use block, emitting the ServerToolUseMessage with
    ///     input from either content_block_start or accumulated input_json_delta events.
    /// </summary>
    private List<IMessage> FinalizeServerToolUseBlock(StreamingContentBlock block)
    {
        // Generate a synthetic ID if none provided (e.g., Kimi doesn't send id for server_tool_use)
        if (string.IsNullOrEmpty(block.Id))
        {
            block.Id = $"srvtoolu_synth_{block.Index}_{Guid.NewGuid():N}";
            _logger.LogDebug(
                "Generated synthetic ID {SyntheticId} for server_tool_use block {Index} (provider did not send id)",
                block.Id,
                block.Index
            );
        }

        if (string.IsNullOrEmpty(block.ToolUseId))
        {
            block.ToolUseId = block.Id;
        }

        // If input_json_delta accumulated a complete object, it overwrites any content_block_start input.
        // Otherwise, block.Input retains whatever was set at content_block_start (or null).
        if (block.JsonAccumulator.IsComplete)
        {
            var parsedInput = block.JsonAccumulator.GetParsedInput();
            if (parsedInput != null)
            {
                block.Input = parsedInput;
            }
            else
            {
                _logger.LogDebug(
                    "JsonAccumulator was complete but returned null for block {Index}. " +
                    "Keeping input from content_block_start.",
                    block.Index
                );
            }
        }

        JsonElement inputElement = default;
        if (block.Input != null)
        {
            try
            {
                inputElement = JsonSerializer.Deserialize<JsonElement>(block.Input.ToJsonString());
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to deserialize input for server_tool_use block {Index} (tool: {ToolName})",
                    block.Index,
                    block.Name ?? "unknown"
                );
                inputElement = default;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to convert input JsonNode for server_tool_use block {Index} (tool: {ToolName}). " +
                    "This may indicate a stale JsonElement from SSE document disposal.",
                    block.Index,
                    block.Name ?? "unknown"
                );
                inputElement = default;
            }
        }

        var serverToolUse = new ServerToolUseMessage
        {
            ToolUseId = block.Id!,
            ToolName = block.Name ?? string.Empty,
            Input = inputElement,
            Role = ParseRole(_role),
            FromAgent = _messageId,
            GenerationId = _messageId,
        };

        _messages.Add(serverToolUse);
        return [serverToolUse];
    }

    /// <summary>
    ///     Creates a ToolsCallMessage from a streaming content block
    /// </summary>
    private ToolsCallMessage CreateToolsCallMessage(StreamingContentBlock block)
    {
        // Handle missing function name - default to empty string if not available
        var functionName = block.Name ?? string.Empty;

        // Handle missing or empty arguments - use empty object instead of empty string
        var functionArgs = "{}";
        if (block.Input != null)
        {
            functionArgs = block.Input.ToJsonString();
        }
        else if (block.JsonAccumulator.IsComplete)
        {
            functionArgs = block.JsonAccumulator.GetRawJson();
            // Ensure we have valid JSON, not an empty string
            if (string.IsNullOrEmpty(functionArgs))
            {
                functionArgs = "{}";
            }
        }

        var message = new ToolsCallMessage
        {
            Role = ParseRole(_role),
            FromAgent = _messageId,
            GenerationId = _messageId,
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = functionName,
                    FunctionArgs = functionArgs,
                    ToolCallId = block.Id ?? string.Empty,
                },
            ],
        };

        // Apply usage metadata if available
        if (_usage != null)
        {
            message = message with { Metadata = CreateUsageMetadata() };
        }

        return message;
    }

    // Typed event handlers

    private List<IMessage> HandleTypedMessageStart(AnthropicMessageStartEvent messageStartEvent)
    {
        if (messageStartEvent.Message == null)
        {
            return [];
        }

        // Store message properties
        _messageId = messageStartEvent.Message.Id;
        _model = messageStartEvent.Message.Model;
        _role = messageStartEvent.Message.Role;

        // Get usage if available
        _usage = messageStartEvent.Message.Usage;

        // No messages to return yet
        return [];
    }

    private List<IMessage> HandleTypedContentBlockStart(AnthropicContentBlockStartEvent contentBlockStartEvent)
    {
        var index = contentBlockStartEvent.Index;
        var contentBlock = contentBlockStartEvent.ContentBlock;

        if (contentBlock == null)
        {
            return [];
        }

        string? id = null;
        string? name = null;
        JsonNode? input = null;

        // Extract properties if it's a tool use block
        if (contentBlock is AnthropicResponseToolUseContent toolUseContent)
        {
            id = toolUseContent.Id;
            name = toolUseContent.Name;
            input =
                toolUseContent.Input.ValueKind != JsonValueKind.Undefined
                    ? JsonNode.Parse(toolUseContent.Input.ToString())
                    : null;
        }
        else if (contentBlock is AnthropicResponseServerToolUseContent serverToolContent)
        {
            id = serverToolContent.Id;
            name = serverToolContent.Name;
            input =
                serverToolContent.Input.ValueKind != JsonValueKind.Undefined
                    ? JsonNode.Parse(serverToolContent.Input.ToString())
                    : null;
        }

        // Create and store the content block
        _contentBlocks[index] = new StreamingContentBlock
        {
            Index = index,
            Type = contentBlock.Type,
            Id = id,
            Name = name,
            Input = input,
        };

        // Check for citations on text blocks
        if (contentBlock is AnthropicResponseTextContent textContent && textContent.Citations != null)
        {
            _contentBlocks[index].Citations = textContent.Citations;
            _logger.LogDebug(
                "Captured {CitationCount} citations from typed content_block_start for block {Index}",
                textContent.Citations.Count,
                index
            );
        }

        // For tool_use blocks, create a ToolsCallUpdateMessage instead of immediately finalizing
        if (contentBlock is AnthropicResponseToolUseContent toolUseTool && !string.IsNullOrEmpty(toolUseTool.Id))
        {
            var toolUpdate = new ToolsCallUpdateMessage
            {
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                ToolCallUpdates =
                [
                    new ToolCallUpdate
                    {
                        ToolCallId = toolUseTool.Id,
                        Index = index,
                        // Only include FunctionName if it's available
                        FunctionName = !string.IsNullOrEmpty(toolUseTool.Name) ? toolUseTool.Name : null,
                        // Only include FunctionArgs if available
                        FunctionArgs =
                            toolUseTool.Input.ValueKind != JsonValueKind.Undefined
                            && toolUseTool.Input.GetPropertyCount() > 0
                                ? toolUseTool.Input.ToString()
                                : "", // Use empty object for no args instead of empty string
                    },
                ],
            };

            return [toolUpdate];
        }

        // Handle server_tool_use blocks (built-in tools like web_search, web_fetch)
        // Don't emit ServerToolUseMessage here — defer to content_block_stop so we can
        // include input accumulated from input_json_delta events.
        if (contentBlock is AnthropicResponseServerToolUseContent serverToolUseContent)
        {
            _contentBlocks[index].ToolUseId = serverToolUseContent.Id;
            // Input was already extracted from serverToolContent.Input and stored in the StreamingContentBlock above
            return [];
        }

        // Handle web_search_tool_result
        if (contentBlock is AnthropicWebSearchToolResultContent webSearchResult)
        {
            var serverToolResult = new ServerToolResultMessage
            {
                ToolUseId = ResolveServerToolUseId(webSearchResult.ToolUseId, "web_search"),
                ToolName = "web_search",
                Result = webSearchResult.Content.ValueKind != JsonValueKind.Undefined
                    ? webSearchResult.Content.Clone()
                    : default,
                IsError = IsServerToolResultError(webSearchResult.Content),
                ErrorCode = GetErrorCodeFromResult(webSearchResult.Content),
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
            };

            _messages.Add(serverToolResult);
            return [serverToolResult];
        }

        // Handle web_fetch_tool_result
        if (contentBlock is AnthropicWebFetchToolResultContent webFetchResult)
        {
            var serverToolResult = new ServerToolResultMessage
            {
                ToolUseId = ResolveServerToolUseId(webFetchResult.ToolUseId, "web_fetch"),
                ToolName = "web_fetch",
                Result = webFetchResult.Content.ValueKind != JsonValueKind.Undefined
                    ? webFetchResult.Content.Clone()
                    : default,
                IsError = IsServerToolResultError(webFetchResult.Content),
                ErrorCode = GetErrorCodeFromResult(webFetchResult.Content),
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
            };

            _messages.Add(serverToolResult);
            return [serverToolResult];
        }

        // Handle bash_code_execution_tool_result
        if (contentBlock is AnthropicBashCodeExecutionToolResultContent bashResult)
        {
            var serverToolResult = new ServerToolResultMessage
            {
                ToolUseId = ResolveServerToolUseId(bashResult.ToolUseId, "bash_code_execution"),
                ToolName = "bash_code_execution",
                Result = bashResult.Content.ValueKind != JsonValueKind.Undefined
                    ? bashResult.Content.Clone()
                    : default,
                IsError = IsServerToolResultError(bashResult.Content),
                ErrorCode = GetErrorCodeFromResult(bashResult.Content),
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
            };

            _messages.Add(serverToolResult);
            return [serverToolResult];
        }

        // Handle text_editor_code_execution_tool_result
        if (contentBlock is AnthropicTextEditorCodeExecutionToolResultContent textEditorResult)
        {
            var serverToolResult = new ServerToolResultMessage
            {
                ToolUseId = ResolveServerToolUseId(textEditorResult.ToolUseId, "text_editor_code_execution"),
                ToolName = "text_editor_code_execution",
                Result = textEditorResult.Content.ValueKind != JsonValueKind.Undefined
                    ? textEditorResult.Content.Clone()
                    : default,
                IsError = IsServerToolResultError(textEditorResult.Content),
                ErrorCode = GetErrorCodeFromResult(textEditorResult.Content),
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
            };

            _messages.Add(serverToolResult);
            return [serverToolResult];
        }

        return [];
    }

    private static bool IsServerToolResultError(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("type", out var typeElement))
        {
            var type = typeElement.GetString();
            return type?.EndsWith("_error") == true;
        }

        return false;
    }

    private static string? GetErrorCodeFromResult(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Object && content.TryGetProperty("error_code", out var errorElement))
        {
            return errorElement.GetString();
        }

        return null;
    }

    private List<IMessage> HandleTypedContentBlockDelta(AnthropicContentBlockDeltaEvent contentBlockDeltaEvent)
    {
        var index = contentBlockDeltaEvent.Index;
        var delta = contentBlockDeltaEvent.Delta;

        if (delta == null)
        {
            return [];
        }

        // Make sure we have a content block for this index
        if (!_contentBlocks.TryGetValue(index, out var block))
        {
            block = new StreamingContentBlock { Index = index, Type = "text" };
            _contentBlocks[index] = block;
        }

        // Handle different delta types
        return delta switch
        {
            AnthropicTextDelta textDelta => HandleTextDelta(block, textDelta),
            AnthropicThinkingDelta thinkingDelta => HandleThinkingDelta(block, thinkingDelta),
            AnthropicInputJsonDelta inputJsonDelta => HandleInputJsonDelta(block, inputJsonDelta),
            AnthropicSignatureDelta signatureDelta => HandleSignatureDelta(block, signatureDelta),
            AnthropicToolCallsDelta toolCallsDelta => HandleToolCallsDelta(toolCallsDelta),
            _ => [],
        };
    }

    private List<IMessage> HandleTextDelta(StreamingContentBlock block, AnthropicTextDelta textDelta)
    {
        block.Text += textDelta.Text;

        // Return a TextUpdateMessage for the delta
        var textUpdate = new TextUpdateMessage
        {
            Text = textDelta.Text,
            Role = ParseRole(_role),
            FromAgent = _messageId,
            GenerationId = _messageId,
            IsThinking = false,
        };

        return [textUpdate];
    }

    private List<IMessage> HandleThinkingDelta(StreamingContentBlock block, AnthropicThinkingDelta thinkingDelta)
    {
        block.Text = thinkingDelta.Thinking; // Replace with latest thinking

        // Return a TextUpdateMessage for the thinking update
        var thinkingUpdate = new TextUpdateMessage
        {
            Text = thinkingDelta.Thinking,
            Role = ParseRole(_role),
            FromAgent = _messageId,
            GenerationId = _messageId,
            IsThinking = true,
        };

        return [thinkingUpdate];
    }

    private List<IMessage> HandleInputJsonDelta(StreamingContentBlock block, AnthropicInputJsonDelta inputJsonDelta)
    {
        return HandleJsonDelta(block, inputJsonDelta.PartialJson);
    }

    private static List<IMessage> HandleSignatureDelta(
        StreamingContentBlock block,
        AnthropicSignatureDelta signatureDelta
    )
    {
        // Store the signature but don't generate a message
        return [];
    }

    private List<IMessage> HandleToolCallsDelta(AnthropicToolCallsDelta toolCallsDelta)
    {
        if (toolCallsDelta.ToolCalls.Count == 0)
        {
            return [];
        }

        var toolCall = toolCallsDelta.ToolCalls[0];
        var toolUpdate = new ToolsCallUpdateMessage
        {
            Role = ParseRole(_role),
            FromAgent = _messageId,
            GenerationId = _messageId,
            ToolCallUpdates =
            [
                new ToolCallUpdate
                {
                    ToolCallId = toolCall.Id,
                    Index = toolCall.Index,
                    // Only include FunctionName if it's non-empty
                    FunctionName = !string.IsNullOrEmpty(toolCall.Name) ? toolCall.Name : null,
                    // Ensure we always provide a valid JSON object, even when args are empty
                    FunctionArgs =
                        toolCall.Input.ValueKind != JsonValueKind.Undefined && toolCall.Input.GetPropertyCount() > 0
                            ? toolCall.Input.ToString()
                            : "",
                },
            ],
        };

        return [toolUpdate];
    }

    private List<IMessage> HandleTypedContentBlockStop(AnthropicContentBlockStopEvent contentBlockStopEvent)
    {
        var index = contentBlockStopEvent.Index;

        // Check if we have a content block for this index
        if (!_contentBlocks.TryGetValue(index, out var block))
        {
            _logger.LogWarning(
                "content_block_stop received for unknown block index {Index}. " +
                "This may indicate a dropped content_block_start event.",
                index
            );
            return [];
        }

        // Handle tool use blocks
        if (block.Type == "tool_use")
        {
            return FinalizeToolUseBlock(block);
        }

        // Handle server_tool_use blocks — emit ServerToolUseMessage with accumulated input from input_json_delta
        if (block.Type == "server_tool_use")
        {
            return FinalizeServerToolUseBlock(block);
        }

        // For text blocks, create a final TextMessage (or TextWithCitationsMessage if citations present)
        if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
        {
            // Check if we have citations - if so, create TextWithCitationsMessage
            if (block.Citations != null && block.Citations.Count > 0)
            {
                var citationsMessage = new TextWithCitationsMessage
                {
                    Text = block.Text,
                    Citations = block.Citations
                        .Select(c => new CitationInfo
                        {
                            Type = c.Type,
                            Url = c.Url,
                            Title = c.Title,
                            CitedText = c.CitedText,
                            StartIndex = c.StartCharIndex,
                            EndIndex = c.EndCharIndex,
                        })
                        .ToImmutableList(),
                    Role = ParseRole(_role),
                    FromAgent = _messageId,
                    GenerationId = _messageId,
                };

                _messages.Add(citationsMessage);
                return [citationsMessage];
            }

            // No citations - regular text message
            var textMessage = new TextMessage
            {
                Text = block.Text,
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                IsThinking = false,
            };

            _messages.Add(textMessage);
            return [textMessage];
        }

        // For thinking blocks, create a final ThinkingMessage
        if (block.Type == "thinking" && !string.IsNullOrEmpty(block.Text))
        {
            var thinkingMessage = new TextMessage
            {
                Text = block.Text,
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                IsThinking = true,
            };

            _messages.Add(thinkingMessage);
            return [thinkingMessage];
        }

        return [];
    }

    private List<IMessage> HandleTypedMessageDelta(AnthropicMessageDeltaEvent messageDeltaEvent)
    {
        // Handle message delta with usage information
        if (messageDeltaEvent.Delta?.StopReason != null && messageDeltaEvent.Usage != null)
        {
            _usage = messageDeltaEvent.Usage;

            // Create a usage message directly
            return [CreateUsageMessage()];
        }

        return [];
    }

    private static List<IMessage> HandleTypedMessageStop()
    {
        // Nothing special to do for message_stop
        return [];
    }

    private static List<IMessage> HandleTypedError(AnthropicErrorEvent errorEvent)
    {
        // Log error and return empty list
        if (errorEvent.Error != null)
        {
            Console.Error.WriteLine($"Anthropic API error: {errorEvent.Error.Type} - {errorEvent.Error.Message}");
        }

        return [];
    }

    /// <summary>
    ///     Helper class for accumulating partial JSON strings during streaming
    /// </summary>
    private class InputJsonAccumulator
    {
        private readonly StringBuilder _jsonBuffer = new();
        private JsonNode? _parsedInput;

        public bool IsComplete => _parsedInput != null;

        public void AddDelta(string partialJson)
        {
            _ = _jsonBuffer.Append(partialJson);

            try
            {
                // Try to parse the accumulated JSON with each new delta
                _parsedInput = JsonNode.Parse(_jsonBuffer.ToString());
            }
            catch
            {
                // Parsing will fail until we have complete, valid JSON
                // That's expected and we continue accumulation
            }
        }

        public JsonNode? GetParsedInput()
        {
            return _parsedInput;
        }

        public string GetRawJson()
        {
            return _jsonBuffer.ToString();
        }
    }

    /// <summary>
    ///     Resolves the tool_use_id for a server tool result block.
    ///     Always prefers the ID from the preceding server_tool_use block to ensure
    ///     consistency between server_tool_use.id and tool_result.tool_use_id in the request.
    ///     Providers like Kimi may provide mismatched IDs between the tool use and result blocks.
    /// </summary>
    private string ResolveServerToolUseId(string toolUseId, string toolName)
    {
        // Always try to find the matching server_tool_use block by tool name,
        // since providers may provide different IDs in the result vs use blocks
        foreach (var block in _contentBlocks.Values)
        {
            if (block.Type == "server_tool_use"
                && block.Name == toolName
                && !string.IsNullOrEmpty(block.ToolUseId)
                && !block.ToolUseIdConsumed)
            {
                block.ToolUseIdConsumed = true;
                if (!string.IsNullOrEmpty(toolUseId) && toolUseId != block.ToolUseId)
                {
                    _logger.LogDebug(
                        "Overriding tool_use_id for {ToolName} result from {OriginalId} to {ResolvedId} to match server_tool_use block {BlockIndex}",
                        toolName,
                        toolUseId,
                        block.ToolUseId,
                        block.Index
                    );
                }
                else if (string.IsNullOrEmpty(toolUseId))
                {
                    _logger.LogDebug(
                        "Resolved empty tool_use_id for {ToolName} result to {ToolUseId} from server_tool_use block {BlockIndex}",
                        toolName,
                        block.ToolUseId,
                        block.Index
                    );
                }

                return block.ToolUseId;
            }
        }

        if (string.IsNullOrEmpty(toolUseId))
        {
            _logger.LogWarning(
                "Could not resolve tool_use_id for {ToolName} result - no matching server_tool_use block found",
                toolName
            );
        }

        return toolUseId;
    }

    /// <summary>
    ///     Helper class to track the state of a content block during streaming
    /// </summary>
    private class StreamingContentBlock
    {
        public int Index { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public JsonNode? Input { get; set; }

        // For server tool results - correlate tool use with result
        public string? ToolUseId { get; set; }

        // Track whether this server_tool_use block's ID has been consumed by a result
        public bool ToolUseIdConsumed { get; set; }

        // For text with citations
        public List<Citation>? Citations { get; set; }

        // For accumulating partial JSON during streaming
        public InputJsonAccumulator JsonAccumulator { get; } = new();
    }
}
