using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Prompts;

public class PromptChainTemplateTests
{
  private readonly IPromptReader _promptReader;

  public PromptChainTemplateTests()
  {
    // Get the current assembly for embedded resource access
    var assembly = Assembly.GetExecutingAssembly();
    _promptReader = new PromptReader(assembly, "AchieveAi.LmDotnetTools.LmCore.Tests.Prompts.TestPrompts.yaml");
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
      { "topic", "machine learning" }
    };

    // Act
    var promptChain = _promptReader.GetPromptChain(promptName);
    var renderedMessages = promptChain.PromptMessages(variables);

    // Assert
    Assert.Equal(3, renderedMessages.Count);
    Assert.Equal("system", renderedMessages[0].Role);
    Assert.Contains("You are a helpful assistant for", renderedMessages[0].Content);
    Assert.Contains("AchieveAI", renderedMessages[0].Content);
    Assert.Equal("user", renderedMessages[1].Role);
    Assert.Contains("Hello, I'm John", renderedMessages[1].Content);
    Assert.Contains("What can you tell me about machine learning?", renderedMessages[1].Content);
    Assert.Equal("assistant", renderedMessages[2].Role);
    Assert.Contains("Hello John", renderedMessages[2].Content);
    Assert.Contains("here's what I know about machine learning", renderedMessages[2].Content);
  }

  [Fact]
  public void PromptChain_WithListTemplate_RendersCorrectly()
  {
    // Arrange
    var promptName = "TemplateChainPromptWithList";
    var variables = new Dictionary<string, object>
    {
      { "interests", new List<string> { "AI", "Programming", "Science" } }
    };

    // Act
    var promptChain = _promptReader.GetPromptChain(promptName);
    var renderedMessages = promptChain.PromptMessages(variables);

    // Assert
    Assert.Equal(3, renderedMessages.Count);
    
    // Check system message
    Assert.Equal("system", renderedMessages[0].Role);
    Assert.Equal("You are a helpful assistant.", renderedMessages[0].Content);
    
    // Check user message with rendered list
    Assert.Equal("user", renderedMessages[1].Role);
    Assert.Contains("Here are my interests:", renderedMessages[1].Content);
    Assert.Contains("- AI", renderedMessages[1].Content);
    Assert.Contains("- Programming", renderedMessages[1].Content);
    Assert.Contains("- Science", renderedMessages[1].Content);
    Assert.Contains("Can you recommend something based on these?", renderedMessages[1].Content);
    
    // Check assistant message with joined interests
    Assert.Equal("assistant", renderedMessages[2].Role);
    Assert.Contains("Based on your interests in AI, Programming, Science", renderedMessages[2].Content);
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
          { "Interests", "Technology" }
        }
      }
    };

    // Act
    var promptChain = _promptReader.GetPromptChain(promptName);
    var renderedMessages = promptChain.PromptMessages(variables);

    // Assert
    Assert.Equal(3, renderedMessages.Count);
    
    // Check system message
    Assert.Equal("system", renderedMessages[0].Role);
    Assert.Contains("You are a helpful assistant for", renderedMessages[0].Content);
    Assert.Contains("AchieveAI", renderedMessages[0].Content);
    
    // Check user message with rendered dictionary
    Assert.Equal("user", renderedMessages[1].Role);
    Assert.Contains("Here is my profile:", renderedMessages[1].Content);
    Assert.Contains("- Name: Jane Smith", renderedMessages[1].Content);
    Assert.Contains("- Age: 28", renderedMessages[1].Content);
    Assert.Contains("- Interests: Technology", renderedMessages[1].Content);
    Assert.Contains("What can you suggest for me?", renderedMessages[1].Content);
    
    // Check assistant message
    Assert.Equal("assistant", renderedMessages[2].Role);
    Assert.Contains("Based on your profile", renderedMessages[2].Content);
    Assert.Contains("I suggest", renderedMessages[2].Content);
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
    Assert.Equal("system", messages[0].Role);
    Assert.Contains("You are a helpful assistant for {{company}}", messages[0].Content);
    Assert.Equal("user", messages[1].Role);
    Assert.Contains("Hello, I'm {{name}}", messages[1].Content);
    Assert.Contains("What can you tell me about {{topic}}?", messages[1].Content);
    Assert.Equal("assistant", messages[2].Role);
    Assert.Contains("Hello {{name}}", messages[2].Content);
    Assert.Contains("here's what I know about {{topic}}", messages[2].Content);
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
