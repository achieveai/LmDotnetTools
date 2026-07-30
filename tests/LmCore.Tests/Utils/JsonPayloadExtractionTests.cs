using System.Text.Json.Nodes;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

/// <summary>
///     Covers <see cref="JsonStringUtils.TryExtractJsonPayload"/>: pulling a self-contained JSON value out of
///     free-form model output (clean JSON, fenced JSON, prose-wrapped JSON) while rejecting text that carries no
///     parseable JSON. Each accepted result must itself round-trip through <c>JsonNode.Parse</c>.
/// </summary>
public class JsonPayloadExtractionTests
{
    [Fact]
    public void WholeCleanJsonObject_IsReturnedVerbatim()
    {
        var text = """{ "summary": "ok", "count": 2 }""";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeTrue();
        JsonNode.Parse(json)!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void WholeCleanJsonArray_IsReturnedVerbatim()
    {
        var text = "[1, 2, 3]";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeTrue();
        JsonNode.Parse(json)!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public void FencedJsonBlock_WithLanguageTag_IsUnwrapped()
    {
        var text = "Here you go:\n```json\n{ \"summary\": \"ok\" }\n```\nThanks!";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeTrue();
        JsonNode.Parse(json)!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void FencedBlock_WithoutLanguageTag_IsUnwrapped()
    {
        var text = "```\n{ \"summary\": \"ok\" }\n```";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeTrue();
        JsonNode.Parse(json)!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void ProseWrappedObject_IsExtractedByBalancedSpan()
    {
        var text = "Sure! The result is { \"summary\": \"ok\" } — hope that helps.";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeTrue();
        JsonNode.Parse(json)!["summary"]!.GetValue<string>().Should().Be("ok");
    }

    [Fact]
    public void NestedObject_WithInnerArrayAndBraces_ExtractsWholeSpan()
    {
        var text = "prefix { \"a\": [1, 2], \"b\": { \"c\": 3 } } suffix";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeTrue();

        var node = JsonNode.Parse(json)!;
        node["a"]!.AsArray().Should().HaveCount(2);
        node["b"]!["c"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void BraceInsideStringLiteral_DoesNotThrowOffBalance()
    {
        var text = "note: { \"text\": \"a } brace in a string\" } end";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeTrue();
        JsonNode.Parse(json)!["text"]!.GetValue<string>().Should().Be("a } brace in a string");
    }

    [Fact]
    public void PlainProseWithoutJson_ReturnsFalse()
    {
        var text = "## Findings\n\nNo issues found. The code looks correct.";

        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeFalse();
        json.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void NullOrBlank_ReturnsFalse(string? text)
    {
        JsonStringUtils.TryExtractJsonPayload(text, out var json).Should().BeFalse();
        json.Should().BeEmpty();
    }
}
