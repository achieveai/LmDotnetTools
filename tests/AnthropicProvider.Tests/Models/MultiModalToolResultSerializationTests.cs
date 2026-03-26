namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     Tests that tool_result messages with multimodal ContentBlocks (text + images)
///     are serialized correctly in the Anthropic API request format.
///     Text-only results must remain backward compatible (string format).
/// </summary>
public class MultiModalToolResultSerializationTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    /// <summary>
    ///     Text-only tool_result (no ContentBlocks) must produce "content": "text" (string format).
    ///     This verifies backward compatibility after the Content -> ContentRaw refactor.
    /// </summary>
    [Fact]
    public void FromMessages_TextOnlyToolResult_ProducesStringContent()
    {
        var messages = CreateToolCallAndResultMessages(
            toolCallId: "toolu_01ABC",
            resultText: "The answer is 42.",
            contentBlocks: null);

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // The tool_result is in the second user message (first is the original question)
        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var toolResult = userMsg.GetProperty("content")[0];

        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_01ABC", toolResult.GetProperty("tool_use_id").GetString());

        // Content must be a string, not an array
        var content = toolResult.GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("The answer is 42.", content.GetString());
    }

    /// <summary>
    ///     Multimodal tool_result with images must produce "content": [blocks...] (array format).
    /// </summary>
    [Fact]
    public void FromMessages_MultiModalToolResult_ProducesArrayContent()
    {
        var contentBlocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "Here is an image:" },
            new ImageToolResultBlock
            {
                Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
                MimeType = "image/png",
            },
        };

        var messages = CreateToolCallAndResultMessages(
            toolCallId: "toolu_01DEF",
            resultText: "Here is an image:",
            contentBlocks: contentBlocks);

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // The tool_result is in the second user message (first is the original question)
        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var toolResult = userMsg.GetProperty("content")[0];

        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_01DEF", toolResult.GetProperty("tool_use_id").GetString());

        // Content must be an array with text and image blocks
        var content = toolResult.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());

        // First block: text
        var textBlock = content[0];
        Assert.Equal("text", textBlock.GetProperty("type").GetString());
        Assert.Equal("Here is an image:", textBlock.GetProperty("text").GetString());

        // Second block: image with base64 source
        var imageBlock = content[1];
        Assert.Equal("image", imageBlock.GetProperty("type").GetString());
        var source = imageBlock.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.StartsWith("iVBOR", source.GetProperty("data").GetString());
    }

    /// <summary>
    ///     ContentBlocks with text only (no images) must fall back to string format
    ///     for backward compatibility and smaller payload.
    /// </summary>
    [Fact]
    public void FromMessages_TextOnlyContentBlocks_FallsBackToStringFormat()
    {
        var contentBlocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "Just text, no images." },
        };

        var messages = CreateToolCallAndResultMessages(
            toolCallId: "toolu_01GHI",
            resultText: "Just text, no images.",
            contentBlocks: contentBlocks);

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // The tool_result is in the second user message (first is the original question)
        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var toolResult = userMsg.GetProperty("content")[0];

        // Must be string format since there are no images
        var content = toolResult.GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("Just text, no images.", content.GetString());
    }

    /// <summary>
    ///     Error tool results with ContentBlocks must always use string format
    ///     (Anthropic API convention for error results).
    /// </summary>
    [Fact]
    public void FromMessages_ErrorToolResultWithContentBlocks_UsesStringFormat()
    {
        var contentBlocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "Error occurred" },
            new ImageToolResultBlock { Data = "base64data", MimeType = "image/png" },
        };

        var toolCall = new ToolCall
        {
            FunctionName = "test_tool",
            FunctionArgs = "{}",
            ToolCallId = "toolu_01ERR",
            ToolCallIdx = 0,
        };

        var errorResult = new ToolCallResult("toolu_01ERR", "Error: something went wrong", contentBlocks)
        {
            IsError = true,
        };

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Do something" },
            new ToolsCallAggregateMessage(
                new ToolsCallMessage { ToolCalls = [toolCall], GenerationId = "gen1" },
                new ToolsCallResultMessage { ToolCallResults = [errorResult], GenerationId = "gen1" },
                "assistant"),
        };

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var toolResult = userMsg.GetProperty("content")[0];

        // Must be string format even though ContentBlocks has images,
        // because IsError is true
        var content = toolResult.GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("Error: something went wrong", content.GetString());
    }

    /// <summary>
    ///     ToolsCallResultMessage with mixed results (some with images, some text-only)
    ///     produces the correct format for each result independently.
    /// </summary>
    [Fact]
    public void FromMessages_MixedToolResults_EachUsesCorrectFormat()
    {
        var imageBlocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "Result with image" },
            new ImageToolResultBlock { Data = "base64data", MimeType = "image/jpeg" },
        };

        var toolCall1 = new ToolCall
        {
            FunctionName = "tool_a",
            FunctionArgs = "{}",
            ToolCallId = "toolu_01A",
            ToolCallIdx = 0,
        };

        var toolCall2 = new ToolCall
        {
            FunctionName = "tool_b",
            FunctionArgs = "{}",
            ToolCallId = "toolu_01B",
            ToolCallIdx = 1,
        };

        var result1 = new ToolCallResult("toolu_01A", "Text only result");
        var result2 = new ToolCallResult("toolu_01B", "Result with image", imageBlocks);

        // Use ToolsCallResultMessage directly (plural form with multiple results)
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Do two things" },
            new ToolsCallAggregateMessage(
                new ToolsCallMessage { ToolCalls = [toolCall1, toolCall2], GenerationId = "gen1" },
                new ToolsCallResultMessage
                {
                    ToolCallResults = [result1, result2],
                    GenerationId = "gen1",
                },
                "assistant"),
        };

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // The user message should have two tool_result content blocks
        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var resultContents = userMsg.GetProperty("content");
        Assert.Equal(2, resultContents.GetArrayLength());

        // First result: text-only (string format)
        var firstResult = resultContents[0];
        Assert.Equal("tool_result", firstResult.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, firstResult.GetProperty("content").ValueKind);

        // Second result: multimodal (array format)
        var secondResult = resultContents[1];
        Assert.Equal("tool_result", secondResult.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Array, secondResult.GetProperty("content").ValueKind);
    }

    /// <summary>
    ///     Multimodal tool_result in a CompositeMessage with server tool results
    ///     is serialized correctly.
    /// </summary>
    [Fact]
    public void FromMessages_CompositeWithServerToolMultiModal_ProducesArrayContent()
    {
        var contentBlocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "Server tool result" },
            new ImageToolResultBlock { Data = "base64data", MimeType = "image/png" },
        };

        var composite = new CompositeMessage
        {
            Messages =
            [
                new TextMessage { Role = Role.Assistant, Text = "Let me use the server tool." },
                new ToolCallResultMessage
                {
                    ToolCallId = "toolu_srv_01",
                    Result = "Server tool result",
                    ContentBlocks = contentBlocks,
                    ExecutionTarget = ExecutionTarget.ProviderServer,
                    Role = Role.User,
                },
            ],
            Role = Role.Assistant,
            GenerationId = "gen1",
        };

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Use server tool" },
            composite,
        };

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // The secondary (user) message should have the server tool result
        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var toolResult = userMsg.GetProperty("content")[0];

        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        var content = toolResult.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());
    }

    /// <summary>
    ///     Multimodal content blocks in the tool_result array must not contain
    ///     extraneous null fields (e.g., "thinking", "source" on text blocks).
    /// </summary>
    [Fact]
    public void FromMessages_MultiModalToolResult_ContentBlocksOmitNullFields()
    {
        var contentBlocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "Some text" },
            new ImageToolResultBlock { Data = "imgdata", MimeType = "image/png" },
        };

        var messages = CreateToolCallAndResultMessages(
            toolCallId: "toolu_01CLEAN",
            resultText: "Some text",
            contentBlocks: contentBlocks);

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // The tool_result is in the second user message (first is the original question)
        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var content = userMsg.GetProperty("content")[0].GetProperty("content");

        // Text block should not have "source", "thinking", "signature" etc.
        var textBlock = content[0];
        Assert.False(textBlock.TryGetProperty("source", out _), "Text block should not have 'source'");
        Assert.False(textBlock.TryGetProperty("thinking", out _), "Text block should not have 'thinking'");
        Assert.False(textBlock.TryGetProperty("signature", out _), "Text block should not have 'signature'");

        // Image block should not have "text", "thinking" etc.
        var imageBlock = content[1];
        Assert.False(imageBlock.TryGetProperty("text", out _), "Image block should not have 'text'");
        Assert.False(imageBlock.TryGetProperty("thinking", out _), "Image block should not have 'thinking'");
    }

    /// <summary>
    ///     The in-memory Content property on AnthropicContent remains accessible
    ///     even though it is [JsonIgnore]d for serialization (backward compat for tests).
    /// </summary>
    [Fact]
    public void AnthropicContent_ContentProperty_StillReadableInMemory()
    {
        var content = new AnthropicContent
        {
            Type = "tool_result",
            ToolUseId = "toolu_01",
            Content = "test result",
        };

        // The backing property is still accessible in code
        Assert.Equal("test result", content.Content);

        // But when serialized, "content" comes from ContentRaw
        var json = JsonSerializer.Serialize(content, _jsonOptions);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("test result", doc.RootElement.GetProperty("content").GetString());
    }

    /// <summary>
    ///     When ToolResultContentBlocks is set, ContentRaw serializes as array;
    ///     when Content is set, ContentRaw serializes as string.
    ///     When both are null, "content" is omitted.
    /// </summary>
    [Fact]
    public void AnthropicContent_ContentRaw_CorrectlySelects_StringOrArray()
    {
        // Case 1: String content
        var stringContent = new AnthropicContent
        {
            Type = "tool_result",
            ToolUseId = "toolu_01",
            Content = "text result",
        };
        var json1 = JsonSerializer.Serialize(stringContent, _jsonOptions);
        var doc1 = JsonDocument.Parse(json1);
        Assert.Equal(JsonValueKind.String, doc1.RootElement.GetProperty("content").ValueKind);

        // Case 2: Array content (ToolResultContentBlocks)
        var arrayContent = new AnthropicContent
        {
            Type = "tool_result",
            ToolUseId = "toolu_02",
            ToolResultContentBlocks =
            [
                new AnthropicContent { Type = "text", Text = "hello" },
            ],
        };
        var json2 = JsonSerializer.Serialize(arrayContent, _jsonOptions);
        var doc2 = JsonDocument.Parse(json2);
        Assert.Equal(JsonValueKind.Array, doc2.RootElement.GetProperty("content").ValueKind);

        // Case 3: Both null -> content omitted
        var nullContent = new AnthropicContent
        {
            Type = "tool_result",
            ToolUseId = "toolu_03",
        };
        var json3 = JsonSerializer.Serialize(nullContent, _jsonOptions);
        var doc3 = JsonDocument.Parse(json3);
        Assert.False(doc3.RootElement.TryGetProperty("content", out _));
    }

    /// <summary>
    ///     A standalone (singular) ToolCallResultMessage with image ContentBlocks
    ///     must produce "content": [blocks...] (array format) in the serialized tool_result.
    ///     This verifies the path where ToolCallResultMessage is NOT wrapped in
    ///     ToolsCallAggregateMessage but sent as a separate IMessage.
    /// </summary>
    [Fact]
    public void FromMessages_StandaloneSingularToolCallResultMessage_WithImages_ProducesArrayContent()
    {
        var contentBlocks = new List<ToolResultContentBlock>
        {
            new TextToolResultBlock { Text = "Here is the captured screenshot:" },
            new ImageToolResultBlock
            {
                Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
                MimeType = "image/png",
            },
        };

        // Build message list with a standalone (non-aggregate) ToolCallResultMessage.
        // The assistant tool_use must precede the user tool_result for Anthropic to accept it.
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Take a screenshot" },
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "screenshot_tool",
                        FunctionArgs = "{}",
                        ToolCallId = "toolu_01STANDALONE",
                        ToolCallIdx = 0,
                    },
                ],
                Role = Role.Assistant,
                GenerationId = "gen_standalone",
            },
            new ToolCallResultMessage
            {
                ToolCallId = "toolu_01STANDALONE",
                Result = "Here is the captured screenshot:",
                ContentBlocks = contentBlocks,
                Role = Role.User,
            },
        };

        var request = AnthropicRequest.FromMessages(messages);
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var doc = JsonDocument.Parse(json);

        // The user message (after the assistant tool_use) should contain the tool_result
        var userMsg = GetMessageByRole(doc, "user", skip: 1);
        var toolResult = userMsg.GetProperty("content")[0];

        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_01STANDALONE", toolResult.GetProperty("tool_use_id").GetString());

        // Content must be an array with text and image blocks (because images are present)
        var content = toolResult.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(2, content.GetArrayLength());

        // First block: text
        var textBlock = content[0];
        Assert.Equal("text", textBlock.GetProperty("type").GetString());
        Assert.Equal("Here is the captured screenshot:", textBlock.GetProperty("text").GetString());

        // Second block: image with base64 source
        var imageBlock = content[1];
        Assert.Equal("image", imageBlock.GetProperty("type").GetString());
        var source = imageBlock.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.StartsWith("iVBOR", source.GetProperty("data").GetString());
    }

    #region Helpers

    /// <summary>
    ///     Creates a standard tool call + result message pair using ToolsCallAggregateMessage.
    /// </summary>
    private static IMessage[] CreateToolCallAndResultMessages(
        string toolCallId,
        string resultText,
        IList<ToolResultContentBlock>? contentBlocks)
    {
        var toolCall = new ToolCall
        {
            FunctionName = "test_tool",
            FunctionArgs = "{\"q\":\"test\"}",
            ToolCallId = toolCallId,
            ToolCallIdx = 0,
        };

        var toolResult = new ToolCallResult(toolCallId, resultText, contentBlocks);

        return
        [
            new TextMessage { Role = Role.User, Text = "Test question" },
            new ToolsCallAggregateMessage(
                new ToolsCallMessage { ToolCalls = [toolCall], GenerationId = "gen1" },
                new ToolsCallResultMessage { ToolCallResults = [toolResult], GenerationId = "gen1" },
                "assistant"),
        ];
    }

    /// <summary>
    ///     Gets a message element by role from the parsed JSON document.
    ///     Use skip to get the Nth occurrence (0-based).
    /// </summary>
    private static JsonElement GetMessageByRole(JsonDocument doc, string role, int skip = 0)
    {
        var messages = doc.RootElement.GetProperty("messages");
        var count = 0;
        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.GetProperty("role").GetString() == role)
            {
                if (count == skip)
                {
                    return msg;
                }

                count++;
            }
        }

        throw new InvalidOperationException(
            $"Could not find message with role '{role}' (skip={skip}) in request JSON");
    }

    #endregion
}
