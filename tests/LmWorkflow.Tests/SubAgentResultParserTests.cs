using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Unit tests for <see cref="SubAgentResultParser"/>: the parse of the injected
///     <c>&lt;sub-agent name="…" id="…"&gt;…&lt;/sub-agent&gt;</c> completion block, asserting the correlation
///     key is the <c>id</c> attribute (not <c>name</c>), and that the completed/error variants are
///     distinguished and their payloads extracted.
/// </summary>
public class SubAgentResultParserTests
{
    [Fact]
    public void TryParse_CompletedBlock_ExtractsIdAndJsonPayload_NotError()
    {
        var text =
            "<sub-agent name=\"general-purpose\" id=\"abc123def456\">\n"
            + "[Completed] Task: Summarize the doc\n"
            + "Result: { \"text\": \"all done\" }\n"
            + "</sub-agent>";

        var parsed = SubAgentResultParser.TryParse(text, out var id, out var payload, out var isError);

        parsed.Should().BeTrue();
        id.Should().Be("abc123def456");
        isError.Should().BeFalse();
        payload.Should().Be("{ \"text\": \"all done\" }");
    }

    [Fact]
    public void TryParse_ErrorBlock_ExtractsIdAndError_IsError()
    {
        var text =
            "<sub-agent name=\"summarizer\" id=\"deadbeef0000\">\n"
            + "[Error] Task: Summarize the doc\n"
            + "Error: the sub-agent exploded\n"
            + "</sub-agent>";

        var parsed = SubAgentResultParser.TryParse(text, out var id, out var payload, out var isError);

        parsed.Should().BeTrue();
        id.Should().Be("deadbeef0000");
        isError.Should().BeTrue();
        payload.Should().Be("the sub-agent exploded");
    }

    [Fact]
    public void TryParse_MultiLineJsonResult_CapturesWholePayload()
    {
        var text =
            "<sub-agent name=\"t\" id=\"id1\">\n"
            + "[Completed] Task: x\n"
            + "Result: {\n  \"text\": \"line\"\n}\n"
            + "</sub-agent>";

        var parsed = SubAgentResultParser.TryParse(text, out var id, out var payload, out var isError);

        parsed.Should().BeTrue();
        id.Should().Be("id1");
        isError.Should().BeFalse();
        payload.Should().Be("{\n  \"text\": \"line\"\n}");
    }

    [Theory]
    [InlineData("just some plain text")]
    [InlineData("")]
    [InlineData("<other-tag id=\"x\">nope</other-tag>")]
    public void TryParse_NonMatchingText_ReturnsFalse(string text)
    {
        var parsed = SubAgentResultParser.TryParse(text, out var id, out var payload, out var isError);

        parsed.Should().BeFalse();
        id.Should().BeEmpty();
        payload.Should().BeEmpty();
        isError.Should().BeFalse();
    }
}
