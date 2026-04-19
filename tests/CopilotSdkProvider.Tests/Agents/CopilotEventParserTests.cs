using System.Text.Json;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Tests.Agents;

public class CopilotEventParserTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ExtractSessionId_TopLevelCamelCase_ReturnsValue()
    {
        var el = Parse("""{"sessionId": "s-1"}""");
        CopilotEventParser.ExtractSessionId(el).Should().Be("s-1");
    }

    [Fact]
    public void ExtractSessionId_NestedSessionId_ReturnsValue()
    {
        var el = Parse("""{"session": {"id": "s-2"}}""");
        CopilotEventParser.ExtractSessionId(el).Should().Be("s-2");
    }

    [Fact]
    public void ExtractSessionId_Missing_ReturnsNull()
    {
        var el = Parse("""{"foo": "bar"}""");
        CopilotEventParser.ExtractSessionId(el).Should().BeNull();
    }

    [Fact]
    public void ExtractSessionId_NullElement_ReturnsNull()
    {
        CopilotEventParser.ExtractSessionId(null).Should().BeNull();
    }

    [Fact]
    public void ExtractErrorMessage_StringError_ReturnsIt()
    {
        var el = Parse("""{"error": "simple error"}""");
        CopilotEventParser.ExtractErrorMessage(el).Should().Be("simple error");
    }

    [Fact]
    public void ExtractErrorMessage_ObjectError_ReturnsMessage()
    {
        var el = Parse("""{"error": {"message": "detailed error"}}""");
        CopilotEventParser.ExtractErrorMessage(el).Should().Be("detailed error");
    }

    [Fact]
    public void ExtractErrorMessage_FallbackMessage_ReturnsIt()
    {
        var el = Parse("""{"message": "fallback msg"}""");
        CopilotEventParser.ExtractErrorMessage(el).Should().Be("fallback msg");
    }

    [Fact]
    public void ExtractErrorMessage_NoError_ReturnsNull()
    {
        var el = Parse("""{"data": 42}""");
        CopilotEventParser.ExtractErrorMessage(el).Should().BeNull();
    }

    [Fact]
    public void GetPropertyString_ExistingProperty_ReturnsValue()
    {
        var el = Parse("""{"name": "test"}""");
        CopilotEventParser.GetPropertyString(el, "name").Should().Be("test");
    }

    [Fact]
    public void GetPropertyString_MissingProperty_ReturnsNull()
    {
        var el = Parse("""{"other": "value"}""");
        CopilotEventParser.GetPropertyString(el, "name").Should().BeNull();
    }

    [Fact]
    public void GetPropertyString_NonStringProperty_ReturnsNull()
    {
        var el = Parse("""{"count": 42}""");
        CopilotEventParser.GetPropertyString(el, "count").Should().BeNull();
    }

    [Fact]
    public void ExtractSessionUpdateKind_NestedUpdate_ReturnsKind()
    {
        var el = Parse("""{"update": {"sessionUpdate": "agent_message_chunk"}}""");
        CopilotEventParser.ExtractSessionUpdateKind(el).Should().Be("agent_message_chunk");
    }

    [Fact]
    public void ExtractSessionUpdateKind_TopLevel_ReturnsKind()
    {
        var el = Parse("""{"sessionUpdate": "tool_call"}""");
        CopilotEventParser.ExtractSessionUpdateKind(el).Should().Be("tool_call");
    }

    [Fact]
    public void ExtractSessionUpdateKind_Missing_ReturnsNull()
    {
        var el = Parse("""{"foo": "bar"}""");
        CopilotEventParser.ExtractSessionUpdateKind(el).Should().BeNull();
    }

    [Fact]
    public void ExtractSessionUpdateElement_WithUpdateProperty_ReturnsIt()
    {
        var el = Parse("""{"update": {"sessionUpdate": "agent_message_chunk", "content": {"text": "hi"}}}""");
        var result = CopilotEventParser.ExtractSessionUpdateElement(el);

        result.Should().NotBeNull();
        result!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        CopilotEventParser.GetPropertyString(result.Value, "sessionUpdate").Should().Be("agent_message_chunk");
    }

    [Fact]
    public void ExtractSessionUpdateElement_WithoutUpdate_ReturnsRoot()
    {
        var el = Parse("""{"sessionUpdate": "plan"}""");
        var result = CopilotEventParser.ExtractSessionUpdateElement(el);

        result.Should().NotBeNull();
        CopilotEventParser.GetPropertyString(result!.Value, "sessionUpdate").Should().Be("plan");
    }

    [Fact]
    public void GetPropertyElement_ExistingProperty_ReturnsClonedElement()
    {
        var el = Parse("""{"data": {"nested": true}}""");
        var result = CopilotEventParser.GetPropertyElement(el, "data");
        result.Should().NotBeNull();
        result!.Value.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void GetPropertyElement_MissingProperty_ReturnsNull()
    {
        var el = Parse("""{"other": 1}""");
        CopilotEventParser.GetPropertyElement(el, "data").Should().BeNull();
    }

    [Fact]
    public void CreateEmptyObject_ReturnsEmptyJsonObject()
    {
        var obj = CopilotEventParser.CreateEmptyObject();
        obj.ValueKind.Should().Be(JsonValueKind.Object);
        obj.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        CopilotEventParser.Truncate("hello").Should().Be("hello");
    }

    [Fact]
    public void Truncate_LongString_TruncatesTo2000()
    {
        var longString = new string('x', 3000);
        var result = CopilotEventParser.Truncate(longString);
        result.Length.Should().Be(2000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Truncate_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        CopilotEventParser.Truncate(input!).Should().Be(string.Empty);
    }
}
