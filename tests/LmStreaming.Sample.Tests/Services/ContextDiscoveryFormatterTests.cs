using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the <c>&lt;context-discovery&gt;</c> wrapper shape that both the boot-time system-prompt
/// seed (<see cref="ContextDiscoveryFormatter.BuildSystemPromptBlock"/>) and the mid-session
/// injected user turn (<see cref="ContextDiscoveryFormatter.BuildInjectedMessage"/>) emit. The
/// model parses this tag, so the unit tests double as the contract.
/// </summary>
public class ContextDiscoveryFormatterTests
{
    private readonly ContextDiscoveryFormatter _formatter = new();

    [Fact]
    public void BuildInjectedMessage_WrapsBodyWithPathAttribute()
    {
        var rendered = _formatter.BuildInjectedMessage("CLAUDE.md", "Project rules.", truncated: false);

        rendered.Should().Be("<context-discovery path=\"CLAUDE.md\">\nProject rules.\n</context-discovery>");
    }

    [Fact]
    public void BuildInjectedMessage_AddsTruncatedAttribute_WhenFlagged()
    {
        var rendered = _formatter.BuildInjectedMessage("AGENTS.md", "Body.", truncated: true);

        rendered.Should().Be("<context-discovery path=\"AGENTS.md\" truncated=\"true\">\nBody.\n</context-discovery>");
    }

    [Fact]
    public void BuildInjectedMessage_PreservesTrailingNewlineWithoutDoubling()
    {
        var rendered = _formatter.BuildInjectedMessage("CLAUDE.md", "Body.\n", truncated: false);

        rendered.Should().Be("<context-discovery path=\"CLAUDE.md\">\nBody.\n</context-discovery>");
    }

    [Fact]
    public void BuildInjectedMessage_EscapesXmlSpecialsInPath()
    {
        var rendered = _formatter.BuildInjectedMessage("dir\"quote/&amp.md", "body", truncated: false);

        // The closing tag is never affected by path content; the path goes only inside the
        // attribute. Escaping must keep the attribute parseable.
        rendered.Should().StartWith("<context-discovery path=\"dir&quot;quote/&amp;amp.md\">\n");
    }

    [Fact]
    public void BuildInjectedMessage_NullOrEmptyContent_Throws()
    {
        var act = () => _formatter.BuildInjectedMessage("CLAUDE.md", string.Empty, truncated: false);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildSystemPromptBlock_NullOrEmptyContent_ReturnsEmpty()
    {
        _formatter.BuildSystemPromptBlock("CLAUDE.md", null, truncated: false).Should().BeEmpty();
        _formatter.BuildSystemPromptBlock("CLAUDE.md", string.Empty, truncated: false).Should().BeEmpty();
    }

    [Fact]
    public void BuildSystemPromptBlock_MatchesInjectedMessage_Output_ForSameInputs()
    {
        // Boot-time seed and mid-session injection MUST render byte-identical, so a context file
        // delivered after the first turn looks the same to the model as one seeded at boot.
        var boot = _formatter.BuildSystemPromptBlock("CLAUDE.md", "Rules.", truncated: true);
        var mid = _formatter.BuildInjectedMessage("CLAUDE.md", "Rules.", truncated: true);

        boot.Should().Be(mid);
    }
}
