using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using LmMultiTurn.Tests.Lifecycle;
using Moq;
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

    /// <summary>
    /// Loop-level check (issue #246, item 6): a mid-run <c>NotifyClient</c> call must be delivered
    /// inline as part of the SAME run/turn, never by spinning up a run of its own. Proven here by
    /// wiring a real <see cref="MultiTurnAgentLoop"/> with a <see cref="RecordingLifecyclePublisher"/>
    /// and asserting <see cref="LifecycleEventTypes.RunStarted"/> fires exactly once across the whole
    /// exchange (the tool call plus the model's follow-up final answer).
    /// </summary>
    [Fact]
    public async Task NotifyClient_MidRun_DoesNotStartAnExtraRun()
    {
        var notifyCall = new ToolCallMessage
        {
            FunctionName = NotifyClientToolProvider.ToolName,
            FunctionArgs = JsonSerializer.Serialize(new { message = "halfway there", label = "Progress" }),
            ToolCallId = "tc_notify",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "done", Role = Role.Assistant };
        var callCount = 0;
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                IMessage msg = callCount == 1 ? notifyCall : finalText;
                return Task.FromResult(ToAsyncEnumerable([msg]));
            });

        var publisher = new RecordingLifecyclePublisher();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            mockAgent.Object,
            registry,
            "notify-lifecycle-thread",
            lifecycleServices: new MultiTurnLifecycleServices { Publisher = publisher });
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        await foreach (var _ in loop.ExecuteRunAsync(
            new UserInput([new TextMessage { Text = "go", Role = Role.User }]), cts.Token)) { }

        publisher.EventTypes.Count(t => t == LifecycleEventTypes.RunStarted).Should().Be(
            1,
            "NotifyClient must deliver inline without starting a run of its own");
        publisher.Payloads<RunCompletedPayload>(LifecycleEventTypes.RunCompleted).Should().ContainSingle();
        callCount.Should().Be(2);

        await cts.CancelAsync();
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
