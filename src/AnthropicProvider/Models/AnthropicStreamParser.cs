using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
/// Helper class for parsing Anthropic SSE stream events into IMessage objects
/// </summary>
public class AnthropicStreamParser
{
    private readonly List<IMessage> _messages = new();
    private readonly Dictionary<int, StreamingContentBlock> _contentBlocks = new();
    private string _messageId = string.Empty;
    private string _model = string.Empty;
    private string _role = "assistant";
    private AnthropicUsage? _usage;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new instance of the AnthropicStreamParser
    /// </summary>
    public AnthropicStreamParser()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// Processes a raw SSE event string and returns any resulting IMessage updates
    /// </summary>
    /// <param name="eventType">The SSE event type</param>
    /// <param name="data">The SSE event data (JSON string)</param>
    /// <returns>A list of message updates (empty if none produced by this event)</returns>
    public List<IMessage> ProcessEvent(string eventType, string data)
    {
        if (string.IsNullOrEmpty(data))
            return new List<IMessage>();

        try
        {
            // Parse the JSON data
            var json = JsonNode.Parse(data);
            if (json == null)
                return new List<IMessage>();

            var eventTypeFromJson = json["type"]?.GetValue<string>();
            if (string.IsNullOrEmpty(eventTypeFromJson))
                return new List<IMessage>();

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
                    return new List<IMessage>(); // Ignore ping events
                default:
                    Console.Error.WriteLine($"Unknown event type: {eventTypeFromJson}");
                    return new List<IMessage>();
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error parsing SSE data: {ex.Message}");
            return new List<IMessage>();
        }
    }

    /// <summary>
    /// Processes a strongly-typed AnthropicStreamEvent and returns any resulting IMessage updates
    /// </summary>
    /// <param name="streamEvent">The strongly-typed stream event</param>
    /// <returns>A list of message updates (empty if none produced by this event)</returns>
    public List<IMessage> ProcessStreamEvent(AnthropicStreamEvent streamEvent)
    {
        return streamEvent switch
        {
            AnthropicMessageStartEvent messageStartEvent => HandleTypedMessageStart(messageStartEvent),
            AnthropicContentBlockStartEvent contentBlockStartEvent => HandleTypedContentBlockStart(contentBlockStartEvent),
            AnthropicContentBlockDeltaEvent contentBlockDeltaEvent => HandleTypedContentBlockDelta(contentBlockDeltaEvent),
            AnthropicContentBlockStopEvent contentBlockStopEvent => HandleTypedContentBlockStop(contentBlockStopEvent),
            AnthropicMessageDeltaEvent messageDeltaEvent => HandleTypedMessageDelta(messageDeltaEvent),
            AnthropicMessageStopEvent => HandleTypedMessageStop(),
            AnthropicPingEvent => new List<IMessage>(), // Ignore ping events
            AnthropicErrorEvent errorEvent => HandleTypedError(errorEvent),
            _ => new List<IMessage>() // Unknown event type
        };
    }

    private List<IMessage> HandleMessageStart(JsonNode json)
    {
        var message = json["message"];
        if (message == null)
            return new List<IMessage>();

        // Store message properties
        _messageId = message["id"]?.GetValue<string>() ?? string.Empty;
        _model = message["model"]?.GetValue<string>() ?? string.Empty;
        _role = message["role"]?.GetValue<string>() ?? "assistant";

        // Get usage if available
        if (message["usage"] != null)
        {
            _usage = JsonSerializer.Deserialize<AnthropicUsage>(
                message["usage"]!.ToJsonString(),
                _jsonOptions);
        }

        // No messages to return yet
        return new List<IMessage>();
    }

    private List<IMessage> HandleContentBlockStart(JsonNode json)
    {
        var index = json["index"]?.GetValue<int>() ?? 0;
        var contentBlock = json["content_block"];
        if (contentBlock == null)
            return new List<IMessage>();

        var blockType = contentBlock["type"]?.GetValue<string>() ?? string.Empty;

        // Create and store the content block
        _contentBlocks[index] = new StreamingContentBlock
        {
            Index = index,
            Type = blockType,
            Id = contentBlock["id"]?.GetValue<string>(),
            Name = contentBlock["name"]?.GetValue<string>(),
            Input = contentBlock["input"]?.AsObject()
        };

        // For tool_use blocks, create a ToolsCallUpdateMessage instead of immediately finalizing
        if (blockType == "tool_use" && !string.IsNullOrEmpty(_contentBlocks[index].Id))
        {
            var input = contentBlock["input"]?.AsObject();
            var toolUpdate = new ToolsCallUpdateMessage
            {
                Role = ParseRole(_role),
                FromAgent = _messageId, 
                GenerationId = _messageId,
                ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
                {
                    ToolCallId = _contentBlocks[index].Id!,
                    Index = index,
                    // Only include FunctionName if it's available
                    FunctionName = !string.IsNullOrEmpty(_contentBlocks[index].Name) ? _contentBlocks[index].Name : null,
                    // Only include FunctionArgs if Input is available
                    FunctionArgs = input != null && input.Count > 0 ? input.ToJsonString() : null
                })
            };
            
            return new List<IMessage> { toolUpdate };
        }

