using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins the per-turn output-budget contract for <see cref="MultiTurnAgentLoop"/>. Sub-agent and
/// workflow-delegate loops are constructed WITHOUT an explicit <see cref="GenerateReplyOptions.MaxToken"/>
/// (their <c>DefaultOptions</c> carry only a model id, if that). Without a library-side floor, each
/// turn's request goes out with <c>MaxToken == null</c>, so the Anthropic provider applies its raw
/// <c>4096</c> default. A <c>tool_use</c> block whose payload is a real file body (<c>Write.content</c>)
/// or script (<c>Bash.command</c>) then exhausts that budget: the provider stops with
/// <c>stop_reason: max_tokens</c> and truncates the streaming tool-argument JSON mid-string, and the
/// loop executes the corrupt args. The main agent already dodges this with an explicit <c>MaxToken</c>;
/// these loops must get a sensible floor too.
/// </summary>
public class MultiTurnAgentBudgetTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    [Fact]
    public async Task Loop_WithNoExplicitMaxToken_SendsProviderARealBudget_NotThe4096Default()
    {
        // Capture the options the provider actually receives for the turn.
        GenerateReplyOptions? captured = null;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, options, _) =>
                {
                    captured = options;
                    return Task.FromResult(
                        ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }])
                    );
                }
            );

        var registry = new FunctionRegistry();

        // No defaultOptions supplied — exactly how a sub-agent/workflow-controller loop with no
        // configured budget is constructed. Today this yields MaxToken == null on every turn.
        await using var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            registry,
            threadId: "test-thread",
            logger: _loggerMock.Object
        );

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "hi", Role = Role.User }]);
        await foreach (var _ in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            // drain
        }

        captured.Should().NotBeNull("the provider must have been invoked for the turn");
        captured!
            .MaxToken.Should()
            .NotBeNull(
                "a sub-agent/workflow-delegate loop must send a real max_tokens instead of letting the "
                    + "provider fall back to its 4096 default, which truncates tool-call argument JSON"
            );
        captured
            .MaxToken.Should()
            .BeGreaterThan(
                4096,
                "the floor must exceed the provider default that causes stop_reason=max_tokens truncation"
            );

        await cts.CancelAsync();
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}
