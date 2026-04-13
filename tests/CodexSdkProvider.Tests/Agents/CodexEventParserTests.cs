using System.Text.Json;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Tests.Agents;

public class CodexEventParserTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    #region ExtractThreadId

    [Fact]
    public void ExtractThreadId_CamelCase_ReturnsValue()
    {
        var el = Parse("""{"threadId": "t-123"}""");
        CodexEventParser.ExtractThreadId(el).Should().Be("t-123");
    }

    [Fact]
    public void ExtractThreadId_SnakeCase_ReturnsValue()
    {
        var el = Parse("""{"thread_id": "t-456"}""");
        CodexEventParser.ExtractThreadId(el).Should().Be("t-456");
    }

    [Fact]
    public void ExtractThreadId_NestedThread_ReturnsValue()
    {
        var el = Parse("""{"thread": {"id": "t-789"}}""");
        CodexEventParser.ExtractThreadId(el).Should().Be("t-789");
    }

    [Fact]
    public void ExtractThreadId_Missing_ReturnsNull()
    {
        var el = Parse("""{"foo": "bar"}""");
        CodexEventParser.ExtractThreadId(el).Should().BeNull();
    }

    [Fact]
    public void ExtractThreadId_NullElement_ReturnsNull()
    {
        CodexEventParser.ExtractThreadId(null).Should().BeNull();
    }

    #endregion

    #region ExtractTurnId

    [Fact]
    public void ExtractTurnId_CamelCase_ReturnsValue()
    {
        var el = Parse("""{"turnId": "turn-1"}""");
        CodexEventParser.ExtractTurnId(el).Should().Be("turn-1");
    }

    [Fact]
    public void ExtractTurnId_SnakeCase_ReturnsValue()
    {
        var el = Parse("""{"turn_id": "turn-2"}""");
        CodexEventParser.ExtractTurnId(el).Should().Be("turn-2");
    }

    [Fact]
    public void ExtractTurnId_Nested_ReturnsValue()
    {
        var el = Parse("""{"turn": {"id": "turn-3"}}""");
        CodexEventParser.ExtractTurnId(el).Should().Be("turn-3");
    }

    [Fact]
    public void ExtractTurnId_Missing_ReturnsNull()
    {
        var el = Parse("""{}""");
        CodexEventParser.ExtractTurnId(el).Should().BeNull();
    }

    #endregion

    #region ExtractTurnStatus

    [Fact]
    public void ExtractTurnStatus_TopLevel_ReturnsValue()
    {
        var el = Parse("""{"status": "completed"}""");
        CodexEventParser.ExtractTurnStatus(el).Should().Be("completed");
    }

    [Fact]
    public void ExtractTurnStatus_Nested_ReturnsValue()
    {
        var el = Parse("""{"turn": {"status": "failed"}}""");
        CodexEventParser.ExtractTurnStatus(el).Should().Be("failed");
    }

    [Fact]
    public void ExtractTurnStatus_Missing_ReturnsNull()
    {
        var el = Parse("""{"other": 1}""");
        CodexEventParser.ExtractTurnStatus(el).Should().BeNull();
    }

    #endregion

    #region ExtractTurnErrorMessage

    [Fact]
    public void ExtractTurnErrorMessage_TopLevelError_ReturnsMessage()
    {
        var el = Parse("""{"error": {"message": "boom"}}""");
        CodexEventParser.ExtractTurnErrorMessage(el).Should().Be("boom");
    }

    [Fact]
    public void ExtractTurnErrorMessage_NestedTurnError_ReturnsMessage()
    {
        var el = Parse("""{"turn": {"error": {"message": "nested boom"}}}""");
        CodexEventParser.ExtractTurnErrorMessage(el).Should().Be("nested boom");
    }

    [Fact]
    public void ExtractTurnErrorMessage_NoError_ReturnsNull()
    {
        var el = Parse("""{"status": "completed"}""");
        CodexEventParser.ExtractTurnErrorMessage(el).Should().BeNull();
    }

    #endregion

    #region ExtractErrorMessage

    [Fact]
    public void ExtractErrorMessage_StringError_ReturnsIt()
    {
        var el = Parse("""{"error": "simple error"}""");
        CodexEventParser.ExtractErrorMessage(el).Should().Be("simple error");
    }

    [Fact]
    public void ExtractErrorMessage_ObjectError_ReturnsMessage()
    {
        var el = Parse("""{"error": {"message": "detailed error"}}""");
        CodexEventParser.ExtractErrorMessage(el).Should().Be("detailed error");
    }

    [Fact]
    public void ExtractErrorMessage_FallbackMessage_ReturnsIt()
    {
        var el = Parse("""{"message": "fallback msg"}""");
        CodexEventParser.ExtractErrorMessage(el).Should().Be("fallback msg");
    }

    [Fact]
    public void ExtractErrorMessage_NoError_ReturnsNull()
    {
        var el = Parse("""{"data": 42}""");
        CodexEventParser.ExtractErrorMessage(el).Should().BeNull();
    }

    #endregion

    #region Method Checkers

    [Theory]
    [InlineData("item/started", true)]
    [InlineData("item.started", true)]
    [InlineData("item/completed", false)]
    [InlineData("other", false)]
    public void IsItemStartedMethod_ChecksCorrectly(string method, bool expected)
    {
        CodexEventParser.IsItemStartedMethod(method).Should().Be(expected);
    }

    [Theory]
    [InlineData("item/completed", true)]
    [InlineData("item.completed", true)]
    [InlineData("item/started", false)]
    public void IsItemCompletedMethod_ChecksCorrectly(string method, bool expected)
    {
        CodexEventParser.IsItemCompletedMethod(method).Should().Be(expected);
    }

    [Theory]
    [InlineData("codex/event/web_search_begin", true)]
    [InlineData("codex.event.web_search_begin", true)]
    [InlineData("codex/event/web_search_end", false)]
    public void IsWebSearchBeginMethod_ChecksCorrectly(string method, bool expected)
    {
        CodexEventParser.IsWebSearchBeginMethod(method).Should().Be(expected);
    }

    [Theory]
    [InlineData("codex/event/web_search_end", true)]
    [InlineData("codex.event.web_search_end", true)]
    [InlineData("codex/event/web_search_begin", false)]
    public void IsWebSearchEndMethod_ChecksCorrectly(string method, bool expected)
    {
        CodexEventParser.IsWebSearchEndMethod(method).Should().Be(expected);
    }

    #endregion

    #region Status Checkers

    [Theory]
    [InlineData("in_progress", true)]
    [InlineData("inProgress", true)]
    [InlineData("inprogress", true)]
    [InlineData("IN_PROGRESS", true)]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    public void IsInProgress_ChecksCorrectly(string status, bool expected)
    {
        CodexEventParser.IsInProgress(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("failed", true)]
    [InlineData("in_progress", false)]
    public void IsTerminalTurnStatus_ChecksCorrectly(string status, bool expected)
    {
        CodexEventParser.IsTerminalTurnStatus(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("interrupted", true)]
    [InlineData("cancelled", true)]
    [InlineData("canceled", true)]
    [InlineData("FAILED", true)]
    [InlineData("completed", false)]
    [InlineData(null, false)]
    public void IsTurnFailureStatus_ChecksCorrectly(string? status, bool expected)
    {
        CodexEventParser.IsTurnFailureStatus(status).Should().Be(expected);
    }

    [Theory]
    [InlineData("turn/failed", true)]
    [InlineData("turn.failed", true)]
    [InlineData("turn/interrupted", true)]
    [InlineData("turn.interrupted", true)]
    [InlineData("turn/cancelled", true)]
    [InlineData("turn.cancelled", true)]
    [InlineData("turn/canceled", true)]
    [InlineData("turn.canceled", true)]
    [InlineData("turn/completed", false)]
    [InlineData("item/started", false)]
    public void IsTurnFailureNotification_ChecksCorrectly(string method, bool expected)
    {
        CodexEventParser.IsTurnFailureNotification(method).Should().Be(expected);
    }

    #endregion

    #region NormalizeInternalToolStatus

    [Theory]
    [InlineData(null, false, "success")]
    [InlineData(null, true, "error")]
    [InlineData("", false, "success")]
    [InlineData("completed", false, "success")]
    [InlineData("completed", true, "error")]
    [InlineData("success", false, "success")]
    [InlineData("failed", false, "error")]
    [InlineData("error", false, "error")]
    [InlineData("interrupted", false, "cancelled")]
    [InlineData("cancelled", false, "cancelled")]
    [InlineData("canceled", false, "cancelled")]
    [InlineData("timed_out", false, "timed_out")]
    [InlineData("timeout", false, "timed_out")]
    [InlineData("unknown_status", false, "success")]
    [InlineData("unknown_status", true, "error")]
    public void NormalizeInternalToolStatus_NormalizesCorrectly(
        string? status, bool hasError, string expected)
    {
        CodexEventParser.NormalizeInternalToolStatus(status, hasError).Should().Be(expected);
    }

    #endregion

    #region NormalizeInternalToolName

    [Theory]
    [InlineData("webSearch", "web_search")]
    [InlineData("web_search", "web_search")]
    [InlineData("commandExecution", "command_execution")]
    [InlineData("command_execution", "command_execution")]
    [InlineData("fileChange", "file_change")]
    [InlineData("file_change", "file_change")]
    [InlineData("todoList", "todo_list")]
    [InlineData("todo_list", "todo_list")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void NormalizeInternalToolName_NormalizesCorrectly(
        string? itemType, string? expected)
    {
        CodexEventParser.NormalizeInternalToolName(itemType).Should().Be(expected);
    }

    #endregion

    #region TryParseInternalToolItem

    [Fact]
    public void TryParseInternalToolItem_ValidWebSearch_ReturnsTrue()
    {
        var el = Parse("""{"item": {"type": "webSearch", "id": "call-1", "query": "test"}}""");
        var result = CodexEventParser.TryParseInternalToolItem(
            el, out var item, out var toolName, out var toolCallId);

        result.Should().BeTrue();
        toolName.Should().Be("web_search");
        toolCallId.Should().Be("call-1");
    }

    [Fact]
    public void TryParseInternalToolItem_ValidCommandExecution_UsesCallId()
    {
        var el = Parse("""{"item": {"type": "command_execution", "call_id": "cmd-1"}}""");
        var result = CodexEventParser.TryParseInternalToolItem(
            el, out _, out var toolName, out var toolCallId);

        result.Should().BeTrue();
        toolName.Should().Be("command_execution");
        toolCallId.Should().Be("cmd-1");
    }

    [Fact]
    public void TryParseInternalToolItem_UnknownType_ReturnsFalse()
    {
        var el = Parse("""{"item": {"type": "unknown_tool", "id": "x"}}""");
        CodexEventParser.TryParseInternalToolItem(
            el, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseInternalToolItem_MissingId_ReturnsFalse()
    {
        var el = Parse("""{"item": {"type": "webSearch"}}""");
        CodexEventParser.TryParseInternalToolItem(
            el, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseInternalToolItem_MissingItem_ReturnsFalse()
    {
        var el = Parse("""{"other": "data"}""");
        CodexEventParser.TryParseInternalToolItem(
            el, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseInternalToolItem_NullElement_ReturnsFalse()
    {
        CodexEventParser.TryParseInternalToolItem(
            null, out _, out _, out _).Should().BeFalse();
    }

    #endregion

    #region AddToolSpecificFields

    [Fact]
    public void AddToolSpecificFields_WebSearch_AddsQueryAndAction()
    {
        var payload = Parse("""{"query": "test query", "action": "search"}""");
        var dict = new Dictionary<string, object?>();

        CodexEventParser.AddToolSpecificFields(dict, "web_search", payload, isResultPayload: false);

        dict.Should().ContainKey("query");
        dict["query"].Should().Be("test query");
        dict.Should().ContainKey("action");
        dict["action"].Should().Be("search");
    }

    [Fact]
    public void AddToolSpecificFields_CommandExecution_AddsCommand()
    {
        var payload = Parse("""{"command": "ls -la", "cwd": "/tmp"}""");
        var dict = new Dictionary<string, object?>();

        CodexEventParser.AddToolSpecificFields(dict, "command_execution", payload, isResultPayload: false);

        dict["command"].Should().Be("ls -la");
        dict["cwd"].Should().Be("/tmp");
    }

    [Fact]
    public void AddToolSpecificFields_CommandExecutionResult_AddsExitCode()
    {
        var payload = Parse("""{"exit_code": 0, "stdout": "hello"}""");
        var dict = new Dictionary<string, object?>();

        CodexEventParser.AddToolSpecificFields(dict, "command_execution", payload, isResultPayload: true);

        dict["exit_code"].Should().Be(0);
        dict["stdout_excerpt"].Should().Be("hello");
    }

    #endregion

    #region Utility Methods

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        CodexEventParser.Truncate("hello").Should().Be("hello");
    }

    [Fact]
    public void Truncate_LongString_TruncatesTo2000()
    {
        var longString = new string('x', 3000);
        var result = CodexEventParser.Truncate(longString);
        result.Length.Should().Be(2000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Truncate_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        CodexEventParser.Truncate(input!).Should().Be(string.Empty);
    }

    [Fact]
    public void CreateEmptyObject_ReturnsEmptyJsonObject()
    {
        var obj = CodexEventParser.CreateEmptyObject();
        obj.ValueKind.Should().Be(JsonValueKind.Object);
        obj.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void GetPropertyString_ExistingProperty_ReturnsValue()
    {
        var el = Parse("""{"name": "test"}""");
        CodexEventParser.GetPropertyString(el, "name").Should().Be("test");
    }

    [Fact]
    public void GetPropertyString_MissingProperty_ReturnsNull()
    {
        var el = Parse("""{"other": "value"}""");
        CodexEventParser.GetPropertyString(el, "name").Should().BeNull();
    }

    [Fact]
    public void GetPropertyString_NonStringProperty_ReturnsNull()
    {
        var el = Parse("""{"count": 42}""");
        CodexEventParser.GetPropertyString(el, "count").Should().BeNull();
    }

    [Fact]
    public void GetPropertyElement_ExistingProperty_ReturnsClonedElement()
    {
        var el = Parse("""{"data": {"nested": true}}""");
        var result = CodexEventParser.GetPropertyElement(el, "data");
        result.Should().NotBeNull();
        result!.Value.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void GetPropertyElement_MissingProperty_ReturnsNull()
    {
        var el = Parse("""{"other": 1}""");
        CodexEventParser.GetPropertyElement(el, "data").Should().BeNull();
    }

    [Fact]
    public void AddIntField_NumericValue_AddsInteger()
    {
        var payload = Parse("""{"exitCode": 42}""");
        var dict = new Dictionary<string, object?>();

        CodexEventParser.AddIntField(dict, payload, "exit_code", "exitCode");

        dict["exit_code"].Should().Be(42);
    }

    [Fact]
    public void AddIntField_StringNumericValue_ParsesAndAdds()
    {
        var payload = Parse("""{"exitCode": "7"}""");
        var dict = new Dictionary<string, object?>();

        CodexEventParser.AddIntField(dict, payload, "exit_code", "exitCode");

        dict["exit_code"].Should().Be(7);
    }

    [Fact]
    public void AddIntField_MissingField_DoesNotAdd()
    {
        var payload = Parse("""{"other": 1}""");
        var dict = new Dictionary<string, object?>();

        CodexEventParser.AddIntField(dict, payload, "exit_code", "exitCode");

        dict.Should().NotContainKey("exit_code");
    }

    #endregion
}
