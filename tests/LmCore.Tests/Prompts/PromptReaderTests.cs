using System.Reflection;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Prompts;

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
        _ = Assert.Throws<KeyNotFoundException>(() => _promptReader.GetPrompt(promptName));
    }

    [Fact]
    public void GetPrompt_NonExistentVersion_ThrowsKeyNotFoundException()
    {
        // Arrange
        var promptName = "SimplePrompt";
        var version = "v2.0";

        // Act & Assert
        _ = Assert.Throws<KeyNotFoundException>(() => _promptReader.GetPrompt(promptName, version));
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
        Assert.Equal(
            "Programming is the process of creating a set of instructions for computers.",
            ((ICanGetText)promptChain.Messages[2]).GetText()
        );
    }

    [Fact]
    public void GetPromptChain_SimplePrompt_ThrowsInvalidOperationException()
    {
        // Arrange
        var promptName = "SimplePrompt";

        // Act & Assert
        _ = Assert.Throws<InvalidOperationException>(() => _promptReader.GetPromptChain(promptName));
    }

    [Fact]
    public void Constructor_PromptGroupWithAllNonVersionedKeys_DoesNotThrow_ExactKeyWorks_LatestNotFound()
    {
        // Arrange - a prompt group whose keys are all non-version strings, e.g. a domain-context
        // lookup table keyed by exam name rather than "vX.Y" (real-world shape: ExamContext
        // prompts keyed "NeetUG"/"NeetPG"). Before the FindLatestVersion guard in
        // PromptReader.ParseYamlFile, this crashed the constructor with KeyNotFoundException from
        // indexing versions[""], because FindLatestVersion returns "" when no key parses via
        // Version.TryParse.
        const string yaml = """
            ExamContext:
              NeetUG: "NEET UG context text."
              NeetPG: "NEET PG context text."
            """;

        // Act
        PromptReader? reader = null;
        var constructionException = Record.Exception(() =>
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(yaml));
            reader = new PromptReader(stream);
        });

        // Assert - construction must not throw
        Assert.Null(constructionException);
        Assert.NotNull(reader);

        // Exact-key lookup (the only supported lookup for a non-versioned group) works
        var prompt = reader!.GetPrompt("ExamContext", "NeetUG");
        Assert.Equal("NeetUG", prompt.Version);
        Assert.Equal("NEET UG context text.", prompt.Value);

        // "latest" was never registered for this group (guard skipped it), so requesting it
        // via the default version parameter throws KeyNotFoundException rather than silently
        // resolving to an arbitrary entry.
        _ = Assert.Throws<KeyNotFoundException>(() => reader.GetPrompt("ExamContext"));
    }
}
