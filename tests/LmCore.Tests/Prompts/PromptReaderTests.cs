using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Prompts;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Prompts;

public class PromptReaderTests
{
    private readonly IPromptReader _promptReader;

    public PromptReaderTests()
    {
        // Get the current assembly for embedded resource access
        var assembly = Assembly.GetExecutingAssembly();
        _promptReader = new PromptReader(assembly, "AchieveAi.LmDotnetTools.LmCore.Tests.Prompts.TestPrompts.yaml");
    }

    [Fact]
    public void GetPrompt_SimplePrompt_ReturnsCorrectValue()
    {
        // Arrange
        var promptName = "SimplePrompt";

        // Act
        var prompt = _promptReader.GetPrompt(promptName);

        // Assert
        Assert.Equal(promptName, prompt.Name);
        Assert.Equal("latest", prompt.Version);
        Assert.Equal("This is a newer simple prompt.", prompt.Value);
    }

    [Fact]
    public void GetPrompt_SpecificVersion_ReturnsCorrectVersion()
    {
        // Arrange
        var promptName = "SimplePrompt";
        var version = "v1.0";

        // Act
        var prompt = _promptReader.GetPrompt(promptName, version);

        // Assert
        Assert.Equal(promptName, prompt.Name);
        Assert.Equal(version, prompt.Version);
        Assert.Equal("This is a simple prompt.", prompt.Value);
    }

    [Fact]
    public void GetPrompt_NonExistentPrompt_ThrowsKeyNotFoundException()
    {
        // Arrange
        var promptName = "NonExistentPrompt";

        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => _promptReader.GetPrompt(promptName));
    }

    [Fact]
    public void GetPrompt_NonExistentVersion_ThrowsKeyNotFoundException()
    {
        // Arrange
        var promptName = "SimplePrompt";
        var version = "v2.0";

        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => _promptReader.GetPrompt(promptName, version));
    }

    [Fact]
    public void GetPromptChain_ChainPrompt_ReturnsCorrectMessages()
    {
        // Arrange
        var promptName = "ChainPrompt";

        // Act
        var promptChain = _promptReader.GetPromptChain(promptName);

        // Assert
        Assert.Equal(promptName, promptChain.Name);
        Assert.Equal(3, promptChain.Messages.Count);
        Assert.Equal("system", promptChain.Messages[0].Role.ToString().ToLower());
        Assert.Equal("You are a helpful assistant.", ((ICanGetText)promptChain.Messages[0]).GetText());
        Assert.Equal("user", promptChain.Messages[1].Role.ToString().ToLower());
        Assert.Equal("What can you tell me about programming?", ((ICanGetText)promptChain.Messages[1]).GetText());
        Assert.Equal("assistant", promptChain.Messages[2].Role.ToString().ToLower());
        Assert.Equal("Programming is the process of creating a set of instructions for computers.", ((ICanGetText)promptChain.Messages[2]).GetText());
    }

    [Fact]
    public void GetPromptChain_SimplePrompt_ThrowsInvalidOperationException()
    {
        // Arrange
        var promptName = "SimplePrompt";

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _promptReader.GetPromptChain(promptName));
    }
}
