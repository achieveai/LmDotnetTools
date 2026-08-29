using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Lifecycle;

/// <summary>
/// The approval gate as the loop actually uses it: a configured approver is consulted before a
/// handler runs, its refusal keeps the handler from running at all, and an unconfigured bundle
/// leaves the loop behaving exactly as it did before approval existed.
/// </summary>
/// <remarks>
/// These are the tests that make the gate load-bearing. <c>ToolInvocationPreparer</c> has its own
/// unit tests, but a preparer nothing calls is a decision nothing enforces — the property worth
/// pinning is that <see cref="MultiTurnAgentLoop"/> asks it, on the path that dispatches tools.
/// </remarks>
public class ToolApprovalGateIntegrationTests
{
    private const string ToolName = "delete_everything";

    [Fact]
    public async Task ARefusedCallNeverReachesItsHandler()
    {
        var gate = RecordingToolApprovalGate.Denying("not on my watch");
        var invocations = 0;

        var messages = await RunOneToolCallAsync(
            Bundle(gate),
            _ =>
            {
                invocations++;
                return "deleted everything";
            }
        );

        invocations.Should().Be(0, "a refusal must block execution, not merely annotate it");

        var result = messages.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(ToolApprovalOutcomes.Denied);

        // The model is told what happened in the ordinary result channel, so it can pick something
        // else rather than see the loop unwind.
        result.Result.Should().Contain(ToolName).And.Contain("not on my watch");

        gate.ToolNames.Should().Equal([ToolName]);
    }

    [Fact]
    public async Task AnApprovedCallRunsOnExactlyTheBytesTheApproverSaw()
    {
        var gate = RecordingToolApprovalGate.Allowing();
        string? handlerSaw = null;

        _ = await RunOneToolCallAsync(
            Bundle(gate),
            args =>
            {
                handlerSaw = args;
                return "ok";
            }
        );

        var approved = gate.Requests.Should().ContainSingle().Subject;
        handlerSaw.Should().Be(approved.Arguments.Json, "what was decided and what executed must not diverge");
    }

    [Fact]
    public async Task WithNoApproverConfiguredTheHandlerRunsUntouched()
    {
        string? handlerSaw = null;

        var messages = await RunOneToolCallAsync(
            lifecycleServices: null,
            handler: args =>
            {
                handlerSaw = args;
                return "ok";
            }
        );

        handlerSaw.Should().Be(Arguments);
        var result = messages.OfType<ToolCallResultMessage>().Should().ContainSingle().Subject;
        result.IsError.Should().BeFalse();
        result.Result.Should().Be("ok");
    }

    [Fact]
    public async Task AHallucinatedToolNameIsRejectedBeforeAnyApproverIsAsked()
    {
        // Ordering, not politeness: a name the host never registered cannot be executed whatever
        // the answer, so asking would spend a human's attention — or a remote approval round trip —
        // on a call that was already going to fail.
        var gate = RecordingToolApprovalGate.Allowing();

        var messages = await RunOneToolCallAsync(Bundle(gate), _ => "ok", calledToolName: "no_such_tool");

        gate.WasConsulted.Should().BeFalse();
        messages.OfType<ToolCallResultMessage>().Should().ContainSingle().Which.IsError.Should().BeTrue();
    }

    private const string Arguments = """{"target":"/"}""";

    private static MultiTurnLifecycleServices Bundle(IToolApprovalGate gate) =>
        new() { Approval = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [gate] }) };

    /// <summary>
    /// Drives one turn that calls <see cref="ToolName"/> (or <paramref name="calledToolName"/>) and
    /// one closing turn of plain text, returning everything the run published.
    /// </summary>
    private static async Task<List<IMessage>> RunOneToolCallAsync(
        MultiTurnLifecycleServices? lifecycleServices,
        Func<string, string> handler,
        string? calledToolName = null
    )
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = calledToolName ?? ToolName,
            FunctionArgs = Arguments,
            ToolCallId = "tc_1",
            Role = Role.Assistant,
        };

        var agent = new Mock<IStreamingAgent>();
        var turn = 0;
        _ = agent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
                Task.FromResult(
                    ++turn == 1
                        ? ToAsyncEnumerable([toolCall])
                        : ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }])
                )
            );

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(
            new FunctionContract
            {
                Name = ToolName,
                Description = "A tool a host would want gated.",
                Parameters = [],
            },
            (args, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(handler(args)))
        );

        await using var loop = new MultiTurnAgentLoop(
            agent.Object,
            registry,
            "approval-thread",
            lifecycleServices: lifecycleServices
        );

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var messages = new List<IMessage>();
        var input = new UserInput([new TextMessage { Text = "go", Role = Role.User }]);
        await foreach (var message in loop.ExecuteRunAsync(input, cts.Token))
        {
            messages.Add(message);
        }

        await cts.CancelAsync();
        return messages;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
