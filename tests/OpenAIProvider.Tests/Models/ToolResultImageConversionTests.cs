using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models;

/// <summary>
/// Tests that tool results with image ContentBlocks are converted to
/// an additional user message containing those images, since OpenAI
/// does not support images in tool_result messages.
/// </summary>
public class ToolResultImageConversionTests
{
    [Fact]
    public void FromMessage_ToolResultWithImages_AppendsUserImageMessage()
    {
        // Arrange: a tool result with text + image content blocks
        var toolResult = new ToolCallResult(
            "call_123",
            "Here is the search result",
            [
                new TextToolResultBlock { Text = "Some text result" },
                new ImageToolResultBlock
                {
                    Data = "iVBORw0KGgo=",
                    MimeType = "image/png",
                },
            ]);

        var message = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResult],
        };

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(message).ToList();

        // Assert: should have 1 tool message + 1 user image message
        Assert.Equal(2, chatMessages.Count);

        // First message is the tool result (text only)
        Assert.Equal(RoleEnum.Tool, chatMessages[0].Role);
        Assert.Equal("call_123", chatMessages[0].ToolCallId);
        Assert.Equal(
            "Here is the search result",
            chatMessages[0].Content!.Get<string>());

        // Second message is a user message with the image
        Assert.Equal(RoleEnum.User, chatMessages[1].Role);
        var contentArray = chatMessages[1].Content!.Get<Union<TextContent, ImageContent>[]>();
        Assert.Single(contentArray);

        var imageContent = contentArray[0].Get<ImageContent>();
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", imageContent.Url.Url);
    }

    [Fact]
    public void FromMessage_ToolResultWithoutImages_NoExtraMessage()
    {
        // Arrange: a tool result with only text (no image ContentBlocks)
        var toolResult = new ToolCallResult("call_456", "Plain text result");

        var message = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResult],
        };

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(message).ToList();

        // Assert: should have exactly 1 tool message, no user image message
        Assert.Single(chatMessages);
        Assert.Equal(RoleEnum.Tool, chatMessages[0].Role);
        Assert.Equal("call_456", chatMessages[0].ToolCallId);
    }

    [Fact]
    public void FromMessage_ToolResultWithTextOnlyContentBlocks_NoExtraMessage()
    {
        // Arrange: ContentBlocks present but no images, only text blocks
        var toolResult = new ToolCallResult(
            "call_789",
            "Text result",
            [new TextToolResultBlock { Text = "Just text, no images" }]);

        var message = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResult],
        };

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(message).ToList();

        // Assert: no images means no extra user message
        Assert.Single(chatMessages);
        Assert.Equal(RoleEnum.Tool, chatMessages[0].Role);
    }

    [Fact]
    public void FromMessage_MultipleToolResultsWithImages_SingleAggregatedUserMessage()
    {
        // Arrange: two tool results, each with an image
        var toolResult1 = new ToolCallResult(
            "call_a",
            "Result A",
            [
                new ImageToolResultBlock
                {
                    Data = "AAAA",
                    MimeType = "image/jpeg",
                },
            ]);

        var toolResult2 = new ToolCallResult(
            "call_b",
            "Result B",
            [
                new ImageToolResultBlock
                {
                    Data = "BBBB",
                    MimeType = "image/png",
                },
            ]);

        var message = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResult1, toolResult2],
        };

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(message).ToList();

        // Assert: 2 tool messages + 1 user message with 2 images
        Assert.Equal(3, chatMessages.Count);

        Assert.Equal(RoleEnum.Tool, chatMessages[0].Role);
        Assert.Equal(RoleEnum.Tool, chatMessages[1].Role);
        Assert.Equal(RoleEnum.User, chatMessages[2].Role);

        var contentArray = chatMessages[2].Content!.Get<Union<TextContent, ImageContent>[]>();
        Assert.Equal(2, contentArray.Length);

        var img1 = contentArray[0].Get<ImageContent>();
        Assert.Equal("data:image/jpeg;base64,AAAA", img1.Url.Url);

        var img2 = contentArray[1].Get<ImageContent>();
        Assert.Equal("data:image/png;base64,BBBB", img2.Url.Url);
    }

    [Fact]
    public void FromMessage_MixedToolResults_OnlyImageResultsContributeToUserMessage()
    {
        // Arrange: one tool result with image, one without
        var toolResultWithImage = new ToolCallResult(
            "call_img",
            "Image result",
            [
                new ImageToolResultBlock
                {
                    Data = "CCCC",
                    MimeType = "image/webp",
                },
            ]);

        var toolResultTextOnly = new ToolCallResult("call_txt", "Text only result");

        var message = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResultWithImage, toolResultTextOnly],
        };

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(message).ToList();

        // Assert: 2 tool messages + 1 user message with 1 image
        Assert.Equal(3, chatMessages.Count);

        Assert.Equal(RoleEnum.Tool, chatMessages[0].Role);
        Assert.Equal(RoleEnum.Tool, chatMessages[1].Role);
        Assert.Equal(RoleEnum.User, chatMessages[2].Role);

        var contentArray = chatMessages[2].Content!.Get<Union<TextContent, ImageContent>[]>();
        Assert.Single(contentArray);

        var img = contentArray[0].Get<ImageContent>();
        Assert.Equal("data:image/webp;base64,CCCC", img.Url.Url);
    }

    [Fact]
    public void FromMessage_ToolsCallAggregateWithImages_ImageMessageAfterToolResults()
    {
        // Arrange: a ToolsCallAggregateMessage wrapping tool calls and results with images
        var toolCall = new ToolCall
        {
            FunctionName = "search",
            FunctionArgs = "{}",
            ToolCallId = "call_agg",
        };

        var toolsCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            ToolCalls = [toolCall],
        };

        var toolResult = new ToolCallResult(
            "call_agg",
            "Search result",
            [
                new ImageToolResultBlock
                {
                    Data = "DDDD",
                    MimeType = "image/png",
                },
            ]);

        var toolsCallResult = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResult],
        };

        var aggregate = new ToolsCallAggregateMessage(toolsCallMessage, toolsCallResult);

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(aggregate).ToList();

        // Assert: 1 assistant (tool_calls) + 1 tool (result) + 1 user (image)
        Assert.Equal(3, chatMessages.Count);
        Assert.Equal(RoleEnum.Assistant, chatMessages[0].Role);
        Assert.Equal(RoleEnum.Tool, chatMessages[1].Role);
        Assert.Equal(RoleEnum.User, chatMessages[2].Role);

        var contentArray = chatMessages[2].Content!.Get<Union<TextContent, ImageContent>[]>();
        Assert.Single(contentArray);
        Assert.Equal(
            "data:image/png;base64,DDDD",
            contentArray[0].Get<ImageContent>().Url.Url);
    }

    [Fact]
    public void FromMessage_NullContentBlocks_NoExtraMessage()
    {
        // Arrange: ContentBlocks explicitly null
        var toolResult = new ToolCallResult("call_null", "Result")
        {
            ContentBlocks = null,
        };

        var message = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResult],
        };

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(message).ToList();

        // Assert: just the tool message, no extra user message
        Assert.Single(chatMessages);
        Assert.Equal(RoleEnum.Tool, chatMessages[0].Role);
    }

    /// <summary>
    /// An empty ContentBlocks list (not null) should not produce an extra user message,
    /// since there are no images to extract.
    /// </summary>
    [Fact]
    public void FromMessage_EmptyContentBlocksList_NoExtraMessage()
    {
        // Arrange: ContentBlocks is an empty list (distinct from null)
        var toolResult = new ToolCallResult("call_empty", "Empty blocks result")
        {
            ContentBlocks = [],
        };

        var message = new ToolsCallResultMessage
        {
            ToolCallResults = [toolResult],
        };

        // Act
        var chatMessages = ChatCompletionRequest.FromMessage(message).ToList();

        // Assert: just the tool message, no extra user message
        Assert.Single(chatMessages);
        Assert.Equal(RoleEnum.Tool, chatMessages[0].Role);
        Assert.Equal("call_empty", chatMessages[0].ToolCallId);
    }

    /// <summary>
    /// Full integration test via FromMessages: a standalone ToolsCallMessage (assistant)
    /// followed by a ToolsCallResultMessage (with images) produces three ChatMessages
    /// in order: [assistant(tool_calls), tool(result), user(images)].
    /// </summary>
    [Fact]
    public void FromMessages_StandaloneToolResultWithImages_UserImageMessageAppearsInOrder()
    {
        // Arrange: assistant tool call + tool result with an image
        var toolCallMessage = new ToolsCallMessage
        {
            Role = Role.Assistant,
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "capture_screen",
                    FunctionArgs = "{}",
                    ToolCallId = "call_screen1",
                },
            ],
        };

        var toolResultMessage = new ToolsCallResultMessage
        {
            ToolCallResults =
            [
                new ToolCallResult(
                    "call_screen1",
                    "Screenshot captured",
                    [
                        new TextToolResultBlock { Text = "Screenshot of the page" },
                        new ImageToolResultBlock
                        {
                            Data = "AAAA",
                            MimeType = "image/png",
                        },
                    ]),
            ],
        };

        var messages = new IMessage[] { toolCallMessage, toolResultMessage };

        // Act: use the full FromMessages pipeline (not just FromMessage)
        var request = ChatCompletionRequest.FromMessages(messages, null, "gpt-4");

        // Assert: messages in order: assistant(tool_calls), tool(result), user(images)
        Assert.Equal(3, request.Messages.Count);

        // First: assistant with tool_calls
        Assert.Equal(RoleEnum.Assistant, request.Messages[0].Role);
        Assert.NotNull(request.Messages[0].ToolCalls);
        Assert.Single(request.Messages[0].ToolCalls!);
        Assert.Equal("capture_screen", request.Messages[0].ToolCalls![0].Function.Name);

        // Second: tool result
        Assert.Equal(RoleEnum.Tool, request.Messages[1].Role);
        Assert.Equal("call_screen1", request.Messages[1].ToolCallId);
        Assert.Equal("Screenshot captured", request.Messages[1].Content!.Get<string>());

        // Third: user message with images extracted from ContentBlocks
        Assert.Equal(RoleEnum.User, request.Messages[2].Role);
        var contentArray = request.Messages[2].Content!.Get<Union<TextContent, ImageContent>[]>();
        Assert.Single(contentArray);

        var imageContent = contentArray[0].Get<ImageContent>();
        Assert.Equal("data:image/png;base64,AAAA", imageContent.Url.Url);
    }
}
