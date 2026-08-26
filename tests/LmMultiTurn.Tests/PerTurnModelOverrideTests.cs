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

public sealed class PerTurnModelOverrideTests
{
    [Fact]
    public async Task Consecutive_runs_use_the_requested_model_once_without_mutating_the_default()
    {
        var models = new List<string?>();
        var histories = new List<IReadOnlyList<string>>();
        var agent = new Mock<IStreamingAgent>();
        agent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (messages, options, _) =>
                {
                    models.Add(options.ModelId);
                    histories.Add([.. messages.OfType<TextMessage>().Select(m => m.Text)]);
                    return Task.FromResult(
                        ToAsyncEnumerable([new TextMessage { Text = $"done-{models.Count}", Role = Role.Assistant }])
                    );
                }
            );

        var defaults = new GenerateReplyOptions { ModelId = "gpt-5.6-terra" };
        await using var loop = new MultiTurnAgentLoop(
            agent.Object,
            new FunctionRegistry(),
            threadId: "model-routing",
            defaultOptions: defaults,
            logger: Mock.Of<ILogger<MultiTurnAgentLoop>>()
        );
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        await DrainAsync(
            loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = "provisional", Role = Role.User }]), cts.Token)
        );
        await DrainAsync(
            loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "synthesize", Role = Role.User }], ModelId: "gpt-5.6-sol"),
                cts.Token
            )
        );
        await DrainAsync(
            loop.ExecuteRunAsync(new UserInput([new TextMessage { Text = "next", Role = Role.User }]), cts.Token)
        );

        models.Should().Equal("gpt-5.6-terra", "gpt-5.6-sol", "gpt-5.6-terra");
        histories[1].Should().ContainInOrder("provisional", "done-1", "synthesize");
        histories[1].Should().HaveCount(3, "same-thread synthesis retains the complete provisional history");
        defaults.ModelId.Should().Be("gpt-5.6-terra");
        await cts.CancelAsync();
    }

    private static async Task DrainAsync(IAsyncEnumerable<IMessage> messages)
    {
        await foreach (var _ in messages) { }
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
