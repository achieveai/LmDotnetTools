using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.ClientTools;

/// <summary>
/// Provider-level tests for <see cref="NotifyClientToolProvider"/> (issue #246). The tool must
/// resolve immediately (never <c>Deferred</c>) and must deliver exactly once through the injected
/// delegate — never through anything resembling <c>SendAsync</c>/a new run.
/// </summary>
public class NotifyClientToolProviderTests
{
    private readonly List<NotifyMessage> _delivered = [];
    private readonly FunctionDescriptor _tool;

    public NotifyClientToolProviderTests()
    {
        var provider = new NotifyClientToolProvider((notify, _) =>
        {
            _delivered.Add(notify);
            return ValueTask.CompletedTask;
        });
        _tool = provider.GetFunctions().Single(f => f.Contract.Name == NotifyClientToolProvider.ToolName);
    }

    private Task<ToolHandlerResult> InvokeAsync(string argsJson, string? toolCallId = "tc_1") =>
        _tool.Handler(argsJson, new ToolCallContext { ToolCallId = toolCallId }, CancellationToken.None);

    [Fact]
    public async Task ValidCall_ResolvesImmediately_AndDeliversExactlyOneNotification()
    {
        var args = JsonSerializer.Serialize(new { message = "build finished", label = "Build" });

        var result = await InvokeAsync(args);

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        ((ToolHandlerResult.Resolved)result).Payload.IsError.Should().BeFalse();

        _delivered.Should().ContainSingle();
        _delivered[0].NotifyKind.Should().Be(NotifyKinds.ClientNotification);
        _delivered[0].Detail.Should().Be("build finished");
        _delivered[0].Label.Should().Be("Build");
        _delivered[0].SourceToolName.Should().Be(NotifyClientToolProvider.ToolName);
        _delivered[0].SourceToolCallId.Should().Be("tc_1");
    }

    [Fact]
    public async Task ValidCall_WithoutLabel_Delivers()
    {
        var args = JsonSerializer.Serialize(new { message = "heads up" });

        var result = await InvokeAsync(args);

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        _delivered.Should().ContainSingle(n => n.Detail == "heads up" && n.Label == null);
    }

    [Fact]
    public async Task MissingMessage_ReturnsError_AndDeliversNothing()
    {
        var result = await InvokeAsync(JsonSerializer.Serialize(new { label = "x" }));

        result.Should().BeOfType<ToolHandlerResult.Resolved>();
        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("missing_message");
        _delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyMessage_ReturnsError()
    {
        var result = await InvokeAsync(JsonSerializer.Serialize(new { message = "   " }));

        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.ErrorCode.Should().Be("missing_message");
        _delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedJson_ReturnsInvalidArgs_AndDeliversNothing()
    {
        var result = await InvokeAsync("{not json");

        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("invalid_args");
        _delivered.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyArgs_ReturnsMissingMessage()
    {
        var result = await InvokeAsync("");

        var resolved = (ToolHandlerResult.Resolved)result;
        resolved.Payload.ErrorCode.Should().Be("missing_message");
    }
}
