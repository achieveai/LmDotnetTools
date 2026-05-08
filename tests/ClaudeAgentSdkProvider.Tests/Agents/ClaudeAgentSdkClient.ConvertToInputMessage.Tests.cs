using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

public class ClaudeAgentSdkClientConvertToInputMessageTests
{
    [Fact]
    public void ConvertToInputMessage_SingleTextMessage_ReturnsOneTextBlock()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello, world!", Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert
        Assert.Equal("user", result.Type);
        Assert.Equal("user", result.Message.Role);
        _ = Assert.Single(result.Message.Content);
        var textBlock = Assert.IsType<InputTextContentBlock>(result.Message.Content[0]);
        Assert.Equal("Hello, world!", textBlock.Text);
    }

    [Fact]
    public void ConvertToInputMessage_MultipleTextMessages_CombinesIntoMultipleBlocks()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "First message", Role = Role.User },
            new TextMessage { Text = "Second message", Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert
        Assert.Equal(2, result.Message.Content.Count);

        var block1 = Assert.IsType<InputTextContentBlock>(result.Message.Content[0]);
        Assert.Equal("First message", block1.Text);

        var block2 = Assert.IsType<InputTextContentBlock>(result.Message.Content[1]);
        Assert.Equal("Second message", block2.Text);
    }

    [Fact]
    public void ConvertToInputMessage_TextAndImage_CombinesBothIntoSingleMessage()
    {
        // Arrange
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        var imageData = BinaryData.FromBytes(imageBytes, "image/png");

        var messages = new List<IMessage>
        {
            new TextMessage { Text = "What is in this image?", Role = Role.User },
            new ImageMessage { ImageData = imageData, Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert
        Assert.Equal(2, result.Message.Content.Count);

        // First block should be text
        var textBlock = Assert.IsType<InputTextContentBlock>(result.Message.Content[0]);
        Assert.Equal("What is in this image?", textBlock.Text);

        // Second block should be image
        var imageBlock = Assert.IsType<InputImageContentBlock>(result.Message.Content[1]);
        Assert.Equal("base64", imageBlock.Source.Type);
        Assert.Equal("image/png", imageBlock.Source.MediaType);
        Assert.NotEmpty(imageBlock.Source.Data);
    }

    [Fact]
    public void ConvertToInputMessage_ImageAndText_PreservesOrder()
    {
        // Arrange - image first, then text
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header
        var imageData = BinaryData.FromBytes(imageBytes, "image/jpeg");

        var messages = new List<IMessage>
        {
            new ImageMessage { ImageData = imageData, Role = Role.User },
            new TextMessage { Text = "Describe this", Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert - order should be preserved (image first, then text)
        Assert.Equal(2, result.Message.Content.Count);
        _ = Assert.IsType<InputImageContentBlock>(result.Message.Content[0]);
        _ = Assert.IsType<InputTextContentBlock>(result.Message.Content[1]);
    }

    [Fact]
    public void ConvertToInputMessage_MultipleImages_CombinesAllIntoSingleMessage()
    {
        // Arrange
        var imageData1 = BinaryData.FromBytes([0x89, 0x50, 0x4E, 0x47], "image/png");
        var imageData2 = BinaryData.FromBytes([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg");

        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Compare these images", Role = Role.User },
            new ImageMessage { ImageData = imageData1, Role = Role.User },
            new ImageMessage { ImageData = imageData2, Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert
        Assert.Equal(3, result.Message.Content.Count);
        _ = Assert.IsType<InputTextContentBlock>(result.Message.Content[0]);
        _ = Assert.IsType<InputImageContentBlock>(result.Message.Content[1]);
        _ = Assert.IsType<InputImageContentBlock>(result.Message.Content[2]);
    }

    [Fact]
    public void ConvertToInputMessage_EmptyList_ReturnsEmptyContent()
    {
        // Arrange
        var messages = new List<IMessage>();

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert
        Assert.Empty(result.Message.Content);
    }

    [Fact]
    public void ConvertToInputMessage_IncludesAllTextMessagesRegardlessOfRole()
    {
        // Arrange - ConvertToInputMessage converts all text messages; role filtering is done by caller
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "User question", Role = Role.User },
            new TextMessage { Text = "Assistant response", Role = Role.Assistant },
            new TextMessage { Text = "User follow-up", Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert - all text messages are converted (filtering by role happens in SendMessagesAsync)
        Assert.Equal(3, result.Message.Content.Count);

        var block1 = Assert.IsType<InputTextContentBlock>(result.Message.Content[0]);
        Assert.Equal("User question", block1.Text);

        var block2 = Assert.IsType<InputTextContentBlock>(result.Message.Content[1]);
        Assert.Equal("Assistant response", block2.Text);

        var block3 = Assert.IsType<InputTextContentBlock>(result.Message.Content[2]);
        Assert.Equal("User follow-up", block3.Text);
    }

    [Fact]
    public void ConvertToInputMessage_SkipsEmptyTextMessages()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "", Role = Role.User },
            new TextMessage { Text = "Valid message", Role = Role.User },
            new TextMessage { Text = null!, Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert - only non-empty text should be included
        _ = Assert.Single(result.Message.Content);
        var textBlock = Assert.IsType<InputTextContentBlock>(result.Message.Content[0]);
        Assert.Equal("Valid message", textBlock.Text);
    }

    [Fact]
    public void ConvertToInputMessage_ImageWithDefaultMediaType_UsesJpeg()
    {
        // Arrange - image without explicit media type
        var imageData = BinaryData.FromBytes([0x00, 0x01, 0x02, 0x03]);

        var messages = new List<IMessage>
        {
            new ImageMessage { ImageData = imageData, Role = Role.User },
        };

        // Act
        var result = ClaudeAgentSdkClient.ConvertToInputMessage(messages);

        // Assert
        var imageBlock = Assert.IsType<InputImageContentBlock>(result.Message.Content[0]);
        Assert.Equal("image/png", imageBlock.Source.MediaType);
    }
}