        return new List<IMessage>();
    }

    private List<IMessage> HandleContentBlockDelta(JsonNode json)
    {
        var index = json["index"]?.GetValue<int>() ?? 0;
        var delta = json["delta"];
        if (delta == null)
            return new List<IMessage>();

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
                        IsThinking = false
                    };

                    return new List<IMessage> { textUpdate };
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
                        IsThinking = true
                    };

                    return new List<IMessage> { thinkingUpdate };
                }

            case "input_json_delta":
                {
                    return HandleJsonDelta(block, delta["partial_json"]?.GetValue<string>() ?? string.Empty);
                }
        }

        return new List<IMessage>();
    }

    private List<IMessage> HandleContentBlockStop(JsonNode json)
    {
        var index = json["index"]?.GetValue<int>() ?? 0;

        // Check if we have a content block for this index
        if (!_contentBlocks.TryGetValue(index, out var block))
            return new List<IMessage>();

        // Handle tool use blocks
        if (block.Type == "tool_use")
        {
            return FinalizeToolUseBlock(block);
        }

        // For text blocks, create a final TextMessage
        if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
        {
            var textMessage = new TextMessage
            {
                Text = block.Text,
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                IsThinking = false
            };

            // Apply usage if available
            if (_usage != null)
            {
                textMessage = textMessage with
                {
                    Metadata = CreateUsageMetadata()
                };
            }

            _messages.Add(textMessage);
            return new List<IMessage> { textMessage };
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
                IsThinking = true
            };

            _messages.Add(thinkingMessage);
            return new List<IMessage> { thinkingMessage };
        }

        return new List<IMessage>();
    }

    private List<IMessage> HandleMessageDelta(JsonNode json)
    {
        var delta = json["delta"];
        if (delta == null)
            return new List<IMessage>();

        // Check for stop_reason and usage
        var stopReason = delta["stop_reason"]?.GetValue<string>();
        var usage = json["usage"];
        
        if (usage != null)
        {
            _usage = JsonSerializer.Deserialize<AnthropicUsage>(
                usage.ToJsonString(),
                _jsonOptions);
            
            // Create a usage update message
            var usageUpdate = new TextUpdateMessage
            {
                Text = string.Empty,
                Role = ParseRole(_role),
                Metadata = CreateUsageMetadata()
            };
            
            return new List<IMessage> { usageUpdate };
        }

        return new List<IMessage>();
    }

    private List<IMessage> HandleMessageStop(JsonNode json)
    {
        // We've already handled everything in other events
        return new List<IMessage>();
    }

    /// <summary>
    /// Gets all messages accumulated so far
    /// </summary>
    public List<IMessage> GetAllMessages()
    {
        return _messages.ToList();
    }

    private static Role ParseRole(string role)
    {
        return role.ToLower() switch
        {
            "assistant" => Role.Assistant,
            "user" => Role.User,
            "system" => Role.System,
            "tool" => Role.Tool,
            _ => Role.None
        };
    }

    // Shared helper methods

    /// <summary>
    /// Creates usage metadata for consistent structure across message types
    /// </summary>
    private ImmutableDictionary<string, object> CreateUsageMetadata()
    {
        if (_usage == null)
            return ImmutableDictionary<string, object>.Empty;
            
        return ImmutableDictionary<string, object>.Empty
            .Add("usage", new
            {
                InputTokens = _usage.InputTokens,
                OutputTokens = _usage.OutputTokens,
                TotalTokens = _usage.InputTokens + _usage.OutputTokens
            });
    }

    /// <summary>
    /// Handles JSON delta updates, common code for both typed and untyped handlers
    /// </summary>
    private List<IMessage> HandleJsonDelta(StreamingContentBlock block, string partialJson)
    {
        // Skip empty delta
        if (string.IsNullOrEmpty(partialJson))
            return new List<IMessage>();
        
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
                ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
                {
                    ToolCallId = block.Id!,
                    Index = block.Index,
                    // Only include FunctionName if it's available and not null
                    FunctionName = !string.IsNullOrEmpty(block.Name) ? block.Name : null,
                    // Include the raw JSON as it's being built
                    FunctionArgs = partialJson
                })
            };
            
            return new List<IMessage> { toolUpdate };
        }
        
        return new List<IMessage>();
    }

    /// <summary>
    /// Finalizes a tool use block, shared between typed and untyped handlers
    /// </summary>
    private List<IMessage> FinalizeToolUseBlock(StreamingContentBlock block)
    {
        if (string.IsNullOrEmpty(block.Id))
            return new List<IMessage>();
            
        // Final attempt to parse any accumulated JSON
        if (block.Input == null && block.JsonAccumulator.IsComplete)
        {
            block.Input = block.JsonAccumulator.GetParsedInput();
        }
        
        // Create a final ToolsCallMessage - note that we now allow null Input
        // for tools/functions that don't require arguments
        var toolMessage = CreateToolsCallMessage(block);
        _messages.Add(toolMessage);
        return new List<IMessage> { toolMessage };
    }

    /// <summary>
    /// Creates a ToolsCallMessage from a streaming content block
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
            ToolCalls = ImmutableList.Create(new ToolCall(
                functionName,
                functionArgs
            ) { ToolCallId = block.Id ?? string.Empty })
        };

        // Apply usage metadata if available
        if (_usage != null)
        {
            message = message with { Metadata = CreateUsageMetadata() };
        }

        return message;
    }

    /// <summary>
    /// Helper class for accumulating partial JSON strings during streaming
    /// </summary>
    private class InputJsonAccumulator
    {
        private readonly StringBuilder _jsonBuffer = new();
        private JsonNode? _parsedInput;
        
        public void AddDelta(string partialJson)
        {
            _jsonBuffer.Append(partialJson);
            
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
        
        public bool IsComplete => _parsedInput != null;
        
        public JsonNode? GetParsedInput() => _parsedInput;
        
        public string GetRawJson() => _jsonBuffer.ToString();
    }

    /// <summary>
    /// Helper class to track the state of a content block during streaming
    /// </summary>
    private class StreamingContentBlock
    {
        public int Index { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public JsonNode? Input { get; set; }
        
        // For accumulating partial JSON during streaming
        public InputJsonAccumulator JsonAccumulator { get; } = new();
    }

    // Typed event handlers

    private List<IMessage> HandleTypedMessageStart(AnthropicMessageStartEvent messageStartEvent)
    {
        if (messageStartEvent.Message == null)
            return new List<IMessage>();

        // Store message properties
        _messageId = messageStartEvent.Message.Id;
        _model = messageStartEvent.Message.Model;
        _role = messageStartEvent.Message.Role;

        // Get usage if available
        _usage = messageStartEvent.Message.Usage;

        // No messages to return yet
        return new List<IMessage>();
    }

    private List<IMessage> HandleTypedContentBlockStart(AnthropicContentBlockStartEvent contentBlockStartEvent)
    {
        var index = contentBlockStartEvent.Index;
        var contentBlock = contentBlockStartEvent.ContentBlock;
        
        if (contentBlock == null)
            return new List<IMessage>();

        string? id = null;
        string? name = null;
        JsonNode? input = null;

        // Extract properties if it's a tool use block
        if (contentBlock is AnthropicResponseToolUseContent toolUseContent)
        {
            id = toolUseContent.Id;
            name = toolUseContent.Name;
            input = toolUseContent.Input.ValueKind != JsonValueKind.Undefined ? 
                    JsonNode.Parse(toolUseContent.Input.ToString()) : null;
        }

        // Create and store the content block
        _contentBlocks[index] = new StreamingContentBlock
        {
            Index = index,
            Type = contentBlock.Type,
            Id = id,
            Name = name,
            Input = input
        };

        // For tool_use blocks, create a ToolsCallUpdateMessage instead of immediately finalizing
        if (contentBlock is AnthropicResponseToolUseContent toolUseTool && 
            !string.IsNullOrEmpty(toolUseTool.Id))
        {
            var toolUpdate = new ToolsCallUpdateMessage
            {
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
                {
                    ToolCallId = toolUseTool.Id,
                    Index = index,
                    // Only include FunctionName if it's available
                    FunctionName = !string.IsNullOrEmpty(toolUseTool.Name) ? toolUseTool.Name : null,
                    // Only include FunctionArgs if available
                    FunctionArgs = toolUseTool.Input.ValueKind != JsonValueKind.Undefined && toolUseTool.Input.GetPropertyCount() > 0 ? 
                                  toolUseTool.Input.ToString() : 
                                  "" // Use empty object for no args instead of empty string
                })
            };
            
            return new List<IMessage> { toolUpdate };
        }

        return new List<IMessage>();
    }

    private List<IMessage> HandleTypedContentBlockDelta(AnthropicContentBlockDeltaEvent contentBlockDeltaEvent)
    {
        var index = contentBlockDeltaEvent.Index;
        var delta = contentBlockDeltaEvent.Delta;
        
        if (delta == null)
            return new List<IMessage>();

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
            _ => new List<IMessage>()
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
            IsThinking = false
        };

        return new List<IMessage> { textUpdate };
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
            IsThinking = true
        };

        return new List<IMessage> { thinkingUpdate };
    }

    private List<IMessage> HandleInputJsonDelta(StreamingContentBlock block, AnthropicInputJsonDelta inputJsonDelta)
    {
        return HandleJsonDelta(block, inputJsonDelta.PartialJson);
    }

    private List<IMessage> HandleSignatureDelta(StreamingContentBlock block, AnthropicSignatureDelta signatureDelta)
    {
        // Store the signature but don't generate a message
        return new List<IMessage>();
    }

    private List<IMessage> HandleToolCallsDelta(AnthropicToolCallsDelta toolCallsDelta)
    {
        if (toolCallsDelta.ToolCalls.Count == 0)
            return new List<IMessage>();

        var toolCall = toolCallsDelta.ToolCalls[0];
        var toolUpdate = new ToolsCallUpdateMessage
        {
            Role = ParseRole(_role),
            FromAgent = _messageId,
            GenerationId = _messageId,
            ToolCallUpdates = ImmutableList.Create(new ToolCallUpdate
            {
                ToolCallId = toolCall.Id,
                Index = toolCall.Index,
                // Only include FunctionName if it's non-empty
                FunctionName = !string.IsNullOrEmpty(toolCall.Name) ? toolCall.Name : null,
                // Ensure we always provide a valid JSON object, even when args are empty
                FunctionArgs = toolCall.Input.ValueKind != JsonValueKind.Undefined && toolCall.Input.GetPropertyCount() > 0 ? 
                              toolCall.Input.ToString() : 
                              ""
            })
        };

        return new List<IMessage> { toolUpdate };
    }

    private List<IMessage> HandleTypedContentBlockStop(AnthropicContentBlockStopEvent contentBlockStopEvent)
    {
        var index = contentBlockStopEvent.Index;

        // Check if we have a content block for this index
        if (!_contentBlocks.TryGetValue(index, out var block))
            return new List<IMessage>();

        // Handle tool use blocks
        if (block.Type == "tool_use")
        {
            return FinalizeToolUseBlock(block);
        }

        // For text blocks, create a final TextMessage
        if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
        {
            var textMessage = new TextMessage
            {
                Text = block.Text,
                Role = ParseRole(_role),
                FromAgent = _messageId,
                GenerationId = _messageId,
                IsThinking = false
            };

            _messages.Add(textMessage);
            return new List<IMessage> { textMessage };
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
                IsThinking = true
            };

            _messages.Add(thinkingMessage);
            return new List<IMessage> { thinkingMessage };
        }

        return new List<IMessage>();
    }

    private List<IMessage> HandleTypedMessageDelta(AnthropicMessageDeltaEvent messageDeltaEvent)
    {
        // Handle message delta with usage information
        if (messageDeltaEvent.Delta?.StopReason != null && messageDeltaEvent.Usage != null)
        {
            _usage = messageDeltaEvent.Usage;
            
            // Update the last message with usage information if we have any messages
            if (_messages.Count > 0)
            {
                var lastMessage = _messages[_messages.Count - 1];
                var usageMetadata = CreateUsageMetadata();

                // Update the last message with metadata
                if (lastMessage is TextMessage textMessage)
                {
                    _messages[_messages.Count - 1] = textMessage with { Metadata = usageMetadata };
                    return new List<IMessage> { textMessage with { Metadata = usageMetadata } };
                }
                else if (lastMessage is ToolsCallMessage toolsCallMessage)
                {
                    _messages[_messages.Count - 1] = toolsCallMessage with { Metadata = usageMetadata };
                    return new List<IMessage> { toolsCallMessage with { Metadata = usageMetadata } };
                }
            }
            
            // If no messages yet, return an empty update with usage information
            return new List<IMessage>
            {
                new TextUpdateMessage
                {
                    Text = string.Empty,
                    Role = ParseRole(_role),
                    FromAgent = _messageId,
                    GenerationId = _messageId,
                    Metadata = CreateUsageMetadata()
                }
            };
        }
        
        return new List<IMessage>();
    }

    private List<IMessage> HandleTypedMessageStop()
    {
        // Nothing special to do for message_stop
        return new List<IMessage>();
    }

    private List<IMessage> HandleTypedError(AnthropicErrorEvent errorEvent)
    {
        // Log error and return empty list
        if (errorEvent.Error != null)
        {
            Console.Error.WriteLine($"Anthropic API error: {errorEvent.Error.Type} - {errorEvent.Error.Message}");
        }
        return new List<IMessage>();
    }
} 