using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Unit tests for <see cref="SubAgentMarkdownParser.Parse"/>. The parser is pure: it returns a
/// <see cref="ParsedSubAgent"/> or <c>null</c> for malformed input. These tests pin the
/// frontmatter contract, the unknown-key tolerance, and the fallback name resolution.
/// </summary>
public class SubAgentMarkdownParserTests
{
    [Fact]
    public void Parse_WellFormed_ExposesAllFields()
    {
        var md =
            "---\nname: echo-agent\ndescription: Echo discovered marker.\nmodel: claude-sonnet-4-5\ntools:\n  - Read\n  - Glob\n---\nYou are the echo sub-agent.";

        var parsed = SubAgentMarkdownParser.Parse(md, "fallback");

        parsed.Should().NotBeNull();
        parsed!.Name.Should().Be("echo-agent");
        parsed.Description.Should().Be("Echo discovered marker.");
        parsed.Model.Should().Be("claude-sonnet-4-5");
        parsed.Tools.Should().Equal("Read", "Glob");
        parsed.SystemPrompt.Should().Be("You are the echo sub-agent.");
    }

    [Fact]
    public void Parse_NoFrontmatterFence_ReturnsNull()
    {
        var md = "name: echo-agent\nYou are the echo sub-agent.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().BeNull();
    }

    [Fact]
    public void Parse_MissingClosingFence_ReturnsNull()
    {
        var md = "---\nname: echo-agent\nYou are the echo sub-agent.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsNull()
    {
        var md = "---\nname: echo-agent\n---\n";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().BeNull();
    }

    [Fact]
    public void Parse_MissingName_FallsBackToFilenameStem()
    {
        var md = "---\ndescription: No name in YAML.\n---\nSystem prompt.";

        var parsed = SubAgentMarkdownParser.Parse(md, "stem-name");

        parsed.Should().NotBeNull();
        parsed!.Name.Should().Be("stem-name");
        parsed.Description.Should().Be("No name in YAML.");
    }

    [Fact]
    public void Parse_MissingNameAndStem_ReturnsNull()
    {
        var md = "---\ndescription: Nothing.\n---\nBody.";

        var parsed = SubAgentMarkdownParser.Parse(md, string.Empty);

        parsed.Should().BeNull();
    }

    [Fact]
    public void Parse_UnknownFrontmatterKeys_AreIgnored()
    {
        // Verifies IgnoreUnmatchedProperties — additions in the gateway's frontmatter contract
        // must not break older builds.
        var md = "---\nname: echo-agent\ndescription: Echo.\ntags: [demo, test]\nfuture_field: 42\n---\nBody.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().NotBeNull();
        parsed!.Name.Should().Be("echo-agent");
    }

    [Fact]
    public void Parse_MalformedYaml_ReturnsNull()
    {
        var md = "---\nname: [unterminated\n---\nBody.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyToolsList_IsEmpty_NotNull()
    {
        // Distinct from "absent" — empty means "no tools at all".
        var md = "---\nname: echo-agent\ntools: []\n---\nBody.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().NotBeNull();
        parsed!.Tools.Should().NotBeNull();
        parsed.Tools.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ToolsAbsent_IsNull()
    {
        // Absent means "inherit all parent tools".
        var md = "---\nname: echo-agent\n---\nBody.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().NotBeNull();
        parsed!.Tools.Should().BeNull();
    }

    [Fact]
    public void Parse_CrlfLineEndings_AreHandled()
    {
        var md = "---\r\nname: echo-agent\r\ndescription: With CRLF.\r\n---\r\nBody.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().NotBeNull();
        parsed!.Name.Should().Be("echo-agent");
        parsed.SystemPrompt.Should().Be("Body.");
    }

    [Fact]
    public void Parse_LeadingBomAndWhitespace_AreTolerated()
    {
        var md = "\uFEFF\n---\nname: echo-agent\n---\nBody.";

        var parsed = SubAgentMarkdownParser.Parse(md, "echo-agent");

        parsed.Should().NotBeNull();
        parsed!.Name.Should().Be("echo-agent");
    }
}
