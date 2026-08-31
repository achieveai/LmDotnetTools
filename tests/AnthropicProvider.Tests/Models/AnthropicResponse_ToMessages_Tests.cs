using System.Reflection;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class AnthropicResponse_ToMessages_Tests
{
    private static readonly string[] separator = ["\r\n", "\n"];

    // Gets the path to the repository root directory
    private static string GetRepositoryRootPath()
    {
        // Start from the current assembly's location
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var currentDir = Path.GetDirectoryName(assemblyLocation);

        // Go up to find the repository root (where you'd typically find .git, etc.)
        // This will work even if the test is run from different working directories
        while (
            currentDir != null
            && !Directory.Exists(Path.Combine(currentDir, ".git"))
            && !File.Exists(Path.Combine(currentDir, "LmDotnetTools.sln"))
        )
        {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        return currentDir ?? throw new InvalidOperationException("Could not find repository root");
    }

    private static string GetExampleFilePath(string filename)
    {
        return Path.Combine(GetRepositoryRootPath(), "src", "AnthropicProvider", "Examples", filename);
    }

    [Fact]
    public void NonStreaming_ExampleResponse_ShouldConvertToCorrectMessages()
    {
        // Arrange
        var exampleJson = File.ReadAllText(GetExampleFilePath("example_responses.json"));
        var responses =
            JsonSerializer.Deserialize<AnthropicResponse[]>(
                exampleJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidOperationException("Failed to deserialize example responses");

        // Act & Assert for first response - text and tool_use
        var response1 = responses[0];
        var messages1 = response1.ToMessages("test-agent");

        // Assert basic properties - we now have 3 messages because of the additional UsageMessage
        Assert.Equal(3, messages1.Count);
        Assert.Equal("msg_01E", response1.Id);
        Assert.Equal(Role.Assistant, messages1[0].Role);
        Assert.Equal("test-agent", messages1[0].FromAgent);

        // Verify text message content
        _ = Assert.IsType<TextMessage>(messages1[0]);
        var textMessage = messages1[0] as TextMessage;
        Assert.NotNull(textMessage);
        Assert.Contains("I'll help you list the files in the root and \"code\" directories", textMessage.Text);
        Assert.False(textMessage.IsThinking);

        // Verify tool message content
        _ = Assert.IsType<ToolsCallMessage>(messages1[1]);
        var toolMessage = messages1[1] as ToolsCallMessage;
        Assert.NotNull(toolMessage);
        var toolCalls = toolMessage.GetToolCalls();
        Assert.NotNull(toolCalls);
        var toolCall = toolCalls.First();
        Assert.Equal("python_mcp-list_directory", toolCall.FunctionName);
        Assert.Equal("toolu_018", toolCall.ToolCallId);

        // Verify usage message
        _ = Assert.IsType<UsageMessage>(messages1[2]);
        var usageMessage = messages1[2] as UsageMessage;
        Assert.NotNull(usageMessage);
        Assert.NotNull(usageMessage.Usage);

        // Act & Assert for second response - thinking content
        var response2 = responses[1];
        var messages2 = response2.ToMessages("test-agent");

        // 5 messages: Plain reasoning (text) + Encrypted reasoning (signature) +
        // text + tool_use + usage. The Encrypted companion preserves the thinking
        // signature so the reasoning round-trips back into a valid request.
        Assert.Equal(5, messages2.Count);
        Assert.Equal("msg_016", response2.Id);

        // Verify thinking message content — surfaced as ReasoningMessage (the
        // canonical LmCore type), consistent with the streaming parser.
        _ = Assert.IsType<ReasoningMessage>(messages2[0]);
        var thinkingMessage = messages2[0] as ReasoningMessage;
        Assert.NotNull(thinkingMessage);
        Assert.Contains("The user wants to find files that are in the directory", thinkingMessage.Reasoning);
        Assert.Equal(ReasoningVisibility.Plain, thinkingMessage.Visibility);

        // Verify the signature is preserved as a companion Encrypted ReasoningMessage,
        // mirroring the streaming parser. Without it, round-tripped thinking is unsigned.
        var expectedSignature = (response2.Content[0] as AnthropicResponseThinkingContent)?.Signature;
        Assert.False(string.IsNullOrEmpty(expectedSignature));
        _ = Assert.IsType<ReasoningMessage>(messages2[1]);
        var signatureMessage = messages2[1] as ReasoningMessage;
        Assert.NotNull(signatureMessage);
        Assert.Equal(ReasoningVisibility.Encrypted, signatureMessage.Visibility);
        Assert.Equal(expectedSignature, signatureMessage.Reasoning);

        // Verify regular text message
        _ = Assert.IsType<TextMessage>(messages2[2]);
        var regularTextMessage = messages2[2] as TextMessage;
        Assert.NotNull(regularTextMessage);
        Assert.Contains("I'll help you find the files that are in", regularTextMessage.Text);
        Assert.False(regularTextMessage.IsThinking);

        // Verify tool message
        _ = Assert.IsType<ToolsCallMessage>(messages2[3]);
        var toolMessage2 = messages2[3] as ToolsCallMessage;
        Assert.NotNull(toolMessage2);
        var toolCalls2 = toolMessage2.GetToolCalls();
        Assert.NotNull(toolCalls2);
        var toolCall2 = toolCalls2.First();
        Assert.Equal("python_mcp-execute_python_in_container", toolCall2.FunctionName);
        Assert.Contains("import os", toolCall2.FunctionArgs);

        // Verify usage message
        _ = Assert.IsType<UsageMessage>(messages2[4]);
        var usageMessage2 = messages2[4] as UsageMessage;
        Assert.NotNull(usageMessage2);
        Assert.NotNull(usageMessage2.Usage);
    }

    /// <summary>
    ///     Full round-trip guard for the thinking signature: a non-streaming response
    ///     carrying a signed thinking block must convert to messages and serialize back
    ///     into a request whose assistant thinking block still carries the signature.
    ///     Before the fix, ToMessages dropped the signature, leaving round-tripped
    ///     reasoning unsigned (rejected by providers that validate it, e.g. DeepSeek).
    /// </summary>
    [Fact]
    public void ThinkingSignature_RoundTripsFromResponseIntoRequest()
    {
        // Arrange: the example response whose first content block is a signed thinking block.
        var exampleJson = File.ReadAllText(GetExampleFilePath("example_responses.json"));
        var responses =
            JsonSerializer.Deserialize<AnthropicResponse[]>(
                exampleJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidOperationException("Failed to deserialize example responses");
        var response = responses[1];
        var expectedSignature = (response.Content[0] as AnthropicResponseThinkingContent)?.Signature;
        Assert.False(string.IsNullOrEmpty(expectedSignature));

        // Act: response → messages (assistant turn) → request, prepended with a user turn
        // so the assistant message is valid history.
        var assistantMessages = response.ToMessages("test-agent").Where(m => m is not UsageMessage);
        var history = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Find the files." },
        };
        history.AddRange(assistantMessages);

        var request = AnthropicRequest.FromMessages(
            history,
            new GenerateReplyOptions { ModelId = "deepseek-v4-flash" }
        );

        // Assert: the assistant message's thinking block carries text AND signature
        // (the Plain + Encrypted reasoning pair merged by MergeAdjacentThinkingBlocks).
        var assistantMsg = request.Messages.Single(m => m.Role == "assistant");
        var thinkingBlock = assistantMsg.Content.Single(c => c.Type == "thinking");
        Assert.Contains("The user wants to find files that are in the directory", thinkingBlock.Thinking);
        Assert.Equal(expectedSignature, thinkingBlock.ThinkingSignature);
    }

    [Fact]
    public void Streaming_ExampleResponse_ShouldConvertToCorrectUpdateMessages()
    {
        // Arrange
        var exampleSse = File.ReadAllText(GetExampleFilePath("example_streaming_responses.txt"));
        var sseEvents = ParseSseEvents(exampleSse);

        // Convert SSE events to JSON nodes and text delta objects
        var textDeltas = new List<TextUpdateMessage>();
        var toolUses = new List<string>();

        foreach (var sseEvent in sseEvents)
        {
            if (string.IsNullOrEmpty(sseEvent.Data))
            {
                continue;
            }

            try
            {
                // Parse as JSON object
                var jsonNode = JsonNode.Parse(sseEvent.Data);
                var eventType = jsonNode?["type"]?.GetValue<string>();

                // Check for content_block_delta events with text_delta
                if (eventType == "content_block_delta")
                {
                    var delta = jsonNode?["delta"];
                    var deltaType = delta?["type"]?.GetValue<string>();

                    if (deltaType == "text_delta" && delta?["text"] != null)
                    {
                        // Create a text update message
                        var text = delta["text"]!.GetValue<string>();
                        textDeltas.Add(
                            new TextUpdateMessage
                            {
                                Text = text,
                                Role = Role.Assistant,
                                IsThinking = false,
                            }
                        );
                    }
                }

                // Check for tool_use content blocks
                if (
                    eventType == "content_block_start"
                    && jsonNode?["content_block"]?["type"]?.GetValue<string>() == "tool_use"
                )
                {
                    var toolId = jsonNode["content_block"]?["id"]?.GetValue<string>();
                    var toolName = jsonNode["content_block"]?["name"]?.GetValue<string>();
                    if (toolId != null && toolName != null)
                    {
                        toolUses.Add(toolName);
                    }
                }

                // Check for message_delta events with usage information
                if (
                    eventType == "message_delta"
                    && jsonNode?["delta"]?["stop_reason"] != null
                    && jsonNode?["usage"] != null
                )
                {
                    // This would handle usage information if needed
                }
            }
            catch (JsonException)
            {
                // Skip invalid JSON
            }
        }

        // Act - Get combined text content
        var combinedText = string.Join("", textDeltas.Select(m => m.Text));

        // Assert
        Assert.NotEmpty(textDeltas);
        Assert.Contains("help you list", combinedText);
        Assert.Contains("the files in the root", combinedText);

        // Check for tool use
        Assert.Contains("python_mcp-list_directory", toolUses);

        // Check for message_delta events with stop_reason
        var messageDeltas = sseEvents
            .Where(e => e.Event == "message_delta")
            .Select(e => JsonNode.Parse(e.Data))
            .Where(j => j?["delta"]?["stop_reason"] != null)
            .ToList();

        Assert.NotEmpty(messageDeltas);
        Assert.Equal("tool_use", messageDeltas[0]?["delta"]?["stop_reason"]?.GetValue<string>());
    }

    // Helper method to parse SSE events
    private static List<SseEvent> ParseSseEvents(string input)
    {
        var events = new List<SseEvent>();
        var lines = input.Split(separator, StringSplitOptions.None);

        SseEvent? currentEvent = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                // Empty line indicates end of an event
                if (currentEvent != null)
                {
                    events.Add(currentEvent);
                    currentEvent = null;
                }

                continue;
            }

            currentEvent ??= new SseEvent();

            if (line.StartsWith("event:"))
            {
                currentEvent.Event = line[6..].Trim();
            }
            else if (line.StartsWith("data:"))
            {
                currentEvent.Data = line[5..].Trim();
            }
        }

        // Add the last event if there is one
        if (currentEvent != null)
        {
            events.Add(currentEvent);
        }

        return events;
    }

    [Fact]
    public void NonStreaming_Usage_PreservesCacheReadAndCacheCreationTokens()
    {
        // The non-streaming ToMessages path built a bare Usage from input/output only, dropping the cache
        // read (cached) and cache creation (cache-write) counts — while the streaming parser preserves both.
        // Streaming and non-streaming must agree, so the accounting layer sees the same cache details (#116).
        const string json = """
            {
              "id": "msg_cache",
              "type": "message",
              "role": "assistant",
              "model": "claude-3-7-sonnet",
              "content": [ { "type": "text", "text": "hello" } ],
              "stop_reason": "end_turn",
              "usage": {
                "input_tokens": 1000,
                "output_tokens": 200,
                "cache_read_input_tokens": 800,
                "cache_creation_input_tokens": 300
              }
            }
            """;
        var response =
            JsonSerializer.Deserialize<AnthropicResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidOperationException("Failed to deserialize response");

        var messages = response.ToMessages("test-agent");

        var usageMessage = Assert.IsType<UsageMessage>(messages.Single(m => m is UsageMessage));
        var usage = usageMessage.Usage;
        Assert.NotNull(usage);
        Assert.Equal(1000, usage.PromptTokens);
        Assert.Equal(200, usage.CompletionTokens);
        // Cache-read tokens surface through the nested InputTokenDetails (TotalCachedTokens reads it).
        Assert.Equal(800, usage.TotalCachedTokens);
        // Cache-creation tokens surface as an ExtraProperty, exactly like the streaming parser.
        Assert.Equal(300, usage.GetExtraProperty<int>("cache_creation_input_tokens"));
    }

    // Simple class to represent an SSE event
    private class SseEvent
    {
        public string Event { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }
}
