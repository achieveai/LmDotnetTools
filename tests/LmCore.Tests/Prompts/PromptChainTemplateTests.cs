using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Prompts;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Prompts;

public class PromptChainTemplateTests
{
    private readonly IPromptReader _promptReader;

    public PromptChainTemplateTests()
    {
        // Get the current assembly for embedded resource access
        var assembly = Assembly.GetExecutingAssembly();
        _promptReader = new PromptReader(
            assembly,
            "AchieveAi.LmDotnetTools.LmCore.Tests.Prompts.TestPrompts.yaml"
        );
    }

    [Fact]
    public void PromptChain_WithTemplates_RendersCorrectly()
    {
        // Arrange
        var promptName = "TemplateChainPrompt";
        var variables = new Dictionary<string, object>
        {
            { "name", "John" },
            { "company", "AchieveAI" },
            { "topic", "machine learning" },
        };

        // Act
        var promptChain = _promptReader.GetPromptChain(promptName);
        var renderedMessages = promptChain.PromptMessages(variables);

        // Assert
        Assert.Equal(3, renderedMessages.Count);
        Assert.Equal("system", renderedMessages[0].Role.ToString().ToLower());
        Assert.Contains(
            "You are a helpful assistant for",
            ((ICanGetText)renderedMessages[0]).GetText()
        );
        Assert.Contains("AchieveAI", ((ICanGetText)renderedMessages[0]).GetText());
        Assert.Equal("user", renderedMessages[1].Role.ToString().ToLower());
        Assert.Contains("Hello, I'm John", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains(
            "What can you tell me about machine learning?",
            ((ICanGetText)renderedMessages[1]).GetText()
        );
        Assert.Equal("assistant", renderedMessages[2].Role.ToString().ToLower());
        Assert.Contains("Hello John", ((ICanGetText)renderedMessages[2]).GetText());
        Assert.Contains(
            "here's what I know about machine learning",
            ((ICanGetText)renderedMessages[2]).GetText()
        );
    }

    [Fact]
    public void PromptChain_WithListTemplate_RendersCorrectly()
    {
        // Arrange
        var promptName = "TemplateChainPromptWithList";
        var variables = new Dictionary<string, object>
        {
            {
                "interests",
                new List<string> { "AI", "Programming", "Science" }
            },
        };

        // Act
        var promptChain = _promptReader.GetPromptChain(promptName);
        var renderedMessages = promptChain.PromptMessages(variables);

        // Assert
        Assert.Equal(3, renderedMessages.Count);

        // Check system message
        Assert.Equal("system", renderedMessages[0].Role.ToString().ToLower());
        Assert.Equal("You are a helpful assistant.", ((ICanGetText)renderedMessages[0]).GetText());

        // Check user message with rendered list
        Assert.Equal("user", renderedMessages[1].Role.ToString().ToLower());
        Assert.Contains("Here are my interests:", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains("- AI", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains("- Programming", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains("- Science", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains(
            "Can you recommend something based on these?",
            ((ICanGetText)renderedMessages[1]).GetText()
        );

        // Check assistant message with joined interests
        Assert.Equal("assistant", renderedMessages[2].Role.ToString().ToLower());
        Assert.Contains(
            "Based on your interests in AI, Programming, Science",
            ((ICanGetText)renderedMessages[2]).GetText()
        );
    }

    [Fact]
    public void PromptChain_WithDictionaryTemplate_RendersCorrectly()
    {
        // Arrange
        var promptName = "TemplateChainPromptWithDictionary";
        var variables = new Dictionary<string, object>
        {
            { "company", "AchieveAI" },
            {
                "profile",
                new Dictionary<string, object>
                {
                    { "Name", "Jane Smith" },
                    { "Age", "28" },
                    { "Interests", "Technology" },
                }
            },
        };

        // Act
        var promptChain = _promptReader.GetPromptChain(promptName);
        var renderedMessages = promptChain.PromptMessages(variables);

        // Assert
        Assert.Equal(3, renderedMessages.Count);

        // Check system message
        Assert.Equal("system", renderedMessages[0].Role.ToString().ToLower());
        Assert.Contains(
            "You are a helpful assistant for",
            ((ICanGetText)renderedMessages[0]).GetText()
        );
        Assert.Contains("AchieveAI", ((ICanGetText)renderedMessages[0]).GetText());

        // Check user message with rendered dictionary
        Assert.Equal("user", renderedMessages[1].Role.ToString().ToLower());
        Assert.Contains("Here is my profile:", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains("- Name: Jane Smith", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains("- Age: 28", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains("- Interests: Technology", ((ICanGetText)renderedMessages[1]).GetText());
        Assert.Contains(
            "What can you suggest for me?",
            ((ICanGetText)renderedMessages[1]).GetText()
        );

        // Check assistant message
        Assert.Equal("assistant", renderedMessages[2].Role.ToString().ToLower());
        Assert.Contains("Based on your profile", ((ICanGetText)renderedMessages[2]).GetText());
        Assert.Contains("I suggest", ((ICanGetText)renderedMessages[2]).GetText());
    }

    [Fact]
    public void PromptChain_WithNoVariables_ReturnsOriginalTemplates()
    {
        // Arrange
        var promptName = "TemplateChainPrompt";

        // Act
        var promptChain = _promptReader.GetPromptChain(promptName);
        var messages = promptChain.PromptMessages();

        // Assert
        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0].Role.ToString().ToLower());
        Assert.Contains(
            "You are a helpful assistant for {{company}}",
            ((ICanGetText)messages[0]).GetText()
        );
        Assert.Equal("user", messages[1].Role.ToString().ToLower());
        Assert.Contains("Hello, I'm {{name}}", ((ICanGetText)messages[1]).GetText());
        Assert.Contains(
            "What can you tell me about {{topic}}?",
            ((ICanGetText)messages[1]).GetText()
        );
        Assert.Equal("assistant", messages[2].Role.ToString().ToLower());
        Assert.Contains("Hello {{name}}", ((ICanGetText)messages[2]).GetText());
        Assert.Contains("here's what I know about {{topic}}", ((ICanGetText)messages[2]).GetText());
    }

    [Fact]
    public void PromptChain_PromptTextMethod_ThrowsNotSupportedException()
    {
        // Arrange
        var promptName = "ChainPrompt";
        var promptChain = _promptReader.GetPromptChain(promptName);

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => promptChain.PromptText());
    }
}
