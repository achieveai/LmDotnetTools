using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Prompts;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Prompts;

public class PromptTemplateTests
{
    private readonly IPromptReader _promptReader;

    public PromptTemplateTests()
    {
        // Get the current assembly for embedded resource access
        var assembly = Assembly.GetExecutingAssembly();
        _promptReader = new PromptReader(assembly, "AchieveAi.LmDotnetTools.LmCore.Tests.Prompts.TestPrompts.yaml");
    }

    [Fact]
    public void SinglePrompt_WithTemplates_RendersCorrectly()
    {
        // Arrange
        var promptName = "TemplatePrompt";
        var variables = new Dictionary<string, object>
    {
      { "name", "John" },
      { "company", "AchieveAI" }
    };

        // Act
        var prompt = _promptReader.GetPrompt(promptName);
        var renderedText = prompt.PromptText(variables);

        // Assert
        Assert.Equal("Hello John, welcome to AchieveAI.", renderedText);
    }

    [Fact]
    public void SinglePrompt_WithListTemplate_RendersCorrectly()
    {
        // Arrange
        var promptName = "TemplatePromptWithList";
        var variables = new Dictionary<string, object>
    {
      { "items", new List<string> { "Apple", "Banana", "Cherry" } }
    };

        // Act
        var prompt = _promptReader.GetPrompt(promptName);
        var renderedText = prompt.PromptText(variables);

        // Assert
        // Check for the presence of each item rather than exact formatting
        Assert.Contains("Here are your items:", renderedText);
        Assert.Contains("- Apple", renderedText);
        Assert.Contains("- Banana", renderedText);
        Assert.Contains("- Cherry", renderedText);
    }

    [Fact]
    public void SinglePrompt_WithDictionaryTemplate_RendersCorrectly()
    {
        // Arrange
        var promptName = "TemplatePromptWithDictionary";
        var variables = new Dictionary<string, object>
    {
      {
        "profile",
        new Dictionary<string, object>
        {
          { "Name", "John Doe" },
          { "Age", "30" },
          { "Occupation", "Developer" }
        }
      }
    };

        // Act
        var prompt = _promptReader.GetPrompt(promptName);
        var renderedText = prompt.PromptText(variables);

        // Assert
        // Note: Dictionary order may not be preserved, so we check for specific lines
        Assert.Contains("- Name: John Doe", renderedText);
        Assert.Contains("- Age: 30", renderedText);
        Assert.Contains("- Occupation: Developer", renderedText);
        Assert.StartsWith("User Profile:", renderedText);
    }

    [Fact]
    public void SinglePrompt_WithInvalidVariableName_DoesNotRender()
    {
        // Arrange
        var promptName = "TemplatePrompt";
        var variables = new Dictionary<string, object>
    {
      { "invalidName", "John" },
      { "company", "AchieveAI" }
    };

        // Act
        var prompt = _promptReader.GetPrompt(promptName);
        var renderedText = prompt.PromptText(variables);

        // Assert - with Scriban, missing variables render as empty strings
        Assert.Equal("Hello , welcome to AchieveAI.", renderedText);
    }

    [Fact]
    public void SinglePrompt_WithNoVariables_ReturnsOriginalTemplate()
    {
        // Arrange
        var promptName = "TemplatePrompt";

        // Act
        var prompt = _promptReader.GetPrompt(promptName);
        var renderedText = prompt.PromptText();

        // Assert
        Assert.Equal("Hello {{name}}, welcome to {{company}}.", renderedText);
    }
}
