using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
using LmMultiTurn.Tests.DualLayer;
using Xunit;

namespace LmMultiTurn.Tests;

public class AgentTextCollectorTests
{
    [Fact]
    public async Task CollectAsync_ThrowsWhenTheRunEndsInError_InsteadOfReturningAnEmptyAnswer()
    {
        // Every headless consumer (the review agents, the judge, the dual-layer executor) reads the
        // collected text as the agent's answer. A run that errored has none, and an empty string or
        // the partial text would be taken for one.
        var provider = new DualLayerAgentTests.ScriptedAgent(_ => throw new InvalidOperationException("boom"));
        await using var loop = new MultiTurnAgentLoop(
            provider,
            new FunctionRegistry(),
            "thread-collector",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false
        );
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = loop.RunAsync(cts.Token);

        var collect = () => AgentTextCollector.CollectAsync(loop, "hello", cts.Token);

        (await collect.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ended in error*boom*");
    }
}
