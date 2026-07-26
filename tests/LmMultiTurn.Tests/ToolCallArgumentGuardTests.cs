using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins the truncation-guard contract at the shared tool-dispatch choke point
/// (<c>MultiTurnAgentLoop.ExecuteToolCallAsync</c>). When the provider stops with
/// <c>stop_reason: max_tokens</c> it cuts the streaming <c>tool_use</c> argument JSON off
/// mid-string (observed on a workflow delegate: <c>Write</c> args truncated to <c>{"file_path":"…</c>
/// after ~71 chars; <c>Bash</c> args arriving empty). The loop must NOT hand malformed arguments to
/// the tool handler — executing a side-effecting tool (Write a file, run a Bash command) with
/// truncated args is the actual harm. Instead it must return a recoverable, LLM-visible error so the
/// run survives and the model can retry.
/// </summary>
public class ToolCallArgumentGuardTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    [Fact]
    public async Task TruncatedToolArgs_DoNotReachHandler_AndReturnRecoverableError()
    {
        // The LLM's tool_use block was cut off at the max_tokens ceiling: valid JSON prefix, no
        // closing brace/quote. This is exactly what the failing workflow delegate sent for Write.
        const string truncatedArgs = "{\"file_path\":\"/tmp/report.md\",\"content\":\"# Title\\nThe quick brown";

        var toolCall = new ToolCallMessage
        {
            FunctionName = "Write",
            FunctionArgs = truncatedArgs,
            ToolCallId = "tc_trunc",
            Role = Role.Assistant,
        };
        SetupToolThenFinalText(toolCall);

        var handlerInvoked = false;
        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildWriteContract(),
            (_, _, _) =>
            {
                handlerInvoked = true;
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("wrote"));
            });

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "write a report", Role = Role.User }]);
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        handlerInvoked.Should().BeFalse(
            "a side-effecting tool must never run with truncated/malformed arguments");

        var toolResult = messages.OfType<ToolCallResultMessage>()
            .Should().ContainSingle(m => m.ToolCallId == "tc_trunc").Subject;
        toolResult.IsError.Should().BeTrue(
            "malformed tool arguments must surface as a recoverable error to the LLM, not a crash");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task EmptyToolArgs_ForFunctionRequiringArgs_DoNotReachHandler_AndReturnRecoverableError()
    {
        // Bash args arrived as an empty string (0 bytes) — the truncation manifested as no JSON at
        // all. FunctionArgs ?? "{}" only guards null, so today "" is dispatched straight to the
        // handler, which then fails deserializing a required 'command'.
        var toolCall = new ToolCallMessage
        {
            FunctionName = "Bash",
            FunctionArgs = string.Empty,
            ToolCallId = "tc_empty",
            Role = Role.Assistant,
        };
        SetupToolThenFinalText(toolCall);

        var handlerInvoked = false;
        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildBashContract(),
            (_, _, _) =>
            {
                handlerInvoked = true;
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ran"));
            });

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "run ls", Role = Role.User }]);
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        handlerInvoked.Should().BeFalse(
            "a tool that requires arguments must not run when the argument payload is empty (truncated to 0 bytes)");

        var toolResult = messages.OfType<ToolCallResultMessage>()
            .Should().ContainSingle(m => m.ToolCallId == "tc_empty").Subject;
        toolResult.IsError.Should().BeTrue(
            "empty args for an arg-requiring tool must surface as a recoverable error to the LLM");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task EmptyToolArgs_ForParameterlessFunction_StillExecute()
    {
        // Guard must NOT over-fire: a genuinely parameterless tool called with "" (or null) args is
        // legitimate and must still run. This is the existing FunctionArgs ?? "{}" behavior; the
        // guard only rejects malformed/empty args when the contract declares required parameters.
        var toolCall = new ToolCallMessage
        {
            FunctionName = "get_time",
            FunctionArgs = string.Empty,
            ToolCallId = "tc_noargs",
            Role = Role.Assistant,
        };
        SetupToolThenFinalText(toolCall);

        var handlerInvoked = false;
        var registry = new FunctionRegistry();
        registry.AddFunction(
            new FunctionContract { Name = "get_time", Description = "Get the current time", Parameters = [] },
            (_, _, _) =>
            {
                handlerInvoked = true;
                return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("12:00"));
            });

        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            "test-thread",
            logger: _loggerMock.Object);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "what time is it", Role = Role.User }]);
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            messages.Add(msg);
        }

        handlerInvoked.Should().BeTrue(
            "a parameterless tool called with empty args is legitimate and must still execute");

        var toolResult = messages.OfType<ToolCallResultMessage>()
            .Should().ContainSingle(m => m.ToolCallId == "tc_noargs").Subject;
        toolResult.IsError.Should().BeFalse();

        await cts.CancelAsync();
    }

    private static FunctionContract BuildWriteContract() => new()
    {
        Name = "Write",
        Description = "Write a file",
        Parameters =
        [
            new FunctionParameterContract
            {
                Name = "file_path",
                Description = "Absolute path to write",
                ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                IsRequired = true,
            },
            new FunctionParameterContract
            {
                Name = "content",
                Description = "File contents",
                ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                IsRequired = true,
            },
        ],
    };

    private static FunctionContract BuildBashContract() => new()
    {
        Name = "Bash",
        Description = "Run a shell command",
        Parameters =
        [
            new FunctionParameterContract
            {
                Name = "command",
                Description = "The command to run",
                ParameterType = new JsonSchemaObject { Type = JsonSchemaTypeHelper.ToType("string") },
                IsRequired = true,
            },
        ],
    };

    private void SetupToolThenFinalText(IMessage toolCall)
    {
        // Turn 1 emits the tool call; every subsequent turn returns terminating text so the run
        // ends deterministically after the tool result is produced (rather than re-emitting the
        // tool call each turn up to MaxTurnsPerRun).
        var callCount = 0;
        _mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(ToAsyncEnumerable([toolCall]))
                    : Task.FromResult(ToAsyncEnumerable(
                        [new TextMessage { Text = "done", Role = Role.Assistant }]));
            });
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
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
